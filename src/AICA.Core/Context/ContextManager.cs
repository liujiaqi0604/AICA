using System;
using System.Collections.Generic;
using System.Linq;
using AICA.Core.LLM;

namespace AICA.Core.Context
{
    /// <summary>
    /// Manages conversation context within a token budget.
    /// Provides conversation history truncation (keep first + last, trim middle)
    /// and improved token estimation for mixed CJK/Latin text.
    /// </summary>
    public class ContextManager
    {
        private readonly int _maxTokenBudget;
        private readonly List<ContextItem> _items = new List<ContextItem>();

        public ContextManager(int maxTokenBudget = 32000)
        {
            _maxTokenBudget = maxTokenBudget;
        }

        /// <summary>
        /// Add a context item with a given priority
        /// </summary>
        public void AddItem(string key, string content, ContextPriority priority)
        {
            // Remove existing item with same key
            _items.RemoveAll(i => i.Key == key);
            _items.Add(new ContextItem
            {
                Key = key,
                Content = content,
                Priority = priority,
                EstimatedTokens = EstimateTokens(content),
                AddedAt = DateTime.UtcNow
            });
        }

        /// <summary>
        /// Get context items that fit within the token budget, ordered by priority
        /// </summary>
        public IReadOnlyList<ContextItem> GetContextWithinBudget()
        {
            var sorted = _items
                .OrderByDescending(i => (int)i.Priority)
                .ThenByDescending(i => i.AddedAt)
                .ToList();

            var result = new List<ContextItem>();
            int totalTokens = 0;

            foreach (var item in sorted)
            {
                if (totalTokens + item.EstimatedTokens <= _maxTokenBudget)
                {
                    result.Add(item);
                    totalTokens += item.EstimatedTokens;
                }
                else if (item.Priority == ContextPriority.Critical)
                {
                    // Critical items are always included, truncate if needed
                    var available = _maxTokenBudget - totalTokens;
                    if (available > 100)
                    {
                        var truncated = TruncateToTokens(item.Content, available);
                        result.Add(new ContextItem
                        {
                            Key = item.Key,
                            Content = truncated + "\n... (truncated)",
                            Priority = item.Priority,
                            EstimatedTokens = available,
                            AddedAt = item.AddedAt
                        });
                        totalTokens += available;
                    }
                }
            }

            return result;
        }

        /// <summary>
        /// Truncate conversation history to fit within a token budget.
        /// Strategy: keep the system message (index 0) and the first user message (index 1),
        /// keep the most recent N messages, and trim messages in between.
        /// A notice is inserted where messages were removed.
        /// </summary>
        /// <param name="messages">Full conversation history</param>
        /// <param name="maxTokens">Maximum total tokens for the conversation</param>
        /// <param name="keepRecentCount">Number of recent messages to always keep</param>
        /// <returns>Truncated conversation history</returns>
        public static List<ChatMessage> TruncateConversation(
            List<ChatMessage> messages,
            int maxTokens,
            int keepRecentCount = 10)
        {
            return TruncateConversationWithStats(messages, maxTokens, keepRecentCount).Messages;
        }

        /// <summary>
        /// Truncate conversation history and return token statistics to avoid redundant recalculation.
        /// Uses value-based scoring to prioritize removing low-value messages first.
        /// </summary>
        public static TruncateResult TruncateConversationWithStats(
            List<ChatMessage> messages,
            int maxTokens,
            int keepRecentCount = 10)
        {
            if (messages == null || messages.Count == 0)
                return new TruncateResult { Messages = messages, TotalTokens = 0, RemovedMessageCount = 0 };

            int totalTokens = messages.Sum(m => EstimateTokens(m.Content));

            // If within budget, return as-is
            if (totalTokens <= maxTokens)
                return new TruncateResult { Messages = messages, TotalTokens = totalTokens, RemovedMessageCount = 0 };

            // Determine which indices must be kept (head + tail)
            int preserveStart = Math.Min(2, messages.Count); // system + first user
            int preserveEnd = Math.Min(keepRecentCount, messages.Count - preserveStart);

            if (preserveStart + preserveEnd >= messages.Count)
            {
                // Not enough messages to trim from the middle; truncate individual long messages instead
                var truncated = TruncateLongMessages(messages, maxTokens);
                int truncatedTokens = truncated.Sum(m => EstimateTokens(m.Content));
                return new TruncateResult { Messages = truncated, TotalTokens = truncatedTokens, RemovedMessageCount = 0 };
            }

            var mustKeep = new HashSet<int>();
            for (int i = 0; i < preserveStart; i++) mustKeep.Add(i);
            for (int i = messages.Count - preserveEnd; i < messages.Count; i++) mustKeep.Add(i);

            // Score middle messages and remove lowest-value ones first
            var removable = new List<(int Index, int Tokens, int Score)>();
            for (int i = 0; i < messages.Count; i++)
            {
                if (mustKeep.Contains(i)) continue;
                removable.Add((i, EstimateTokens(messages[i].Content), ScoreMessage(messages[i])));
            }

            // Sort by score ascending — lowest value removed first
            removable.Sort((a, b) => a.Score.CompareTo(b.Score));

            int tokensToFree = totalTokens - maxTokens;
            var removedIndices = new HashSet<int>();
            foreach (var item in removable)
            {
                if (tokensToFree <= 0) break;
                removedIndices.Add(item.Index);
                tokensToFree -= item.Tokens;
            }

            // Build result preserving original order
            var result = new List<ChatMessage>();
            bool insertedNotice = false;
            for (int i = 0; i < messages.Count; i++)
            {
                if (removedIndices.Contains(i))
                {
                    if (!insertedNotice)
                    {
                        result.Add(ChatMessage.System(
                            $"[NOTE: {removedIndices.Count} earlier messages were removed to fit the context window. " +
                            "The conversation continues with the most recent messages below.]"));
                        insertedNotice = true;
                    }
                    continue;
                }
                result.Add(messages[i]);
            }

            // If still over budget, truncate individual long messages
            int resultTokens = result.Sum(m => EstimateTokens(m.Content));
            if (resultTokens > maxTokens)
            {
                result = TruncateLongMessages(result, maxTokens);
                resultTokens = result.Sum(m => EstimateTokens(m.Content));
            }

            return new TruncateResult { Messages = result, TotalTokens = resultTokens, RemovedMessageCount = removedIndices.Count };
        }

        /// <summary>
        /// Score a message's retention value. Higher = more worth keeping.
        /// </summary>
        private static int ScoreMessage(ChatMessage msg)
        {
            int score = 0;

            // User messages are most valuable (contain intent and decisions)
            if (msg.Role == ChatRole.User) score += 30;
            else if (msg.Role == ChatRole.Assistant) score += 20;
            else if (msg.Role == ChatRole.Tool) score += 10;

            // Real user messages (not system-injected corrections) are high value
            if (msg.Role == ChatRole.User && msg.Content != null &&
                !msg.Content.StartsWith("[System") && !msg.Content.StartsWith("⚠️"))
                score += 20;

            // Failed tool results are low value — the error has already been processed
            if (msg.Role == ChatRole.Tool && msg.Content != null &&
                msg.Content.StartsWith("Error:"))
                score -= 15;

            // System-injected correction messages have served their purpose
            if (msg.Role == ChatRole.User && msg.Content != null &&
                msg.Content.Contains("⚠️"))
                score -= 10;

            // Very short messages are usually confirmations, low info density
            if (msg.Content != null && msg.Content.Length < 50)
                score -= 5;

            // Messages with tool calls in assistant role carry structural info
            if (msg.Role == ChatRole.Assistant && msg.ToolCalls != null && msg.ToolCalls.Count > 0)
                score += 10;

            return score;
        }

        /// <summary>
        /// Truncate individual long messages (tool results, file contents) to fit budget.
        /// Preserves system and user messages, truncates assistant and tool messages.
        /// </summary>
        private static List<ChatMessage> TruncateLongMessages(List<ChatMessage> messages, int maxTokens)
        {
            var result = new List<ChatMessage>(messages);
            int totalTokens = result.Sum(m => EstimateTokens(m.Content));

            if (totalTokens <= maxTokens)
                return result;

            // Truncate from oldest non-system, non-user messages first
            for (int i = 1; i < result.Count && totalTokens > maxTokens; i++)
            {
                var msg = result[i];
                if (msg.Role == LLM.ChatRole.System) continue;

                int msgTokens = EstimateTokens(msg.Content);
                // Only truncate messages > 500 tokens
                if (msgTokens > 500)
                {
                    int targetTokens = Math.Max(200, msgTokens / 3);
                    var truncated = TruncateToTokens(msg.Content, targetTokens);
                    int saved = msgTokens - EstimateTokens(truncated);
                    msg.Content = truncated + "\n... (truncated to save context space)";
                    totalTokens -= saved;
                }
            }

            return result;
        }

        /// <summary>
        /// Get total estimated tokens of all items
        /// </summary>
        public int GetTotalEstimatedTokens()
        {
            return _items.Sum(i => i.EstimatedTokens);
        }

        /// <summary>
        /// Get remaining token budget
        /// </summary>
        public int GetRemainingBudget()
        {
            return Math.Max(0, _maxTokenBudget - GetContextWithinBudget().Sum(i => i.EstimatedTokens));
        }

        /// <summary>
        /// Remove a context item by key
        /// </summary>
        public bool Remove(string key)
        {
            return _items.RemoveAll(i => i.Key == key) > 0;
        }

        /// <summary>
        /// Clear all context items
        /// </summary>
        public void Clear()
        {
            _items.Clear();
        }

        /// <summary>
        /// Improved token estimation that accounts for CJK characters and code symbols.
        /// CJK characters typically use ~1.5 tokens each, code symbols ~0.5 tokens each,
        /// while Latin text averages ~4 chars/token.
        /// </summary>
        public static int EstimateTokens(string text)
        {
            if (string.IsNullOrEmpty(text)) return 0;

            int cjkCount = 0;
            int codeSymbolCount = 0;
            int otherCount = 0;

            foreach (char c in text)
            {
                if (IsCJK(c))
                    cjkCount++;
                else if (IsCodeSymbol(c))
                    codeSymbolCount++;
                else
                    otherCount++;
            }

            // CJK: ~1.5 tokens per char
            // Code symbols ({, }, [, ], etc.): ~0.5 tokens per char
            // Latin/other: ~0.25 tokens per char (4 chars/token)
            int tokens = (int)Math.Ceiling(cjkCount * 1.5 + codeSymbolCount * 0.5 + otherCount * 0.25);
            return Math.Max(1, tokens);
        }

        private static bool IsCJK(char c)
        {
            // CJK Unified Ideographs, CJK Extension A, Hangul, Hiragana, Katakana, full-width
            return (c >= 0x4E00 && c <= 0x9FFF)   // CJK Unified
                || (c >= 0x3400 && c <= 0x4DBF)    // CJK Extension A
                || (c >= 0xAC00 && c <= 0xD7AF)    // Hangul
                || (c >= 0x3040 && c <= 0x309F)     // Hiragana
                || (c >= 0x30A0 && c <= 0x30FF)     // Katakana
                || (c >= 0xFF00 && c <= 0xFFEF);    // Fullwidth
        }

        private static bool IsCodeSymbol(char c)
        {
            return c == '{' || c == '}' || c == '[' || c == ']' ||
                   c == '(' || c == ')' || c == '<' || c == '>' ||
                   c == '"' || c == '\'' || c == ':' || c == ';' ||
                   c == '=' || c == '+' || c == '-' || c == '*' ||
                   c == '/' || c == '\\' || c == '|' || c == '&' ||
                   c == '#' || c == '@' || c == '`' || c == '~';
        }

        /// <summary>
        /// Smart truncation for tool results. Adapts to remaining token budget and content type.
        /// </summary>
        /// <param name="content">Tool result content</param>
        /// <param name="toolName">Name of the tool that produced the result</param>
        /// <param name="remainingTokenBudget">Remaining token budget for the conversation</param>
        /// <returns>Truncated content, or original if within budget</returns>
        public static string SmartTruncateToolResult(string content, string toolName, int remainingTokenBudget)
        {
            if (string.IsNullOrEmpty(content)) return content;

            // Single tool result can use at most 30% of remaining budget, minimum 500 tokens
            int maxTokensForResult = Math.Max(500, (int)(remainingTokenBudget * 0.30));
            int estimatedTokens = EstimateTokens(content);

            if (estimatedTokens <= maxTokensForResult)
                return content;

            switch (toolName)
            {
                case "read_file":
                    return TruncateCodeContent(content, maxTokensForResult);
                case "grep_search":
                    return TruncateSearchResults(content, maxTokensForResult);
                default:
                    return TruncateToTokens(content, maxTokensForResult)
                           + $"\n... (truncated, showing ~{maxTokensForResult} tokens of ~{estimatedTokens})";
            }
        }

        /// <summary>
        /// Truncate code content preserving head and tail on line boundaries.
        /// </summary>
        private static string TruncateCodeContent(string content, int maxTokens)
        {
            var lines = content.Split('\n');
            int targetChars = (int)(maxTokens * 2.5);

            if (content.Length <= targetChars) return content;

            // Keep first 60% and last 20% of target chars
            int headBudget = (int)(targetChars * 0.6);
            int tailBudget = (int)(targetChars * 0.2);

            var headLines = new List<string>();
            int headChars = 0;
            foreach (var line in lines)
            {
                if (headChars + line.Length + 1 > headBudget) break;
                headLines.Add(line);
                headChars += line.Length + 1;
            }

            var tailLines = new List<string>();
            int tailChars = 0;
            for (int i = lines.Length - 1; i >= 0; i--)
            {
                if (tailChars + lines[i].Length + 1 > tailBudget) break;
                tailLines.Insert(0, lines[i]);
                tailChars += lines[i].Length + 1;
            }

            int omitted = lines.Length - headLines.Count - tailLines.Count;
            var sb = new System.Text.StringBuilder();
            foreach (var line in headLines) sb.AppendLine(line);
            sb.AppendLine($"\n... ({omitted} lines omitted) ...\n");
            foreach (var line in tailLines) sb.AppendLine(line);

            return sb.ToString();
        }

        /// <summary>
        /// Truncate search results keeping complete match entries.
        /// </summary>
        private static string TruncateSearchResults(string content, int maxTokens)
        {
            var lines = content.Split('\n');
            int targetChars = (int)(maxTokens * 2.5);

            if (content.Length <= targetChars) return content;

            var sb = new System.Text.StringBuilder();
            int charCount = 0;
            int lineCount = 0;

            foreach (var line in lines)
            {
                if (charCount + line.Length + 1 > targetChars) break;
                sb.AppendLine(line);
                charCount += line.Length + 1;
                lineCount++;
            }

            if (lineCount < lines.Length)
            {
                sb.AppendLine($"\n... ({lines.Length - lineCount} more lines, truncated to fit context)");
            }

            return sb.ToString();
        }

        private static string TruncateToTokens(string text, int targetTokens)
        {
            if (string.IsNullOrEmpty(text)) return text;
            // Use a conservative estimate for truncation: ~2.5 chars per token average
            int targetChars = (int)(targetTokens * 2.5);
            if (text.Length <= targetChars) return text;
            return text.Substring(0, targetChars);
        }
    }

    /// <summary>
    /// Result of conversation truncation, includes token statistics to avoid redundant recalculation.
    /// </summary>
    public class TruncateResult
    {
        public List<ChatMessage> Messages { get; set; }
        public int TotalTokens { get; set; }
        public int RemovedMessageCount { get; set; }
    }

    public class ContextItem
    {
        public string Key { get; set; }
        public string Content { get; set; }
        public ContextPriority Priority { get; set; }
        public int EstimatedTokens { get; set; }
        public DateTime AddedAt { get; set; }
    }

    public enum ContextPriority
    {
        Low = 0,
        Normal = 1,
        High = 2,
        Critical = 3
    }
}

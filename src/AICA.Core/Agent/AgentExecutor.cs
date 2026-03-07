using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using AICA.Core.Context;
using AICA.Core.LLM;
using AICA.Core.Prompt;
using Microsoft.Extensions.Logging;

namespace AICA.Core.Agent
{
    /// <summary>
    /// Core Agent executor that manages the Agent loop
    /// </summary>
    public class AgentExecutor : IAgentExecutor
    {
        private readonly ILLMClient _llmClient;
        private readonly ToolDispatcher _toolDispatcher;
        private readonly ILogger<AgentExecutor> _logger;
        private readonly int _maxIterations;
        private readonly int _maxTokenBudget;
        private readonly string _customInstructions;
        private TaskState _taskState;

        /// <summary>
        /// Current task state (readable for UI layer)
        /// </summary>
        public TaskState CurrentTaskState => _taskState;

        public AgentExecutor(
            ILLMClient llmClient,
            ToolDispatcher toolDispatcher,
            ILogger<AgentExecutor> logger = null,
            int maxIterations = 25,
            int maxTokenBudget = 32000,
            string customInstructions = null)
        {
            _llmClient = llmClient ?? throw new ArgumentNullException(nameof(llmClient));
            _toolDispatcher = toolDispatcher ?? throw new ArgumentNullException(nameof(toolDispatcher));
            _logger = logger;
            _maxIterations = maxIterations;
            _maxTokenBudget = maxTokenBudget;
            _customInstructions = customInstructions;
        }

        /// <summary>
        /// Execute a user request through the Agent loop
        /// </summary>
        /// <param name="userRequest">The current user message</param>
        /// <param name="context">Agent context for workspace operations</param>
        /// <param name="uiContext">UI context for user interactions</param>
        /// <param name="previousMessages">Previous conversation history (excluding system prompt)</param>
        /// <param name="ct">Cancellation token</param>
        public async IAsyncEnumerable<AgentStep> ExecuteAsync(
            string userRequest,
            IAgentContext context,
            IUIContext uiContext,
            List<ChatMessage> previousMessages = null,
            [EnumeratorCancellation] CancellationToken ct = default)
        {
            // Build system prompt with tool definitions
            var toolDefinitions = _toolDispatcher.GetToolDefinitions();
            var systemPrompt = SystemPromptBuilder.GetDefaultPrompt(
                context?.WorkingDirectory ?? Environment.CurrentDirectory,
                toolDefinitions,
                _customInstructions,
                context?.SourceRoots);

            // Build conversation history: system prompt + previous messages
            var conversationHistory = new List<ChatMessage>();

            // Always start with system prompt
            conversationHistory.Add(ChatMessage.System(systemPrompt));

            // Add previous conversation history (if any)
            // Note: previousMessages should already include the current user message
            if (previousMessages != null && previousMessages.Count > 0)
            {
                // Filter out old system prompts from previous messages
                var filteredPrevious = previousMessages
                    .Where(m => m.Role != ChatRole.System)
                    .ToList();
                conversationHistory.AddRange(filteredPrevious);
            }
            else
            {
                // If no previous messages, add the current user request
                conversationHistory.Add(ChatMessage.User(userRequest));
            }

            _taskState = new TaskState { MaxConsecutiveMistakes = 3 };

            // Track tool call signatures to detect duplicates
            var executedToolSignatures = new HashSet<string>(StringComparer.Ordinal);

            while (_taskState.Iteration < _maxIterations && !ct.IsCancellationRequested && !_taskState.Abort && !_taskState.IsCompleted)
            {
                _taskState.Iteration++;
                _taskState.ApiRequestCount++;
                var iteration = _taskState.Iteration;
                _logger?.LogDebug("Agent iteration {Iteration}", iteration);
                System.Diagnostics.Debug.WriteLine($"[AICA] Agent iteration {iteration}");

                // Truncate conversation history if it exceeds token budget
                // Reserve ~15% of budget for system prompt, use 85% for conversation
                int conversationBudget = (int)(_maxTokenBudget * 0.85);
                if (conversationBudget > 0)
                {
                    conversationHistory = ContextManager.TruncateConversation(
                        conversationHistory, conversationBudget);
                }

                // ── Force-completion: when enough tools have been called, remove tools to force text-only summary ──
                int currentTokens = conversationHistory.Sum(m => Context.ContextManager.EstimateTokens(m.Content));
                double tokenUsageRatio = (double)currentTokens / conversationBudget;
                // Primary trigger: absolute tool call count >=8 (most reliable)
                // Backup triggers: iteration>=6 with tools used, token usage, near max iterations
                bool forceCompletion = _taskState.HasEverUsedTools &&
                    (_taskState.TotalToolCallCount >= 8 ||
                     _taskState.Iteration >= 6 ||
                     tokenUsageRatio > 0.60 ||
                     _taskState.Iteration >= _maxIterations - 2);
                System.Diagnostics.Debug.WriteLine($"[AICA] forceCompletion check: TotalToolCalls={_taskState.TotalToolCallCount}, Iteration={_taskState.Iteration}, tokenRatio={tokenUsageRatio:F2}, result={forceCompletion}");

                if (forceCompletion)
                {
                    string reason = _taskState.TotalToolCallCount >= 10
                        ? $"{_taskState.TotalToolCallCount} tool calls executed"
                        : tokenUsageRatio > 0.65
                            ? $"context window {tokenUsageRatio:P0} full"
                            : $"iteration {_taskState.Iteration}/{_maxIterations}";
                    System.Diagnostics.Debug.WriteLine($"[AICA] Force-completion triggered: {reason}");

                    bool alreadySummarizing = conversationHistory.Any(m =>
                        m.Role == LLM.ChatRole.System && m.Content != null &&
                        m.Content.Contains("[SUMMARIZE]"));

                    if (!alreadySummarizing)
                    {
                        // Visible diagnostic so user can confirm force-completion is active
                        yield return AgentStep.TextChunk($"\n\nℹ️ *正在整理分析结果 ({reason})...*\n\n");

                        conversationHistory.Add(ChatMessage.System(
                            $"[SUMMARIZE] Resources are running low ({reason}). " +
                            "You MUST now generate a comprehensive final answer based on ALL the information " +
                            "you have gathered so far. Synthesize your findings into a clear, well-structured response. " +
                            "Do NOT call any more tools."));
                    }
                }

                // Get LLM response with auto-retry
                string assistantResponse = null;
                var toolCalls = new List<ToolCall>();
                var pendingTextChunks = new List<string>();
                string streamError = null;
                bool wasCancelled = false;
                bool wasTruncated = false;
                const int maxRetries = 2;
                int[] retryDelaysMs = { 1000, 3000 };

                // Retry state — yield return is forbidden inside catch, so use flags
                string retryNotice = null;
                bool shouldRetry = false;
                int retryDelayMs = 0;

                for (int attempt = 0; attempt <= maxRetries; attempt++)
                {
                    // Reset per-attempt state
                    assistantResponse = null;
                    toolCalls.Clear();
                    pendingTextChunks.Clear();
                    streamError = null;
                    wasCancelled = false;
                    wasTruncated = false;
                    retryNotice = null;
                    shouldRetry = false;

                    try
                    {
                        // When forceCompletion is active, pass null tools to prevent LLM from making tool calls
                        var effectiveTools = forceCompletion ? null : toolDefinitions;
                        await foreach (var chunk in _llmClient.StreamChatAsync(conversationHistory, effectiveTools, ct).ConfigureAwait(false))
                        {
                            if (chunk.Type == LLMChunkType.Text)
                            {
                                assistantResponse = (assistantResponse ?? string.Empty) + chunk.Text;
                                pendingTextChunks.Add(chunk.Text);
                            }
                            else if (chunk.Type == LLMChunkType.ToolCall)
                            {
                                toolCalls.Add(chunk.ToolCall);
                            }
                            else if (chunk.Type == LLMChunkType.Done
                                && (chunk.FinishReason == "length" || chunk.FinishReason == "max_tokens"))
                            {
                                wasTruncated = true;
                                System.Diagnostics.Debug.WriteLine($"[AICA] Detected truncation: finish_reason={chunk.FinishReason}");
                            }
                        }
                        break; // success — exit retry loop
                    }
                    catch (OperationCanceledException)
                    {
                        wasCancelled = true;
                        break; // never retry cancellation
                    }
                    catch (LLM.LLMException llmEx) when (llmEx.IsContextExceeded)
                    {
                        System.Diagnostics.Debug.WriteLine($"[AICA] Context window exceeded, truncating history (attempt {attempt + 1})");
                        int reducedBudget = conversationBudget / 2;
                        conversationHistory = ContextManager.TruncateConversation(conversationHistory, reducedBudget);

                        if (attempt < maxRetries)
                        {
                            retryNotice = "\n⚠️ 上下文窗口已满，正在自动裁剪历史对话...\n";
                            shouldRetry = true;
                        }
                        else
                        {
                            streamError = "上下文窗口超出限制，自动裁剪后仍无法发送请求。请开始新对话。";
                        }
                    }
                    catch (LLM.LLMException llmEx) when (llmEx.IsTransient && attempt < maxRetries)
                    {
                        System.Diagnostics.Debug.WriteLine($"[AICA] Transient LLM error (attempt {attempt + 1}): {llmEx.Message}");
                        retryNotice = $"\n⏳ 请求失败，正在重试 ({attempt + 1}/{maxRetries})...\n";
                        retryDelayMs = retryDelaysMs[attempt];
                        shouldRetry = true;
                    }
                    catch (Exception ex) when (attempt < maxRetries && IsTransientException(ex))
                    {
                        System.Diagnostics.Debug.WriteLine($"[AICA] Transient error (attempt {attempt + 1}): {ex.Message}");
                        retryNotice = $"\n⏳ 网络错误，正在重试 ({attempt + 1}/{maxRetries})...\n";
                        retryDelayMs = retryDelaysMs[attempt];
                        shouldRetry = true;
                    }
                    catch (Exception ex)
                    {
                        System.Diagnostics.Debug.WriteLine($"[AICA] LLM stream error: {ex.Message}");
                        streamError = ex.Message;
                        break;
                    }

                    // Yield retry notice outside catch block (C# forbids yield in catch)
                    if (retryNotice != null)
                        yield return AgentStep.TextChunk(retryNotice);

                    if (shouldRetry)
                    {
                        if (retryDelayMs > 0)
                            await Task.Delay(retryDelayMs, ct).ConfigureAwait(false);
                        continue;
                    }
                    break;
                }

                // Handle errors first
                if (wasCancelled)
                {
                    yield return AgentStep.WithError("Operation cancelled.");
                    yield break;
                }
                if (streamError != null)
                {
                    yield return AgentStep.WithError($"LLM communication error: {streamError}");
                    yield break;
                }

                // ── Resolve ALL tool calls (API + text-based) BEFORE any decisions ──
                // Skip text-based parsing in force-completion mode (LLM should only produce text)
                if (!forceCompletion && toolCalls.Count == 0 && !string.IsNullOrEmpty(assistantResponse))
                {
                    var parsedCalls = TryParseTextToolCalls(assistantResponse);
                    if (parsedCalls.Count > 0)
                    {
                        System.Diagnostics.Debug.WriteLine($"[AICA] Parsed {parsedCalls.Count} text-based tool call(s) from response");
                        toolCalls.AddRange(parsedCalls);

                        // Remove the text-based tool call syntax from assistantResponse to avoid leaking it
                        assistantResponse = RemoveTextToolCallSyntax(assistantResponse);
                    }
                }

                bool hasToolCalls = toolCalls.Count > 0;

                // ── GUARD FIRST: conversational messages yield text normally and exit ──
                // Must run BEFORE suppression to avoid replacing greetings with indicators.
                if (_taskState.Iteration == 1 && IsLikelyConversational(userRequest))
                {
                    System.Diagnostics.Debug.WriteLine($"[AICA] Conversational message on iteration 1, yielding text and completing (toolCalls={toolCalls.Count})");
                    foreach (var text in pendingTextChunks)
                        yield return AgentStep.TextChunk(text);

                    var convMsg = ChatMessage.Assistant(assistantResponse ?? string.Empty);
                    conversationHistory.Add(convMsg);
                    yield return AgentStep.Complete(assistantResponse);
                    yield break;
                }

                // ── Hallucination suppression ──
                // If the model is going to call tools, pre-tool text is often planning/thinking text
                // rather than user-facing content.
                // For attempt_completion, suppress ALL pre-tool text because the final user-facing
                // response should come from the completion summary, not internal tool-decision text.
                // For other tools, only suppress if the text looks like internal reasoning.
                bool hasAttemptCompletion = toolCalls.Any(tc => tc.Name == "attempt_completion");
                bool suppressText = false;

                if (hasToolCalls && !string.IsNullOrWhiteSpace(assistantResponse))
                {
                    // Always suppress pre-tool text for attempt_completion
                    if (hasAttemptCompletion)
                    {
                        suppressText = true;
                    }
                    // For other tools, check if the text looks like internal reasoning
                    else
                    {
                        var lowerResponse = assistantResponse.ToLowerInvariant();

                        // Patterns indicating internal reasoning/planning (not user-facing content)
                        var reasoningPatterns = new[]
                        {
                            "i need to", "i should", "i will", "i'm going to", "let me",
                            "我需要", "我应该", "我将", "让我",
                            "first, i", "next, i", "then, i",
                            "首先", "接下来", "然后"
                        };

                        // If text is short and starts with reasoning patterns, it's likely planning text
                        if (assistantResponse.Length < 200 && reasoningPatterns.Any(p => lowerResponse.StartsWith(p)))
                        {
                            suppressText = true;
                        }
                    }
                }

                // Additional filter: suppress common meta-reasoning patterns that leak internal thinking
                if (!suppressText && !string.IsNullOrWhiteSpace(assistantResponse))
                {
                    var lowerResponse = assistantResponse.ToLowerInvariant();
                    var metaPatterns = new[]
                    {
                        "the user is asking", "the user wants", "the user is requesting",
                        "looking at the instructions", "actually,", "wait,", "let me check",
                        "i think the user", "let me re-read",
                        "oh wait", "i see -", "this might be"
                    };

                    if (metaPatterns.Any(p => lowerResponse.Contains(p)))
                    {
                        System.Diagnostics.Debug.WriteLine(
                            $"[AICA] Suppressing meta-reasoning text ({assistantResponse?.Length ?? 0} chars)");
                        suppressText = true;
                    }
                }

                if (suppressText)
                {
                    System.Diagnostics.Debug.WriteLine(
                        $"[AICA] Suppressing pre-tool text ({assistantResponse?.Length ?? 0} chars)");
                    assistantResponse = null;
                    // Do NOT yield any text — tool results will follow
                }
                else
                {
                    foreach (var text in pendingTextChunks)
                    {
                        yield return AgentStep.TextChunk(text);
                    }
                }

                // Build assistant message for conversation history
                var assistantMsg = ChatMessage.Assistant(assistantResponse ?? string.Empty);
                if (toolCalls.Count > 0)
                {
                    assistantMsg.ToolCalls = new System.Collections.Generic.List<ToolCallMessage>();
                    foreach (var tc in toolCalls)
                    {
                        assistantMsg.ToolCalls.Add(new ToolCallMessage
                        {
                            Id = tc.Id,
                            Type = "function",
                            Function = new FunctionCall
                            {
                                Name = tc.Name,
                                Arguments = tc.Arguments != null 
                                    ? System.Text.Json.JsonSerializer.Serialize(tc.Arguments) 
                                    : "{}"
                            }
                        });
                    }
                }
                conversationHistory.Add(assistantMsg);

                // ── No tool calls at all ──
                if (toolCalls.Count == 0)
                {
                    System.Diagnostics.Debug.WriteLine($"[AICA] No tool calls. Iteration={_taskState.Iteration}, wasTruncated={wasTruncated}, forceCompletion={forceCompletion}");

                    // Force-completion mode: text-only response IS the final answer
                    if (forceCompletion && !string.IsNullOrWhiteSpace(assistantResponse))
                    {
                        System.Diagnostics.Debug.WriteLine($"[AICA] Force-completion: yielding text response as Complete ({assistantResponse.Length} chars)");
                        // Note: pendingTextChunks were already yielded in the suppression block above,
                        // so we only yield the Complete step here (not the text chunks again).
                        yield return AgentStep.Complete(assistantResponse);
                        yield break;
                    }

                    if (wasTruncated)
                    {
                        conversationHistory.Add(ChatMessage.User("[System: Your response was cut off due to token limit. Please continue from where you left off.]"));
                        continue;
                    }

                    // ── Hallucination detection ──
                    // Detect if the model claims to have executed a tool but didn't actually call it
                    if (!string.IsNullOrWhiteSpace(assistantResponse) && DetectToolExecutionClaim(assistantResponse))
                    {
                        System.Diagnostics.Debug.WriteLine($"[AICA] Detected tool execution hallucination - model claimed to execute a tool but didn't call it");

                        // Yield the hallucinated text first (so user can see what happened)
                        // Already yielded via pendingTextChunks above

                        // Add a strong correction message
                        conversationHistory.Add(ChatMessage.User(
                            "⚠️ CRITICAL ERROR: You described executing a tool but did NOT actually call it. " +
                            "You MUST use the proper tool calling format to execute tools. " +
                            "Do NOT just describe what you would do - ACTUALLY CALL THE TOOL using the function calling mechanism. " +
                            "Please retry the operation by making the actual tool call now."));

                        System.Diagnostics.Debug.WriteLine($"[AICA] Added hallucination correction message");
                        continue;
                    }

                    // First iteration with no tools: genuine text response
                    // (knowledge questions, explanations, conversational replies)
                    if (_taskState.Iteration == 1)
                    {
                        yield return AgentStep.Complete(assistantResponse);
                        yield break;
                    }

                    // Nudge LLM to use tools
                    bool tooManyNoTool = _taskState.RecordNoToolsUsed();
                    if (tooManyNoTool)
                    {
                        yield return AgentStep.Complete(assistantResponse);
                        yield break;
                    }

                    conversationHistory.Add(ChatMessage.User(
                        "You did not use any tools in your response. " +
                        "If you have completed the task (created/edited files or finished the work), you MUST call the `attempt_completion` tool now. " +
                        "If the task is not complete, continue using tools to finish it. " +
                        "Do NOT just write a summary - call the appropriate tool."));
                    System.Diagnostics.Debug.WriteLine($"[AICA] No tools, nudging to use attempt_completion (count={_taskState.ConsecutiveNoToolCount})");
                    continue;
                }

                // Tools were used, reset no-tool counter and mark that tools have been used
                _taskState.ResetNoToolCount();
                _taskState.HasEverUsedTools = true;

                // ── Parameter augmentation ──
                // Auto-add missing parameters when user intent is clear but LLM omitted them
                AugmentToolCallParameters(toolCalls, userRequest);

                System.Diagnostics.Debug.WriteLine($"[AICA] Executing {toolCalls.Count} tool calls");

                // Execute tool calls
                foreach (var toolCall in toolCalls)
                {
                    // ── Duplicate tool call detection ──
                    // Skip tools that were already called with identical arguments
                    // (exempt attempt_completion, condense, and read_file after edit)
                    var toolSignature = GetToolCallSignature(toolCall);

                    // Allow read_file to be called again if a file was edited since last read
                    bool allowDuplicate = false;
                    if (toolCall.Name == "read_file" && _taskState.DidEditFile)
                    {
                        // Check if this read_file is for a file that might have been edited
                        allowDuplicate = true;
                    }

                    if (toolCall.Name != "attempt_completion" && toolCall.Name != "condense"
                        && !allowDuplicate
                        && !executedToolSignatures.Add(toolSignature))
                    {
                        System.Diagnostics.Debug.WriteLine($"[AICA] Skipping duplicate tool call: {toolCall.Name}");
                        _taskState.TotalToolCallCount++; // Count duplicates too for force-completion
                        var dupResult = ToolResult.Fail(
                            $"Duplicate call detected: You already called {toolCall.Name} with the same arguments earlier in this conversation.\n\n" +
                            $"What to do:\n" +
                            $"1. Use the previous result from your conversation history\n" +
                            $"2. If you need updated information, add/change a parameter (e.g., different offset/limit for read_file)\n" +
                            $"3. If the file was modified, the system will allow re-reading automatically\n\n" +
                            $"This check prevents unnecessary duplicate operations and improves efficiency.");
                        yield return AgentStep.ToolStart(toolCall);
                        yield return AgentStep.WithToolResult(toolCall, dupResult);
                        conversationHistory.Add(ChatMessage.ToolResult(toolCall.Id, $"Error: {dupResult.Error}"));
                        continue;
                    }

                    yield return AgentStep.ToolStart(toolCall);

                    _taskState.LastToolName = toolCall.Name;
                    _taskState.TotalToolCallCount++;

                    ToolResult result;
                    try
                    {
                        // Use ConfigureAwait(false) to avoid deadlock when resumed on UI thread
                        using (var toolCts = CancellationTokenSource.CreateLinkedTokenSource(ct))
                        {
                            toolCts.CancelAfter(TimeSpan.FromSeconds(60)); // 60s timeout per tool
                            result = await _toolDispatcher.ExecuteAsync(toolCall, context, uiContext, toolCts.Token).ConfigureAwait(false);
                        }
                    }
                    catch (Exception ex)
                    {
                        System.Diagnostics.Debug.WriteLine($"[AICA] Tool {toolCall.Name} exception: {ex.Message}");
                        result = ToolResult.Fail($"Tool execution error: {ex.Message}");
                    }

                    // Track mistakes vs successes
                    if (result.Success)
                    {
                        _taskState.ResetMistakeCount();
                        // Track file edits
                        if (toolCall.Name == "edit" || toolCall.Name == "write_to_file")
                            _taskState.DidEditFile = true;
                    }
                    else
                    {
                        if (_taskState.IncrementMistakeCount())
                        {
                            System.Diagnostics.Debug.WriteLine($"[AICA] Consecutive mistake threshold reached ({_taskState.ConsecutiveMistakeCount})");
                        }
                    }

                    yield return AgentStep.WithToolResult(toolCall, result);

                    // Handle attempt_completion result — ends the agent loop
                    if (toolCall.Name == "attempt_completion" && result.Success
                        && result.Content != null && result.Content.StartsWith("TASK_COMPLETED:"))
                    {
                        _taskState.IsCompleted = true;
                        // Pass the full serialized result to the UI layer for parsing
                        yield return AgentStep.Complete(result.Content);
                        yield break;
                    }

                    // Handle condense result — replace middle of conversation with summary
                    if (toolCall.Name == "condense" && result.Success
                        && result.Content != null && result.Content.StartsWith("CONDENSE:"))
                    {
                        var summary = result.Content.Substring("CONDENSE:".Length);
                        System.Diagnostics.Debug.WriteLine($"[AICA] Condensing conversation with summary ({summary.Length} chars)");

                        // Keep system prompt (index 0) and first user message (index 1),
                        // replace everything else with the summary
                        var condensed = new List<ChatMessage>();
                        if (conversationHistory.Count > 0)
                            condensed.Add(conversationHistory[0]); // system
                        if (conversationHistory.Count > 1)
                            condensed.Add(conversationHistory[1]); // first user request

                        condensed.Add(ChatMessage.System(
                            "[Conversation condensed] The following is a summary of all previous work:\n" + summary));

                        conversationHistory = condensed;
                        yield return AgentStep.TextChunk("\n\n📝 *Conversation condensed to save context space.*\n\n");
                        continue; // restart the loop with condensed history
                    }

                    // Add tool result to conversation
                    var resultContent = result.Success ? result.Content : $"Error: {result.Error}";
                    // Truncate very long results to avoid token overflow
                    if (resultContent != null && resultContent.Length > 4000)
                    {
                        resultContent = resultContent.Substring(0, 4000) + "\n... (truncated, total length: " + resultContent.Length + " chars)";
                    }
                    conversationHistory.Add(ChatMessage.ToolResult(toolCall.Id, resultContent));

                    // If the user explicitly rejected an edit, stop retrying the same edit path.
                    // Treat this as valid user feedback, not as an execution error.
                    if (toolCall.Name == "edit" && result.Success &&
                        result.Content != null && result.Content.Contains("EDIT CANCELLED BY USER - NO CHANGES WERE APPLIED"))
                    {
                        _taskState.DidRejectTool = true;
                    }
                }

                // ── Check if task should be completed ──
                // If files were edited/created but attempt_completion was not called, remind the AI
                bool hadFileOperations = toolCalls.Any(tc => tc.Name == "write_to_file" || tc.Name == "edit");
                bool hadAttemptCompletion = toolCalls.Any(tc => tc.Name == "attempt_completion");

                if (hadFileOperations && !hadAttemptCompletion)
                {
                    System.Diagnostics.Debug.WriteLine("[AICA] File operations detected but no attempt_completion. Will remind AI in next iteration.");
                    // Don't add reminder immediately - let AI respond first, then check if it calls attempt_completion
                }

                // After tool execution, if the original response was truncated (finish_reason=length),
                // the LLM's text was cut off mid-sentence. Ask it to continue its output.
                if (wasTruncated)
                {
                    System.Diagnostics.Debug.WriteLine("[AICA] Response was truncated after tool calls, asking LLM to continue its output");
                    conversationHistory.Add(ChatMessage.User("[System: Your previous text response was cut off due to token limit. The tool calls have been executed and their results are above. Please continue your response from where it was cut off.]"));
                }

                // Check if consecutive mistake threshold was reached this iteration
                if (_taskState.ConsecutiveMistakeCount >= _taskState.MaxConsecutiveMistakes)
                {
                    yield return AgentStep.WithError(
                        $"The Agent has encountered {_taskState.ConsecutiveMistakeCount} consecutive errors. " +
                        "This may indicate a problem with the current approach. Consider providing guidance.");
                    yield break;
                }
            }

            if (_taskState.Iteration >= _maxIterations)
            {
                yield return AgentStep.WithError($"Maximum iterations ({_maxIterations}) reached. The Agent may be stuck in a loop.");
            }
        }

        /// <summary>
        /// Fallback parser for text-based tool calls that some models output.
        /// Supports formats like:
        ///   <function=tool_name> <parameter=key> value </tool_call>
        ///   <tool_call> {"name": "tool_name", "arguments": {...}} </tool_call>
        /// </summary>
        /// <summary>
        /// Auto-add missing parameters when user intent is clear but LLM omitted them.
        /// For example, auto-add recursive=true to list_dir when user asks for "完整结构".
        /// </summary>
        private void AugmentToolCallParameters(List<ToolCall> toolCalls, string userRequest)
        {
            if (string.IsNullOrEmpty(userRequest)) return;

            var lower = userRequest.ToLowerInvariant();

            foreach (var tc in toolCalls)
            {
                // list_dir: auto-add recursive=true when user asks for full structure
                if (tc.Name == "list_dir" && tc.Arguments != null)
                {
                    bool hasRecursive = tc.Arguments.ContainsKey("recursive");
                    if (!hasRecursive)
                    {
                        var recursiveKeywords = new[] { "完整", "全部", "递归", "目录树", "结构", "树形", "所有",
                            "full", "complete", "recursive", "tree", "entire", "all" };

                        foreach (var kw in recursiveKeywords)
                        {
                            if (lower.Contains(kw))
                            {
                                tc.Arguments["recursive"] = "true";
                                System.Diagnostics.Debug.WriteLine($"[AICA] Auto-augmented list_dir with recursive=true (matched '{kw}')");
                                break;
                            }
                        }
                    }
                }
            }
        }

        private List<ToolCall> TryParseTextToolCalls(string text)
        {
            var result = new List<ToolCall>();

            // Pattern 1: <function=NAME> <parameter=KEY> VALUE ... </tool_call>
            var funcPattern = new Regex(
                @"<function=(\w+)>(.*?)</tool_call>",
                RegexOptions.Singleline | RegexOptions.IgnoreCase);

            foreach (Match match in funcPattern.Matches(text))
            {
                var funcName = match.Groups[1].Value.Trim();
                var body = match.Groups[2].Value.Trim();

                var args = new Dictionary<string, object>();
                var paramPattern = new Regex(
                    @"<parameter=(\w+)>\s*(.*?)(?=<parameter=|$)",
                    RegexOptions.Singleline);

                foreach (Match paramMatch in paramPattern.Matches(body))
                {
                    var key = paramMatch.Groups[1].Value.Trim();
                    var value = SanitizeParameterValue(paramMatch.Groups[2].Value);
                    args[key] = value;
                }

                if (!string.IsNullOrEmpty(funcName) && args.Count > 0)
                {
                    result.Add(new ToolCall
                    {
                        Id = "text_" + Guid.NewGuid().ToString("N").Substring(0, 8),
                        Name = funcName,
                        Arguments = args
                    });
                }
            }

            // Pattern 2: minimax:tool_call format
            // MiniMax model outputs tool calls without explicit tool name, need to infer from context
            // Format: minimax:tool_call\n\n<content>\n\n</minimax:tool_call>
            if (result.Count == 0)
            {
                var minimaxPattern = new Regex(
                    @"minimax:tool_call\s+(.*?)\s*</minimax:tool_call>",
                    RegexOptions.Singleline | RegexOptions.IgnoreCase);

                foreach (Match match in minimaxPattern.Matches(text))
                {
                    var content = match.Groups[1].Value.Trim();

                    // Skip empty matches
                    if (string.IsNullOrWhiteSpace(content))
                    {
                        System.Diagnostics.Debug.WriteLine("[AICA] Skipping empty minimax:tool_call match");
                        continue;
                    }

                    var lines = content.Split(new[] { '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries);

                    if (lines.Length == 0)
                    {
                        System.Diagnostics.Debug.WriteLine("[AICA] No lines found in minimax:tool_call content");
                        continue;
                    }

                    // Infer tool name from content
                    string toolName = null;
                    var args = new Dictionary<string, object>();

                    var firstLine = lines[0].Trim();
                    System.Diagnostics.Debug.WriteLine($"[AICA] Parsing minimax:tool_call with first line: {firstLine}");

                    // Check if first line looks like a command
                    if (firstLine.StartsWith("dotnet ") || firstLine.StartsWith("msbuild ") ||
                        firstLine.StartsWith("git ") || firstLine.StartsWith("npm ") ||
                        firstLine.StartsWith("make ") || firstLine.Contains(".exe") ||
                        firstLine.Contains("&&") || firstLine.Contains("|"))
                    {
                        toolName = "run_command";
                        args["command"] = firstLine;
                        if (lines.Length > 1 && int.TryParse(lines[1].Trim(), out var timeout))
                        {
                            args["timeout"] = timeout;
                        }
                        System.Diagnostics.Debug.WriteLine($"[AICA] Inferred tool: run_command");
                    }
                    // Check if it looks like a file path
                    else if (firstLine.Contains("/") || firstLine.Contains("\\") || firstLine.Contains("."))
                    {
                        // Could be read_file, list_dir, or find_by_name
                        // Default to read_file if it looks like a file
                        if (firstLine.EndsWith(".cs") || firstLine.EndsWith(".txt") ||
                            firstLine.EndsWith(".json") || firstLine.EndsWith(".xml") ||
                            firstLine.EndsWith(".md") || firstLine.EndsWith(".cpp") ||
                            firstLine.EndsWith(".h") || firstLine.EndsWith(".py") ||
                            firstLine.EndsWith(".js") || firstLine.EndsWith(".ts") ||
                            firstLine.EndsWith(".java") || firstLine.EndsWith(".go"))
                        {
                            toolName = "read_file";
                            args["path"] = firstLine;
                            if (lines.Length > 1 && int.TryParse(lines[1].Trim(), out var offset))
                            {
                                args["offset"] = offset;
                            }
                            if (lines.Length > 2 && int.TryParse(lines[2].Trim(), out var limit))
                            {
                                args["limit"] = limit;
                            }
                            System.Diagnostics.Debug.WriteLine($"[AICA] Inferred tool: read_file, path: {firstLine}");
                        }
                        else
                        {
                            toolName = "list_dir";
                            args["path"] = firstLine;
                            System.Diagnostics.Debug.WriteLine($"[AICA] Inferred tool: list_dir");
                        }
                    }
                    // Otherwise treat as grep_search query
                    else
                    {
                        toolName = "grep_search";
                        args["query"] = firstLine;
                        if (lines.Length > 1) args["path"] = lines[1].Trim();
                        if (lines.Length > 2) args["includes"] = lines[2].Trim();
                        System.Diagnostics.Debug.WriteLine($"[AICA] Inferred tool: grep_search");
                    }

                    if (!string.IsNullOrEmpty(toolName) && args.Count > 0)
                    {
                        System.Diagnostics.Debug.WriteLine($"[AICA] Successfully parsed minimax:tool_call as {toolName}");
                        result.Add(new ToolCall
                        {
                            Id = "text_" + Guid.NewGuid().ToString("N").Substring(0, 8),
                            Name = toolName,
                            Arguments = args
                        });
                    }
                    else
                    {
                        System.Diagnostics.Debug.WriteLine($"[AICA] Failed to parse minimax:tool_call - toolName: {toolName}, args count: {args.Count}");
                    }
                }
            }

            // Pattern 3: JSON-style tool calls in text
            if (result.Count == 0)
            {
                var jsonPattern = new Regex(
                    @"\{[^{}]*""name""\s*:\s*""(\w+)""[^{}]*""arguments""\s*:\s*(\{[^}]*\})[^{}]*\}",
                    RegexOptions.Singleline);

                foreach (Match match in jsonPattern.Matches(text))
                {
                    try
                    {
                        var name = match.Groups[1].Value;
                        var argsJson = match.Groups[2].Value;
                        var args = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, object>>(argsJson);

                        if (!string.IsNullOrEmpty(name))
                        {
                            result.Add(new ToolCall
                            {
                                Id = "text_" + Guid.NewGuid().ToString("N").Substring(0, 8),
                                Name = name,
                                Arguments = args ?? new Dictionary<string, object>()
                            });
                        }
                    }
                    catch { }
                }
            }

            return result;
        }

        /// <summary>
        /// Remove text-based tool call syntax from response to avoid leaking it to the user.
        /// Removes patterns like <function=...>...</tool_call> and minimax:tool_call tags.
        /// </summary>
        private static string RemoveTextToolCallSyntax(string text)
        {
            if (string.IsNullOrEmpty(text))
                return text;

            // Remove <function=NAME>...</tool_call> patterns
            text = Regex.Replace(text, @"<function=\w+>.*?</tool_call>", "", RegexOptions.Singleline | RegexOptions.IgnoreCase);

            // Remove minimax:tool_call patterns (with or without angle brackets)
            // Use the same pattern as parsing to ensure consistency
            text = Regex.Replace(text, @"minimax:tool_call\s+.*?\s*</minimax:tool_call>", "", RegexOptions.Singleline | RegexOptions.IgnoreCase);

            // Pattern 2: <minimax:tool_call>...</minimax:tool_call>
            text = Regex.Replace(text, @"<minimax:tool_call>.*?</minimax:tool_call>", "", RegexOptions.Singleline | RegexOptions.IgnoreCase);

            // Clean up excess whitespace
            text = Regex.Replace(text, @"\n{3,}", "\n\n", RegexOptions.None);
            text = text.Trim();

            return text;
        }

        /// <summary>
        /// Detect if the model claims to have executed a tool but didn't actually call it.
        /// This is a common hallucination pattern where the model describes what it "did"
        /// instead of actually calling the tool.
        /// </summary>
        private static bool DetectToolExecutionClaim(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
                return false;

            var lowerText = text.ToLowerInvariant();

            // Patterns indicating the model claims to have executed something
            var executionClaimPatterns = new[]
            {
                // English patterns
                "i have executed", "i've executed", "i executed",
                "i have run", "i've run", "i ran",
                "i have called", "i've called", "i called",
                "already executed", "already run", "already called",
                "the command has been", "command was executed",
                "successfully executed", "execution complete",

                // Chinese patterns
                "已执行", "已运行", "已调用",
                "执行了", "运行了", "调用了",
                "执行完成", "运行完成",
                "成功执行", "成功运行",

                // Result presentation patterns (claiming to show results without actually getting them)
                "here are the results", "here is the result",
                "the results are", "the result is",
                "结果如下", "结果为", "结果是",
                "以下是结果", "查看结果"
            };

            // Check if text contains execution claims
            bool hasExecutionClaim = executionClaimPatterns.Any(p => lowerText.Contains(p));

            // Additional check: if text contains structured output that looks like tool results
            // (tables, formatted data) but no actual tool was called
            bool hasStructuredOutput =
                (lowerText.Contains("exit code:") || lowerText.Contains("stdout:") || lowerText.Contains("stderr:")) ||
                (lowerText.Contains("状态") && lowerText.Contains("文件")) ||
                (text.Contains("|") && text.Contains("---")); // Markdown table

            return hasExecutionClaim || hasStructuredOutput;
        }

        /// <summary>
        /// Clean up parameter values extracted from text-based tool calls.
        /// Removes control characters, XML remnants, and excess whitespace.
        /// </summary>
        private static string SanitizeParameterValue(string raw)
        {
            if (string.IsNullOrEmpty(raw))
                return string.Empty;

            // Remove any remaining XML/HTML tags
            var cleaned = Regex.Replace(raw, @"<[^>]+>", " ");

            // Remove control characters (newlines, tabs, etc.) - replace with space
            cleaned = Regex.Replace(cleaned, @"[\x00-\x1F\x7F]+", " ");

            // Collapse multiple spaces into one
            cleaned = Regex.Replace(cleaned, @"\s{2,}", " ");

            // Trim
            cleaned = cleaned.Trim();

            // Remove surrounding quotes if present
            if (cleaned.Length >= 2 &&
                ((cleaned[0] == '"' && cleaned[cleaned.Length - 1] == '"') ||
                 (cleaned[0] == '\'' && cleaned[cleaned.Length - 1] == '\'')))
            {
                cleaned = cleaned.Substring(1, cleaned.Length - 2).Trim();
            }

            return cleaned;
        }

        public void Abort()
        {
            _llmClient.Abort();
        }

        /// <summary>
        /// Detect transient exceptions worth retrying (timeouts, network issues).
        /// </summary>
        private static bool IsTransientException(Exception ex)
        {
            if (ex is System.Net.Http.HttpRequestException) return true;
            if (ex is TaskCanceledException tce && tce.CancellationToken == default) return true; // timeout, not user cancel
            if (ex.InnerException is System.Net.Sockets.SocketException) return true;
            return false;
        }

        /// <summary>
        /// Generate a stable signature for a tool call (name + sorted args) for dedup.
        /// </summary>
        private static string GetToolCallSignature(ToolCall call)
        {
            var sb = new System.Text.StringBuilder();
            sb.Append(call.Name ?? "");
            if (call.Arguments != null)
            {
                var sortedKeys = new List<string>(call.Arguments.Keys);
                sortedKeys.Sort(StringComparer.Ordinal);
                foreach (var key in sortedKeys)
                {
                    sb.Append('|').Append(key).Append('=');
                    var val = call.Arguments[key];
                    var strVal = val?.ToString() ?? "";
                    // Normalize values for dedup: trim whitespace, lowercase paths and queries
                    if (key == "path" || key == "file_path" || key == "directory")
                    {
                        strVal = strVal.TrimEnd('/', '\\').ToLowerInvariant();
                    }
                    else if (key == "query" || key == "pattern" || key == "name" || key == "command")
                    {
                        strVal = strVal.Trim().ToLowerInvariant();
                    }
                    sb.Append(strVal);
                }
            }
            return sb.ToString();
        }

        /// <summary>
        /// Detect if a user message is likely conversational (greeting, thanks, acknowledgment)
        /// rather than a coding task. Used to prevent proactive tool execution on greetings.
        /// </summary>
        private static bool IsLikelyConversational(string message)
        {
            if (string.IsNullOrWhiteSpace(message)) return true;
            var trimmed = message.Trim();

            // Long messages are likely tasks
            if (trimmed.Length > 20) return false;

            // Check for task-related keywords — if present, it's a task even if short
            var taskKeywords = new[]
            {
                // Chinese
                "文件", "代码", "读", "写", "编辑", "修改", "创建", "删除", "搜索", "查找", "查看",
                "运行", "执行", "构建", "编译", "测试", "分析", "重构", "调试", "打开", "关闭",
                "添加", "移除", "更新", "生成", "实现", "修复", "优化", "部署", "安装", "配置",
                "项目", "目录", "函数", "类", "方法", "变量", "错误", "bug", "报错",
                // English
                "file", "code", "read", "write", "edit", "create", "delete", "search", "find",
                "run", "exec", "build", "compile", "test", "debug", "refactor", "fix",
                "list", "grep", "dir", "open", "close", "add", "remove", "update", "generate",
                "implement", "deploy", "install", "config", "project", "class", "function",
                "method", "variable", "error", "help me", "帮我", "请帮"
            };

            var lower = trimmed.ToLowerInvariant();
            foreach (var keyword in taskKeywords)
            {
                if (lower.Contains(keyword)) return false;
            }

            // Short message without task keywords → likely conversational
            return true;
        }
    }

    /// <summary>
    /// Interface for the Agent executor
    /// </summary>
    public interface IAgentExecutor
    {
        IAsyncEnumerable<AgentStep> ExecuteAsync(
            string userRequest,
            IAgentContext context,
            IUIContext uiContext,
            List<ChatMessage> previousMessages = null,
            CancellationToken ct = default);

        void Abort();
    }

    /// <summary>
    /// Represents a step in the Agent execution
    /// </summary>
    public class AgentStep
    {
        public AgentStepType Type { get; set; }
        public string Text { get; set; }
        public ToolCall ToolCall { get; set; }
        public ToolResult Result { get; set; }
        public string ErrorMessage { get; set; }

        public static AgentStep TextChunk(string text) => new AgentStep { Type = AgentStepType.TextChunk, Text = text };
        public static AgentStep ToolStart(ToolCall call) => new AgentStep { Type = AgentStepType.ToolStart, ToolCall = call };
        public static AgentStep WithToolResult(ToolCall call, ToolResult result) => new AgentStep { Type = AgentStepType.ToolResult, ToolCall = call, Result = result };
        public static AgentStep Complete(string finalText) => new AgentStep { Type = AgentStepType.Complete, Text = finalText };
        public static AgentStep WithError(string error) => new AgentStep { Type = AgentStepType.Error, ErrorMessage = error };
    }

    public enum AgentStepType
    {
        TextChunk,
        ToolStart,
        ToolResult,
        Complete,
        Error
    }
}

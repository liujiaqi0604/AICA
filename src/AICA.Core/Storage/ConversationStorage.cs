using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;

namespace AICA.Core.Storage
{
    /// <summary>
    /// Manages persistent storage and retrieval of conversation histories.
    /// Stores conversations as JSON files in %LOCALAPPDATA%\AICA\conversations\
    /// </summary>
    public class ConversationStorage
    {
        private readonly string _storageDir;
        private static readonly JsonSerializerOptions _jsonOptions = new JsonSerializerOptions
        {
            WriteIndented = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
        };

        public ConversationStorage(string storageDir = null)
        {
            // 修改存储路径到 C:\Users\{用户名}\.AICA\conversations
            _storageDir = storageDir ??
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".AICA", "conversations");

            if (!Directory.Exists(_storageDir))
                Directory.CreateDirectory(_storageDir);
        }

        /// <summary>
        /// Save a conversation to disk.
        /// </summary>
        public async Task SaveConversationAsync(ConversationRecord record)
        {
            if (record == null) throw new ArgumentNullException(nameof(record));

            // Generate unique ID based on project path + creation time
            if (string.IsNullOrEmpty(record.Id))
            {
                record.Id = GenerateConversationId(record.ProjectPath, record.CreatedAt);
            }

            record.UpdatedAt = DateTimeOffset.UtcNow;

            var filePath = GetFilePath(record.Id);
            var json = JsonSerializer.Serialize(record, _jsonOptions);
            File.WriteAllText(filePath, json);
            await Task.CompletedTask;
        }

        /// <summary>
        /// Generate a unique conversation ID based on project path and creation time.
        /// This ensures the same conversation is identified consistently across sessions.
        /// </summary>
        private string GenerateConversationId(string projectPath, DateTimeOffset createdAt)
        {
            // Normalize project path (handle null/empty)
            var normalizedPath = string.IsNullOrEmpty(projectPath) ? "no-project" : projectPath.ToLowerInvariant();

            // Create unique string: projectPath + createdAt (ticks for precision)
            var uniqueString = $"{normalizedPath}|{createdAt.UtcTicks}";

            // Generate SHA256 hash
            using (var sha256 = SHA256.Create())
            {
                var hashBytes = sha256.ComputeHash(Encoding.UTF8.GetBytes(uniqueString));
                return BitConverter.ToString(hashBytes).Replace("-", "").ToLowerInvariant().Substring(0, 32);
            }
        }

        /// <summary>
        /// Load a conversation by ID.
        /// </summary>
        public async Task<ConversationRecord> LoadConversationAsync(string id)
        {
            var filePath = GetFilePath(id);
            if (!File.Exists(filePath))
                return null;

            var json = File.ReadAllText(filePath);
            var record = JsonSerializer.Deserialize<ConversationRecord>(json, _jsonOptions);
            await Task.CompletedTask;
            return record;
        }

        /// <summary>
        /// List all saved conversations, most recent first.
        /// </summary>
        public Task<List<ConversationSummary>> ListConversationsAsync(int limit = 50)
        {
            var summaries = new List<ConversationSummary>();

            System.Diagnostics.Debug.WriteLine($"[AICA] ListConversationsAsync: _storageDir={_storageDir}");
            System.Diagnostics.Debug.WriteLine($"[AICA] ListConversationsAsync: Directory.Exists={Directory.Exists(_storageDir)}");

            if (!Directory.Exists(_storageDir))
            {
                System.Diagnostics.Debug.WriteLine($"[AICA] ListConversationsAsync: Storage directory does not exist, returning empty list");
                return Task.FromResult(summaries);
            }

            var files = Directory.GetFiles(_storageDir, "*.json")
                .OrderByDescending(f => File.GetLastWriteTimeUtc(f))
                .Take(limit)
                .ToList();

            System.Diagnostics.Debug.WriteLine($"[AICA] ListConversationsAsync: Found {files.Count} JSON files");

            foreach (var file in files)
            {
                try
                {
                    var json = File.ReadAllText(file);
                    var record = JsonSerializer.Deserialize<ConversationRecord>(json, _jsonOptions);
                    if (record != null)
                    {
                        summaries.Add(new ConversationSummary
                        {
                            Id = record.Id,
                            Title = record.Title,
                            CreatedAt = record.CreatedAt,
                            UpdatedAt = record.UpdatedAt,
                            MessageCount = record.Messages?.Count ?? 0,
                            WorkingDirectory = record.WorkingDirectory,
                            ProjectPath = record.ProjectPath,
                            ProjectName = record.ProjectName,
                            SolutionPath = record.SolutionPath
                        });
                    }
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"[AICA] ListConversationsAsync: Failed to load {file}: {ex.Message}");
                }
            }

            System.Diagnostics.Debug.WriteLine($"[AICA] ListConversationsAsync: Loaded {summaries.Count} summaries");
            return Task.FromResult(summaries);
        }

        /// <summary>
        /// List conversations filtered by project path.
        /// </summary>
        public async Task<List<ConversationSummary>> ListConversationsForProjectAsync(
            string projectPath,
            int limit = 50)
        {
            var allConversations = await ListConversationsAsync(limit * 2);
            System.Diagnostics.Debug.WriteLine($"[AICA] ListConversationsForProjectAsync: Total conversations loaded: {allConversations.Count}");

            if (string.IsNullOrEmpty(projectPath))
            {
                // 无项目时，返回所有无项目关联的会话
                return allConversations
                    .Where(c => string.IsNullOrEmpty(c.ProjectPath))
                    .Take(limit)
                    .ToList();
            }

            // 标准化路径（统一大小写和斜杠）
            var normalizedPath = NormalizePath(projectPath);
            System.Diagnostics.Debug.WriteLine($"[AICA] ListConversationsForProjectAsync: Looking for projectPath={projectPath}");
            System.Diagnostics.Debug.WriteLine($"[AICA] ListConversationsForProjectAsync: Normalized path={normalizedPath}");

            var matchedConversations = allConversations
                .Where(c =>
                {
                    var matches = !string.IsNullOrEmpty(c.ProjectPath) &&
                                  NormalizePath(c.ProjectPath) == normalizedPath;
                    System.Diagnostics.Debug.WriteLine($"[AICA]   Conversation {c.Id}: ProjectPath={c.ProjectPath}, Normalized={NormalizePath(c.ProjectPath ?? "")}, Matches={matches}");
                    return matches;
                })
                .Take(limit)
                .ToList();

            System.Diagnostics.Debug.WriteLine($"[AICA] ListConversationsForProjectAsync: Matched {matchedConversations.Count} conversations");
            return matchedConversations;
        }

        /// <summary>
        /// List all conversations across all projects (for /allresume feature).
        /// </summary>
        public async Task<List<ConversationSummary>> ListAllConversationsAsync(int limit = 100)
        {
            return await ListConversationsAsync(limit);
        }

        /// <summary>
        /// Normalize path for comparison (case-insensitive, unified slashes).
        /// </summary>
        private string NormalizePath(string path)
        {
            if (string.IsNullOrEmpty(path)) return string.Empty;
            return Path.GetFullPath(path).ToLowerInvariant().Replace('/', '\\');
        }

        /// <summary>
        /// Delete a conversation by ID.
        /// </summary>
        public Task<bool> DeleteConversationAsync(string id)
        {
            var filePath = GetFilePath(id);
            if (File.Exists(filePath))
            {
                File.Delete(filePath);
                return Task.FromResult(true);
            }
            return Task.FromResult(false);
        }

        /// <summary>
        /// Export a conversation as Markdown.
        /// </summary>
        public Task<string> ExportAsMarkdownAsync(string id)
        {
            var filePath = GetFilePath(id);
            if (!File.Exists(filePath))
                return Task.FromResult<string>(null);

            var json = File.ReadAllText(filePath);
            var record = JsonSerializer.Deserialize<ConversationRecord>(json, _jsonOptions);
            if (record == null)
                return Task.FromResult<string>(null);

            var sb = new StringBuilder();
            sb.AppendLine($"# {record.Title ?? "Conversation"}");
            sb.AppendLine();
            sb.AppendLine($"- **Date**: {record.CreatedAt:yyyy-MM-dd HH:mm:ss}");
            sb.AppendLine($"- **Working Directory**: {record.WorkingDirectory ?? "N/A"}");
            sb.AppendLine();
            sb.AppendLine("---");
            sb.AppendLine();

            if (record.Messages != null)
            {
                foreach (var msg in record.Messages)
                {
                    switch (msg.Role)
                    {
                        case "user":
                            sb.AppendLine($"## 🧑 User");
                            sb.AppendLine();
                            sb.AppendLine(msg.Content);
                            sb.AppendLine();
                            break;
                        case "assistant":
                            sb.AppendLine($"## 🤖 AICA");
                            sb.AppendLine();
                            sb.AppendLine(msg.Content);
                            sb.AppendLine();
                            break;
                        case "tool":
                            sb.AppendLine($"> 🔧 **Tool** (`{msg.ToolName ?? "unknown"}`): {Truncate(msg.Content, 200)}");
                            sb.AppendLine();
                            break;
                    }
                }
            }

            return Task.FromResult(sb.ToString());
        }

        /// <summary>
        /// Clean up old conversations beyond a retention limit.
        /// </summary>
        public Task<int> CleanupOldConversationsAsync(int keepCount = 100)
        {
            if (!Directory.Exists(_storageDir))
                return Task.FromResult(0);

            var files = Directory.GetFiles(_storageDir, "*.json")
                .OrderByDescending(f => File.GetLastWriteTimeUtc(f))
                .ToArray();

            int deleted = 0;
            for (int i = keepCount; i < files.Length; i++)
            {
                try
                {
                    File.Delete(files[i]);
                    deleted++;
                }
                catch { }
            }

            return Task.FromResult(deleted);
        }

        private string GetFilePath(string id)
        {
            return Path.Combine(_storageDir, $"{id}.json");
        }

        private static string Truncate(string text, int maxLen)
        {
            if (string.IsNullOrEmpty(text) || text.Length <= maxLen) return text ?? "";
            return text.Substring(0, maxLen) + "...";
        }
    }

    /// <summary>
    /// A complete conversation record for persistence.
    /// </summary>
    public class ConversationRecord
    {
        public string Id { get; set; }
        public string Title { get; set; }
        public string WorkingDirectory { get; set; }

        // 新增：项目信息字段
        public string ProjectPath { get; set; }
        public string ProjectName { get; set; }
        public string SolutionPath { get; set; }

        public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
        public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
        public List<ConversationMessageRecord> Messages { get; set; } = new List<ConversationMessageRecord>();
    }

    /// <summary>
    /// A single message in a conversation record.
    /// </summary>
    public class ConversationMessageRecord
    {
        public string Role { get; set; }
        public string Content { get; set; }
        public string ToolName { get; set; }

        // 新增：支持保存工具调用日志和完成数据
        public string ToolLogsHtml { get; set; }
        public string CompletionData { get; set; }

        public DateTimeOffset Timestamp { get; set; } = DateTimeOffset.UtcNow;
    }

    /// <summary>
    /// Lightweight summary for listing conversations without loading full content.
    /// </summary>
    public class ConversationSummary
    {
        public string Id { get; set; }
        public string Title { get; set; }
        public DateTimeOffset CreatedAt { get; set; }
        public DateTimeOffset UpdatedAt { get; set; }
        public int MessageCount { get; set; }
        public string WorkingDirectory { get; set; }

        // 新增：项目信息字段
        public string ProjectPath { get; set; }
        public string ProjectName { get; set; }
        public string SolutionPath { get; set; }
    }
}

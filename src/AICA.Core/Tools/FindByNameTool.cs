using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using AICA.Core.Agent;

namespace AICA.Core.Tools
{
    /// <summary>
    /// Tool for searching files and directories by name pattern
    /// </summary>
    public class FindByNameTool : IAgentTool
    {
        public string Name => "find_by_name";
        public string Description => "Search for files or directories by name pattern. Use glob patterns like '*.cs' or partial names. Returns matching file/directory paths with size information. Use this when you know the file name but not its location.";

        public ToolDefinition GetDefinition()
        {
            return new ToolDefinition
            {
                Name = Name,
                Description = Description,
                Parameters = new ToolParameters
                {
                    Type = "object",
                    Properties = new Dictionary<string, ToolParameterProperty>
                    {
                        ["pattern"] = new ToolParameterProperty
                        {
                            Type = "string",
                            Description = "The glob pattern to search for (e.g. '*.cs', 'README*', 'Controller')"
                        },
                        ["path"] = new ToolParameterProperty
                        {
                            Type = "string",
                            Description = "Directory to search in (relative to workspace root). Defaults to workspace root."
                        },
                        ["type"] = new ToolParameterProperty
                        {
                            Type = "string",
                            Description = "Filter by type: 'file', 'directory', or 'any'. Default is 'any'."
                        },
                        ["max_depth"] = new ToolParameterProperty
                        {
                            Type = "integer",
                            Description = "Maximum directory depth to search. Default is unlimited."
                        },
                        ["max_results"] = new ToolParameterProperty
                        {
                            Type = "integer",
                            Description = "Maximum number of results to return. Default is 50."
                        }
                    },
                    Required = new[] { "pattern" }
                }
            };
        }

        public async Task<ToolResult> ExecuteAsync(ToolCall call, IAgentContext context, IUIContext uiContext, CancellationToken ct = default)
        {
            if (!call.Arguments.TryGetValue("pattern", out var patternObj) || patternObj == null)
                return ToolResult.Fail("Missing required parameter: pattern");

            var pattern = patternObj.ToString();
            if (string.IsNullOrEmpty(pattern))
                return ToolResult.Fail("Search pattern cannot be empty");

            // Parse optional parameters
            var searchPath = ".";
            if (call.Arguments.TryGetValue("path", out var pathObj) && pathObj != null)
            {
                var p = pathObj.ToString();
                if (!string.IsNullOrWhiteSpace(p)) searchPath = p;
            }

            string typeFilter = "any";
            if (call.Arguments.TryGetValue("type", out var typeObj) && typeObj != null)
            {
                var t = typeObj.ToString().ToLowerInvariant();
                if (t == "file" || t == "directory" || t == "any") typeFilter = t;
            }

            int maxDepth = int.MaxValue;
            if (call.Arguments.TryGetValue("max_depth", out var depthObj) && depthObj != null)
                int.TryParse(depthObj.ToString(), out maxDepth);

            int maxResults = 50;
            if (call.Arguments.TryGetValue("max_results", out var maxObj) && maxObj != null)
                int.TryParse(maxObj.ToString(), out maxResults);

            // Resolve full path
            string fullPath;
            if (string.IsNullOrEmpty(searchPath) || searchPath == "." || searchPath == "./")
                fullPath = context.WorkingDirectory;
            else if (Path.IsPathRooted(searchPath))
                fullPath = searchPath;
            else
                fullPath = Path.Combine(context.WorkingDirectory, searchPath);

            if (!context.IsPathAccessible(searchPath))
                return ToolResult.Fail($"Access denied: {searchPath}");

            if (!Directory.Exists(fullPath))
                return ToolResult.Fail($"Directory not found: {searchPath}");

            return await Task.Run(() =>
            {
                var results = new List<FindResult>();

                try
                {
                    SearchDirectory(fullPath, pattern, typeFilter, 0, maxDepth, maxResults, results, ct);
                }
                catch (Exception ex)
                {
                    return ToolResult.Fail($"Search error: {ex.Message}");
                }

                if (results.Count == 0)
                    return ToolResult.Ok($"No matches found for pattern '{pattern}'.");

                var sb = new StringBuilder();
                sb.AppendLine($"Found {results.Count} result(s) for '{pattern}':");

                foreach (var result in results)
                {
                    var relativePath = GetRelativePath(context.WorkingDirectory, result.FullPath);
                    if (result.IsDirectory)
                    {
                        sb.AppendLine($"  [DIR]  {relativePath}/");
                    }
                    else
                    {
                        sb.AppendLine($"  [FILE] {relativePath} ({FormatSize(result.Size)})");
                    }
                }

                if (results.Count >= maxResults)
                    sb.AppendLine($"\n[Results truncated at {maxResults}]");

                return ToolResult.Ok(sb.ToString());
            }, ct).ConfigureAwait(false);
        }

        private void SearchDirectory(string directory, string pattern, string typeFilter,
            int currentDepth, int maxDepth, int maxResults, List<FindResult> results, CancellationToken ct)
        {
            if (ct.IsCancellationRequested || results.Count >= maxResults)
                return;

            if (currentDepth > maxDepth)
                return;

            if (IsExcludedDirectory(directory))
                return;

            try
            {
                // Search directories
                if (typeFilter == "any" || typeFilter == "directory")
                {
                    foreach (var dir in Directory.GetDirectories(directory))
                    {
                        if (results.Count >= maxResults || ct.IsCancellationRequested) return;
                        
                        var dirName = Path.GetFileName(dir);
                        if (IsExcludedDirectory(dir)) continue;

                        if (MatchesPattern(dirName, pattern))
                        {
                            results.Add(new FindResult { FullPath = dir, IsDirectory = true });
                        }
                    }
                }

                // Search files
                if (typeFilter == "any" || typeFilter == "file")
                {
                    foreach (var file in Directory.GetFiles(directory))
                    {
                        if (results.Count >= maxResults || ct.IsCancellationRequested) return;

                        var fileName = Path.GetFileName(file);
                        if (MatchesPattern(fileName, pattern))
                        {
                            long size = 0;
                            try { size = new FileInfo(file).Length; } catch { }
                            results.Add(new FindResult { FullPath = file, IsDirectory = false, Size = size });
                        }
                    }
                }

                // Recurse into subdirectories
                if (currentDepth < maxDepth)
                {
                    foreach (var subDir in Directory.GetDirectories(directory))
                    {
                        if (results.Count >= maxResults || ct.IsCancellationRequested) return;
                        if (IsExcludedDirectory(subDir)) continue;
                        SearchDirectory(subDir, pattern, typeFilter, currentDepth + 1, maxDepth, maxResults, results, ct);
                    }
                }
            }
            catch (UnauthorizedAccessException) { }
            catch (IOException) { }
        }

        private bool MatchesPattern(string name, string pattern)
        {
            // Support simple glob patterns: *, ?
            // Also support partial name match (contains)
            if (pattern.Contains("*") || pattern.Contains("?"))
            {
                return GlobMatch(name, pattern);
            }
            else
            {
                // Plain text: partial case-insensitive match
                return name.IndexOf(pattern, StringComparison.OrdinalIgnoreCase) >= 0;
            }
        }

        private bool GlobMatch(string input, string pattern)
        {
            // Convert glob to regex
            var regexPattern = "^" +
                System.Text.RegularExpressions.Regex.Escape(pattern)
                    .Replace("\\*", ".*")
                    .Replace("\\?", ".") +
                "$";

            return System.Text.RegularExpressions.Regex.IsMatch(
                input, regexPattern, System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        }

        private bool IsExcludedDirectory(string path)
        {
            var dirName = Path.GetFileName(path);
            if (string.IsNullOrEmpty(dirName)) return false;

            var excluded = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                ".git", ".vs", "bin", "obj", "node_modules",
                "packages", ".nuget", "__pycache__", ".svn"
            };

            return excluded.Contains(dirName);
        }

        private string FormatSize(long bytes)
        {
            string[] suffixes = { "B", "KB", "MB", "GB" };
            int suffixIndex = 0;
            double size = bytes;
            while (size >= 1024 && suffixIndex < suffixes.Length - 1)
            {
                size /= 1024;
                suffixIndex++;
            }
            return $"{size:0.##} {suffixes[suffixIndex]}";
        }

        private string GetRelativePath(string basePath, string fullPath)
        {
            if (string.IsNullOrEmpty(basePath)) return fullPath;
            try
            {
                var baseUri = new Uri(basePath.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar);
                var fullUri = new Uri(fullPath);
                return Uri.UnescapeDataString(baseUri.MakeRelativeUri(fullUri).ToString().Replace('/', Path.DirectorySeparatorChar));
            }
            catch
            {
                return fullPath;
            }
        }

        public Task HandlePartialAsync(ToolCall call, IUIContext ui, CancellationToken ct = default)
        {
            return Task.CompletedTask;
        }

        private class FindResult
        {
            public string FullPath { get; set; }
            public bool IsDirectory { get; set; }
            public long Size { get; set; }
        }
    }
}

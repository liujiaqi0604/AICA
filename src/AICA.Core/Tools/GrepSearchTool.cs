using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using AICA.Core.Agent;

namespace AICA.Core.Tools
{
    /// <summary>
    /// Tool for searching text patterns in files within the workspace
    /// </summary>
    public class GrepSearchTool : IAgentTool
    {
        public string Name => "grep_search";
        public string Description => "Search for text patterns in files. Use this to find code, functions, classes, or specific text across the codebase. Supports regex patterns and file filtering. Returns matching lines with file paths and line numbers.";

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
                        ["query"] = new ToolParameterProperty
                        {
                            Type = "string",
                            Description = "The search pattern (regex by default, or fixed string if fixed_strings is true)"
                        },
                        ["path"] = new ToolParameterProperty
                        {
                            Type = "string",
                            Description = "Directory or file path to search in (relative to workspace root). Defaults to workspace root."
                        },
                        ["includes"] = new ToolParameterProperty
                        {
                            Type = "string",
                            Description = "File glob pattern to include (e.g. '*.cs', '*.py'). Optional."
                        },
                        ["fixed_strings"] = new ToolParameterProperty
                        {
                            Type = "boolean",
                            Description = "If true, treat query as a literal string instead of regex. Default is false."
                        },
                        ["case_sensitive"] = new ToolParameterProperty
                        {
                            Type = "boolean",
                            Description = "If true, search is case-sensitive. Default is false."
                        },
                        ["max_results"] = new ToolParameterProperty
                        {
                            Type = "integer",
                            Description = "Maximum number of matching lines to return. Default is 50."
                        }
                    },
                    Required = new[] { "query" }
                }
            };
        }

        public async Task<ToolResult> ExecuteAsync(ToolCall call, IAgentContext context, IUIContext uiContext, CancellationToken ct = default)
        {
            if (!call.Arguments.TryGetValue("query", out var queryObj) || queryObj == null)
                return ToolResult.Fail("Missing required parameter: query");

            var query = queryObj.ToString();
            if (string.IsNullOrEmpty(query))
                return ToolResult.Fail("Search query cannot be empty");

            // Parse optional parameters
            var searchPath = ".";
            if (call.Arguments.TryGetValue("path", out var pathObj) && pathObj != null)
            {
                var p = pathObj.ToString();
                if (!string.IsNullOrWhiteSpace(p)) searchPath = p;
            }

            string includePattern = null;
            if (call.Arguments.TryGetValue("includes", out var includesObj) && includesObj != null)
            {
                // Handle JsonElement arrays: ["*.h", "*.cpp"] -> "*.h,*.cpp"
                if (includesObj is System.Text.Json.JsonElement je && je.ValueKind == System.Text.Json.JsonValueKind.Array)
                {
                    var parts = new List<string>();
                    foreach (var item in je.EnumerateArray())
                        parts.Add(item.GetString() ?? item.ToString());
                    includePattern = string.Join(",", parts);
                }
                else
                {
                    includePattern = includesObj.ToString();
                }
            }

            bool fixedStrings = false;
            if (call.Arguments.TryGetValue("fixed_strings", out var fixedObj) && fixedObj != null)
                bool.TryParse(fixedObj.ToString(), out fixedStrings);

            bool caseSensitive = false;
            if (call.Arguments.TryGetValue("case_sensitive", out var caseObj) && caseObj != null)
                bool.TryParse(caseObj.ToString(), out caseSensitive);

            int maxResults = 200;
            if (call.Arguments.TryGetValue("max_results", out var maxObj) && maxObj != null)
                int.TryParse(maxObj.ToString(), out maxResults);

            // Resolve full path (supports source roots)
            string fullPath;
            List<string> searchPaths = new List<string>();

            if (string.IsNullOrEmpty(searchPath) || searchPath == "." || searchPath == "./")
            {
                // Default search: include working directory AND all source roots
                searchPaths.Add(context.WorkingDirectory);

                // Add source roots if available
                if (context.SourceRoots != null && context.SourceRoots.Count > 0)
                {
                    searchPaths.AddRange(context.SourceRoots);
                }

                fullPath = context.WorkingDirectory; // For compatibility
            }
            else if (Path.IsPathRooted(searchPath))
            {
                fullPath = searchPath;
                searchPaths.Add(fullPath);
            }
            else
            {
                // Try resolving via source roots first
                var resolved = context.ResolveDirectoryPath(searchPath);
                fullPath = resolved ?? Path.Combine(context.WorkingDirectory, searchPath);
                searchPaths.Add(fullPath);
            }

            if (!context.IsPathAccessible(searchPath))
            {
                // Also check if the resolved path is accessible
                if (fullPath == null || !context.IsPathAccessible(fullPath))
                    return ToolResult.Fail($"Access denied: {searchPath}");
            }

            // Build regex
            Regex regex;
            try
            {
                var pattern = fixedStrings ? Regex.Escape(query) : query;
                var options = RegexOptions.Compiled;
                if (!caseSensitive) options |= RegexOptions.IgnoreCase;
                regex = new Regex(pattern, options);
            }
            catch (ArgumentException ex)
            {
                return ToolResult.Fail($"Invalid regex pattern: {ex.Message}");
            }

            // Execute search - ConfigureAwait(false) to avoid deadlock with UI thread
            return await Task.Run(() =>
            {
                var results = new StringBuilder();
                int matchCount = 0;
                int filesSearched = 0;
                int filesMatched = 0;

                // Track per-file match counts for accurate statistics
                var fileMatchCounts = new Dictionary<string, int>();

                try
                {
                    // Search in all paths (working directory + source roots)
                    foreach (var searchDir in searchPaths)
                    {
                        if (ct.IsCancellationRequested) break;
                        if (matchCount >= maxResults) break;

                        IEnumerable<string> files;
                        if (File.Exists(searchDir))
                        {
                            files = new[] { searchDir };
                        }
                        else if (Directory.Exists(searchDir))
                        {
                            files = GetSearchFiles(searchDir, includePattern);
                        }
                        else
                        {
                            continue; // Skip non-existent paths
                        }

                        foreach (var file in files)
                        {
                            if (ct.IsCancellationRequested) break;
                            if (IsExcludedFile(file)) continue;

                            filesSearched++;

                            try
                            {
                                // Skip files larger than 1MB to avoid reading huge generated files
                                var fileInfo = new FileInfo(file);
                                if (fileInfo.Length > 1024 * 1024)
                                {
                                    continue;
                                }

                                var lines = File.ReadAllLines(file);
                                int fileMatchCount = 0;
                                var relativePath = GetRelativePath(context.WorkingDirectory, file);
                                bool fileHeaderWritten = false;

                                for (int i = 0; i < lines.Length; i++)
                                {
                                    if (regex.IsMatch(lines[i]))
                                    {
                                        fileMatchCount++;

                                        // Only write to results if we haven't hit the display limit
                                        if (matchCount < maxResults)
                                        {
                                            if (!fileHeaderWritten)
                                            {
                                                results.AppendLine($"\n{relativePath}:");
                                                fileHeaderWritten = true;
                                            }
                                            results.AppendLine($"  {i + 1}: {lines[i].Trim()}");
                                            matchCount++;
                                        }
                                    }
                                }

                                // Record file match count even if display was truncated
                                if (fileMatchCount > 0)
                                {
                                    fileMatchCounts[relativePath] = fileMatchCount;
                                    filesMatched++;
                                }
                            }
                            catch
                            {
                                // Skip files we can't read
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    return ToolResult.Fail($"Search error: {ex.Message}");
                }

                if (fileMatchCounts.Count == 0)
                {
                    return ToolResult.Ok($"No matches found for '{query}' in {filesSearched} files.");
                }

                // Calculate total matches (may be more than displayed)
                int totalMatches = fileMatchCounts.Values.Sum();

                var summary = new StringBuilder();
                summary.AppendLine($"Found {totalMatches} match(es) in {filesMatched} file(s) (searched {filesSearched} files)");

                // If results were truncated, provide per-file statistics
                if (totalMatches > maxResults)
                {
                    summary.AppendLine($"[Display truncated at {maxResults} results, but all {totalMatches} matches were counted]");
                    summary.AppendLine();
                    summary.AppendLine("Per-file match counts:");
                    foreach (var kvp in fileMatchCounts.OrderByDescending(x => x.Value))
                    {
                        summary.AppendLine($"  {kvp.Key}: {kvp.Value}");
                    }
                    summary.AppendLine();
                    summary.AppendLine("Detailed matches (first " + maxResults + "):");
                }

                return ToolResult.Ok(summary.ToString() + results.ToString());
            }, ct).ConfigureAwait(false);
        }

        private IEnumerable<string> GetSearchFiles(string directory, string includePattern)
        {
            // Support multiple patterns separated by comma or semicolon
            // e.g. "*.h,*.cpp" or "*.h;*.cpp" or just "*.h"
            var patterns = new List<string>();
            if (!string.IsNullOrEmpty(includePattern))
            {
                // Clean up array-like input from LLM: ["*.h", "*.cpp"] -> *.h, *.cpp
                var cleaned = includePattern.Trim().TrimStart('[').TrimEnd(']');
                cleaned = cleaned.Replace("\"", "").Replace("'", "");
                foreach (var p in cleaned.Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries))
                {
                    var trimmed = p.Trim();
                    if (!string.IsNullOrEmpty(trimmed))
                        patterns.Add(trimmed);
                }
            }
            if (patterns.Count == 0)
                patterns.Add("*.*");

            // Use manual recursive enumeration to handle per-directory access errors gracefully
            // Merge results from all patterns
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var pattern in patterns)
            {
                foreach (var file in EnumerateFilesSafe(directory, pattern))
                {
                    if (seen.Add(file))
                        yield return file;
                }
            }
        }

        /// <summary>
        /// Recursively enumerate files, skipping directories that throw access errors.
        /// Unlike Directory.EnumerateFiles(AllDirectories), this won't abort on permission errors.
        /// </summary>
        private IEnumerable<string> EnumerateFilesSafe(string directory, string searchPattern, int maxFiles = 10000)
        {
            int count = 0;
            var dirs = new Stack<string>();
            dirs.Push(directory);

            while (dirs.Count > 0 && count < maxFiles)
            {
                var currentDir = dirs.Pop();

                // Skip excluded directories early
                if (IsExcludedDirectory(currentDir))
                    continue;

                // Enumerate files in current directory
                IEnumerable<string> files;
                try
                {
                    files = Directory.EnumerateFiles(currentDir, searchPattern);
                }
                catch
                {
                    continue; // Skip directories we can't read
                }

                foreach (var file in files)
                {
                    if (count >= maxFiles) yield break;
                    count++;
                    yield return file;
                }

                // Enumerate subdirectories
                try
                {
                    foreach (var subDir in Directory.EnumerateDirectories(currentDir))
                    {
                        dirs.Push(subDir);
                    }
                }
                catch
                {
                    // Skip if we can't enumerate subdirectories
                }
            }
        }

        private bool IsExcludedDirectory(string dirPath)
        {
            var dirName = Path.GetFileName(dirPath);
            if (string.IsNullOrEmpty(dirName)) return false;

            var excludedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                ".git", ".vs", "bin", "obj", "node_modules", "packages", ".nuget", "TestResults",
                "Debug", "Release", "RelWithDebInfo", "MinSizeRel", "x64", "x86"
            };

            return excludedNames.Contains(dirName);
        }

        private bool IsExcludedFile(string path)
        {
            var excludedDirs = new[]
            {
                $"{Path.DirectorySeparatorChar}.git{Path.DirectorySeparatorChar}",
                $"{Path.DirectorySeparatorChar}.vs{Path.DirectorySeparatorChar}",
                $"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}",
                $"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}",
                $"{Path.DirectorySeparatorChar}node_modules{Path.DirectorySeparatorChar}",
                $"{Path.DirectorySeparatorChar}packages{Path.DirectorySeparatorChar}",
                $"{Path.DirectorySeparatorChar}.nuget{Path.DirectorySeparatorChar}"
            };

            foreach (var dir in excludedDirs)
            {
                if (path.IndexOf(dir, StringComparison.OrdinalIgnoreCase) >= 0)
                    return true;
            }

            // Skip binary/large files by extension
            var ext = Path.GetExtension(path)?.ToLowerInvariant();
            var binaryExtensions = new HashSet<string>
            {
                ".exe", ".dll", ".pdb", ".obj", ".o", ".lib", ".so", ".a",
                ".png", ".jpg", ".jpeg", ".gif", ".bmp", ".ico", ".svg", ".webp",
                ".zip", ".tar", ".gz", ".rar", ".7z", ".bz2",
                ".pdf", ".doc", ".docx", ".xls", ".xlsx",
                ".vsix", ".nupkg", ".snk",
                ".tlog", ".log", ".cache", ".ilk", ".idb", ".ipch", ".sdf", ".suo",
                ".pch", ".ncb", ".opensdf", ".res", ".lastbuildstate"
            };

            return binaryExtensions.Contains(ext);
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
    }
}

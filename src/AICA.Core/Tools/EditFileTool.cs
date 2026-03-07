using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using AICA.Core.Agent;

namespace AICA.Core.Tools
{
    /// <summary>
    /// Tool for making precise edits to existing files
    /// </summary>
    public class EditFileTool : IAgentTool
    {
        public string Name => "edit";
        public string Description => "Edit an EXISTING file by replacing text. CRITICAL: The old_string must match EXACTLY (including all whitespace, line breaks, and indentation). Use read_file first to see the exact content. Set full_replace=true to replace entire file content.";

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
                        ["file_path"] = new ToolParameterProperty
                        {
                            Type = "string",
                            Description = "The path to the file to edit"
                        },
                        ["old_string"] = new ToolParameterProperty
                        {
                            Type = "string",
                            Description = "The exact text to replace. MUST be unique in the file."
                        },
                        ["new_string"] = new ToolParameterProperty
                        {
                            Type = "string",
                            Description = "The new text to replace old_string with"
                        },
                        ["replace_all"] = new ToolParameterProperty
                        {
                            Type = "boolean",
                            Description = "If true, replace all occurrences. Default is false.",
                            Default = false
                        },
                        ["full_replace"] = new ToolParameterProperty
                        {
                            Type = "boolean",
                            Description = "If true, replace the entire file content with new_string. When using this mode, old_string is ignored and can be set to any value (e.g., empty string). This is useful for completely rewriting a file. Default is false.",
                            Default = false
                        }
                    },
                    Required = new[] { "file_path", "new_string" }
                }
            };
        }

        public async Task<ToolResult> ExecuteAsync(ToolCall call, IAgentContext context, IUIContext uiContext, CancellationToken ct = default)
        {
            // Validate required parameters
            if (!call.Arguments.TryGetValue("file_path", out var pathObj) || pathObj == null)
                return ToolResult.Fail("Missing required parameter: file_path");

            if (!call.Arguments.TryGetValue("new_string", out var newObj) || newObj == null)
                return ToolResult.Fail("Missing required parameter: new_string");

            var path = pathObj.ToString();
            var newString = newObj.ToString();
            var replaceAll = false;
            var fullReplace = false;

            if (call.Arguments.TryGetValue("replace_all", out var replaceAllObj) && replaceAllObj != null)
            {
                bool.TryParse(replaceAllObj.ToString(), out replaceAll);
            }

            if (call.Arguments.TryGetValue("full_replace", out var fullReplaceObj) && fullReplaceObj != null)
            {
                bool.TryParse(fullReplaceObj.ToString(), out fullReplace);
            }

            // For non-full_replace mode, old_string is required
            string oldString = null;
            if (!fullReplace)
            {
                if (!call.Arguments.TryGetValue("old_string", out var oldObj) || oldObj == null)
                    return ToolResult.Fail("Missing required parameter: old_string (not required when full_replace=true)");
                oldString = oldObj.ToString();
            }

            // Validate path access
            if (!context.IsPathAccessible(path))
                return ToolResult.Fail($"Access denied: {path}");

            // Check file exists
            if (!await context.FileExistsAsync(path, ct))
                return ToolResult.Fail($"File not found: {path}");

            // Read current content
            var content = await context.ReadFileAsync(path, ct);

            string newContent;

            if (fullReplace)
            {
                // Full replace mode: replace entire file content
                newContent = newString;
            }
            else
            {
                // Normal edit mode: require old_string matching

                // Check old_string exists
                if (!content.Contains(oldString))
                {
                    // Provide detailed debugging information with visible whitespace
                    var preview = content.Length > 500 ? content.Substring(0, 500) + "..." : content;
                    var oldPreview = oldString.Length > 200 ? oldString.Substring(0, 200) + "..." : oldString;

                    // Show whitespace characters explicitly
                    var oldStringVisible = oldString
                        .Replace("\r", "\\r")
                        .Replace("\n", "\\n")
                        .Replace("\t", "\\t")
                        .Replace(" ", "·");

                    var contentVisible = preview
                        .Replace("\r", "\\r")
                        .Replace("\n", "\\n")
                        .Replace("\t", "\\t")
                        .Replace(" ", "·");

                    return ToolResult.Fail(
                        $"old_string not found in file.\n\n" +
                        $"CRITICAL: The string you're searching for does NOT exist in the file.\n\n" +
                        $"What you're searching for ({oldString.Length} chars, whitespace shown as ·\\n\\r\\t):\n{oldStringVisible}\n\n" +
                        $"Actual file content ({content.Length} chars total, whitespace shown as ·\\n\\r\\t):\n{contentVisible}\n\n" +
                        $"Common issues:\n" +
                        $"1. Missing empty lines (\\n\\n)\n" +
                        $"2. Missing trailing spaces (shown as ·)\n" +
                        $"3. Wrong line endings (\\r\\n vs \\n)\n\n" +
                        $"SOLUTION: Use read_file to see the EXACT content, then copy the EXACT string including ALL whitespace.");
                }

                // Check uniqueness (unless replace_all)
                if (!replaceAll)
                {
                    var firstIndex = content.IndexOf(oldString);
                    var lastIndex = content.LastIndexOf(oldString);
                    if (firstIndex != lastIndex)
                    {
                        return ToolResult.Fail("old_string is not unique in the file. Provide more context to make it unique, or use replace_all=true.");
                    }
                }

                // Check if old_string equals new_string
                if (oldString == newString)
                    return ToolResult.Fail("old_string and new_string are identical. This is a no-op.");

                // Apply the edit
                newContent = replaceAll
                    ? content.Replace(oldString, newString)
                    : ReplaceFirst(content, oldString, newString);
            }

            // Show diff and let user apply changes
            var result = await context.ShowDiffAndApplyAsync(path, content, newContent, ct);

            if (!result.Applied)
            {
                var currentContent = await context.ReadFileAsync(path, ct);
                return ToolResult.Ok(
                    $"EDIT CANCELLED BY USER - NO CHANGES WERE APPLIED\n\n" +
                    $"File: {path}\n\n" +
                    $"The user chose not to apply the proposed edit. Respect this decision and do NOT retry the same edit automatically unless the user explicitly asks you to try again.\n\n" +
                    $"CURRENT FILE CONTENT (unchanged after cancellation):\n{currentContent}\n\n" +
                    $"Next step: Explain that the edit was cancelled, analyze the current file state if helpful, and continue the task based on the unchanged file content."
                );
            }

            // Read the actual saved content (user may have modified it in the diff view)
            var finalContent = await context.ReadFileAsync(path, ct);

            // Check if user modified the content
            bool wasModifiedByUser = finalContent != newContent;

            if (wasModifiedByUser)
            {
                // Calculate actual changes
                var originalLines = content.Split('\n').Length;
                var finalLines = finalContent.Split('\n').Length;
                var lineDiff = finalLines - originalLines;
                var diffText = lineDiff > 0 ? $"+{lineDiff}" : lineDiff < 0 ? $"{lineDiff}" : "0";

                // Include the actual final content in the result so AI can see what was actually applied
                return ToolResult.Ok($"⚠️ USER MANUALLY EDITED THE FILE - YOUR SUGGESTION WAS NOT USED ⚠️\n\nFile: {path}\nOriginal: {originalLines} lines → User's version: {finalLines} lines ({diffText})\n\n📄 ACTUAL FILE CONTENT (as saved by user):\n{finalContent}\n\n⚠️ CRITICAL: You MUST read and analyze the actual content above. Do NOT describe your original suggestion. Describe what the user actually saved.");
            }
            else
            {
                if (fullReplace)
                {
                    return ToolResult.Ok($"File content completely replaced: {path}");
                }
                else
                {
                    var occurrences = replaceAll ? CountOccurrences(content, oldString) : 1;
                    return ToolResult.Ok($"File edited: {path} ({occurrences} replacement(s) made)");
                }
            }
        }

        private string ReplaceFirst(string text, string oldValue, string newValue)
        {
            var index = text.IndexOf(oldValue);
            if (index < 0) return text;
            return text.Substring(0, index) + newValue + text.Substring(index + oldValue.Length);
        }

        private int CountOccurrences(string text, string pattern)
        {
            int count = 0;
            int index = 0;
            while ((index = text.IndexOf(pattern, index)) != -1)
            {
                count++;
                index += pattern.Length;
            }
            return count;
        }

        private string Truncate(string text, int maxLength)
        {
            if (string.IsNullOrEmpty(text) || text.Length <= maxLength)
                return text;
            return text.Substring(0, maxLength) + "...";
        }

        public Task HandlePartialAsync(ToolCall call, IUIContext ui, CancellationToken ct = default)
        {
            return Task.CompletedTask;
        }
    }
}

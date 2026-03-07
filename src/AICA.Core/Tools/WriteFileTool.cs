using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using AICA.Core.Agent;

namespace AICA.Core.Tools
{
    /// <summary>
    /// Tool for creating new files
    /// </summary>
    public class WriteFileTool : IAgentTool
    {
        public string Name => "write_to_file";
        public string Description => "Create a NEW file with content. IMPORTANT: Only use this for files that don't exist yet. For existing files, use the 'edit' tool instead. This will fail if the file already exists.";

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
                        ["path"] = new ToolParameterProperty
                        {
                            Type = "string",
                            Description = "The path for the new file (relative to workspace root)"
                        },
                        ["content"] = new ToolParameterProperty
                        {
                            Type = "string",
                            Description = "The content to write to the file"
                        }
                    },
                    Required = new[] { "path", "content" }
                }
            };
        }

        public async Task<ToolResult> ExecuteAsync(ToolCall call, IAgentContext context, IUIContext uiContext, CancellationToken ct = default)
        {
            if (!call.Arguments.TryGetValue("path", out var pathObj) || pathObj == null)
            {
                return ToolResult.Fail("Missing required parameter: path");
            }

            if (!call.Arguments.TryGetValue("content", out var contentObj) || contentObj == null)
            {
                return ToolResult.Fail("Missing required parameter: content");
            }

            var path = pathObj.ToString();
            var content = contentObj.ToString();

            if (!context.IsPathAccessible(path))
            {
                return ToolResult.Fail($"Access denied: {path}");
            }

            // Check if file already exists
            if (await context.FileExistsAsync(path, ct))
            {
                return ToolResult.Fail($"File already exists: {path}. Use 'edit' tool to modify existing files.");
            }

            // Request confirmation
            var confirmed = await context.RequestConfirmationAsync(
                "Create File",
                $"Create new file: {path}\n\nContent preview:\n{content.Substring(0, System.Math.Min(500, content.Length))}...",
                ct);

            if (!confirmed)
            {
                return ToolResult.Fail("Operation cancelled by user");
            }

            await context.WriteFileAsync(path, content, ct);

            return ToolResult.Ok($"File created: {path}");
        }

        public Task HandlePartialAsync(ToolCall call, IUIContext ui, CancellationToken ct = default)
        {
            return Task.CompletedTask;
        }
    }
}

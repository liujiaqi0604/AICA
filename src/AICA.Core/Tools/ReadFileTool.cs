using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using AICA.Core.Agent;

namespace AICA.Core.Tools
{
    /// <summary>
    /// Tool for reading file contents
    /// </summary>
    public class ReadFileTool : IAgentTool
    {
        public string Name => "read_file";
        public string Description => "Read the contents of a file. Use this to view file content before making changes. Supports reading specific line ranges with offset and limit parameters.";

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
                            Description = "The path to the file to read (relative to workspace root or source roots)"
                        },
                        ["offset"] = new ToolParameterProperty
                        {
                            Type = "integer",
                            Description = "Optional. The 1-indexed line number to start reading from."
                        },
                        ["limit"] = new ToolParameterProperty
                        {
                            Type = "integer",
                            Description = "Optional. The number of lines to read."
                        }
                    },
                    Required = new[] { "path" }
                }
            };
        }

        public async Task<ToolResult> ExecuteAsync(ToolCall call, IAgentContext context, IUIContext uiContext, CancellationToken ct = default)
        {
            if (!call.Arguments.TryGetValue("path", out var pathObj) || pathObj == null)
            {
                return ToolResult.Fail("Missing required parameter: path");
            }

            var path = pathObj.ToString();

            // Try to resolve across working directory and source roots
            var resolvedPath = context.ResolveFilePath(path);

            if (resolvedPath != null)
            {
                // Resolved path found — check accessibility
                if (!context.IsPathAccessible(resolvedPath))
                    return ToolResult.Fail($"Access denied: {path}");
            }
            else
            {
                // Not resolved — check original path accessibility
                if (!context.IsPathAccessible(path))
                    return ToolResult.Fail($"Access denied: {path}");

                if (!await context.FileExistsAsync(path, ct))
                    return ToolResult.Fail($"File not found: {path}");
            }

            // ReadFileAsync already uses ResolveFilePath internally
            var content = await context.ReadFileAsync(path, ct);

            // Handle offset and limit if provided
            int? offset = null;
            int? limit = null;

            if (call.Arguments.TryGetValue("offset", out var offsetObj) && offsetObj != null)
            {
                if (int.TryParse(offsetObj.ToString(), out var o))
                    offset = o;
            }

            if (call.Arguments.TryGetValue("limit", out var limitObj) && limitObj != null)
            {
                if (int.TryParse(limitObj.ToString(), out var l))
                    limit = l;
            }

            if (offset.HasValue || limit.HasValue)
            {
                var lines = content.Split('\n');
                var startIndex = (offset ?? 1) - 1;
                var count = limit ?? (lines.Length - startIndex);

                if (startIndex < 0) startIndex = 0;
                if (startIndex >= lines.Length) return ToolResult.Ok("(empty - offset beyond file length)");

                count = System.Math.Min(count, lines.Length - startIndex);
                content = string.Join("\n", lines, startIndex, count);
            }

            return ToolResult.Ok(content);
        }

        public Task HandlePartialAsync(ToolCall call, IUIContext ui, CancellationToken ct = default)
        {
            return Task.CompletedTask;
        }
    }
}

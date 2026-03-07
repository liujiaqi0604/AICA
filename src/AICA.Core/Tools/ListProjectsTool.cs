using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using AICA.Core.Agent;

namespace AICA.Core.Tools
{
    /// <summary>
    /// Tool for listing projects in the solution
    /// </summary>
    public class ListProjectsTool : IAgentTool
    {
        public string Name => "list_projects";
        public string Description => "List all projects in the Visual Studio solution. Shows project types, file counts, filters, and dependencies. Use this to understand solution structure. Can show details for a specific project with project_name parameter.";

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
                        ["project_name"] = new ToolParameterProperty
                        {
                            Type = "string",
                            Description = "Optional. Name of a specific project to get details about. If not provided, lists all projects."
                        },
                        ["show_files"] = new ToolParameterProperty
                        {
                            Type = "boolean",
                            Description = "Optional. If true, show file lists for each project. Default is false.",
                            Default = false
                        }
                    },
                    Required = new string[] { }
                }
            };
        }

        public Task<ToolResult> ExecuteAsync(ToolCall call, IAgentContext context, IUIContext uiContext, CancellationToken ct = default)
        {
            // Parse parameters
            string projectName = null;
            if (call.Arguments.TryGetValue("project_name", out var projNameObj) && projNameObj != null)
            {
                projectName = projNameObj.ToString();
            }

            bool showFiles = false;
            if (call.Arguments.TryGetValue("show_files", out var showFilesObj) && showFilesObj != null)
            {
                bool.TryParse(showFilesObj.ToString(), out showFiles);
            }

            // Get project information from context
            var projects = context.GetProjects();
            if (projects == null || projects.Count == 0)
            {
                return Task.FromResult(ToolResult.Ok("No projects found in the solution."));
            }

            var result = new StringBuilder();

            // If specific project requested
            if (!string.IsNullOrEmpty(projectName))
            {
                if (projects.TryGetValue(projectName, out var projectInfo))
                {
                    result.AppendLine($"Project: {projectInfo.Name}");
                    result.AppendLine($"Type: {projectInfo.ProjectType}");
                    result.AppendLine($"Path: {projectInfo.ProjectFilePath}");
                    result.AppendLine($"Directory: {projectInfo.ProjectDirectory}");
                    result.AppendLine($"Total Files: {projectInfo.SourceFiles.Count}");
                    result.AppendLine();

                    // Show filters
                    if (projectInfo.Filters.Count > 0)
                    {
                        result.AppendLine("Filters:");
                        foreach (var filter in projectInfo.Filters.OrderBy(f => f.Key))
                        {
                            result.AppendLine($"  - {filter.Key} ({filter.Value.Count} files)");
                        }
                        result.AppendLine();
                    }

                    // Show dependencies
                    if (projectInfo.Dependencies.Count > 0)
                    {
                        result.AppendLine($"Dependencies: {string.Join(", ", projectInfo.Dependencies)}");
                        result.AppendLine();
                    }

                    // Show files if requested
                    if (showFiles)
                    {
                        result.AppendLine("Files:");
                        foreach (var file in projectInfo.SourceFiles.Take(50))
                        {
                            result.AppendLine($"  - {file}");
                        }
                        if (projectInfo.SourceFiles.Count > 50)
                        {
                            result.AppendLine($"  ... and {projectInfo.SourceFiles.Count - 50} more files");
                        }
                    }
                }
                else
                {
                    result.AppendLine($"Project '{projectName}' not found.");
                    result.AppendLine();
                    result.AppendLine("Available projects:");
                    foreach (var proj in projects.Keys)
                    {
                        result.AppendLine($"  - {proj}");
                    }
                }
            }
            else
            {
                // List all projects
                result.AppendLine($"Solution contains {projects.Count} project(s):");
                result.AppendLine();

                foreach (var projectInfo in projects.Values.OrderBy(p => p.Name))
                {
                    result.AppendLine($"📁 {projectInfo.Name}");
                    result.AppendLine($"   Type: {projectInfo.ProjectType}");
                    result.AppendLine($"   Files: {projectInfo.SourceFiles.Count}");

                    if (projectInfo.Filters.Count > 0)
                    {
                        var filterSummary = string.Join(", ", projectInfo.Filters.Keys.Take(3));
                        if (projectInfo.Filters.Count > 3)
                            filterSummary += $" (+{projectInfo.Filters.Count - 3} more)";
                        result.AppendLine($"   Filters: {filterSummary}");
                    }

                    if (projectInfo.Dependencies.Count > 0)
                    {
                        result.AppendLine($"   Dependencies: {string.Join(", ", projectInfo.Dependencies)}");
                    }

                    result.AppendLine();
                }

                result.AppendLine("Use list_projects with project_name parameter to see details of a specific project.");
            }

            return Task.FromResult(ToolResult.Ok(result.ToString()));
        }

        public Task HandlePartialAsync(ToolCall call, IUIContext ui, CancellationToken ct = default)
        {
            return Task.CompletedTask;
        }
    }
}

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using AICA.Core.Agent;

namespace AICA.Core.Tools
{
    /// <summary>
    /// Tool for executing terminal commands with safety checks
    /// </summary>
    public class RunCommandTool : IAgentTool
    {
        public string Name => "run_command";
        public string Description => "Execute a terminal/shell command (e.g., 'dotnet build', 'git status', 'npm install'). Returns stdout, stderr, and exit code. Commands require user approval. Use timeout_seconds parameter for long-running commands.";

        /// <summary>
        /// Optional external command safety checker (injected by VS layer)
        /// </summary>
        public Func<string, CommandSafetyInfo> CommandSafetyChecker { get; set; }

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
                        ["command"] = new ToolParameterProperty
                        {
                            Type = "string",
                            Description = "The command to execute (e.g. 'dotnet build', 'git status')"
                        },
                        ["cwd"] = new ToolParameterProperty
                        {
                            Type = "string",
                            Description = "Working directory for the command (relative to workspace root). Defaults to workspace root."
                        },
                        ["timeout_seconds"] = new ToolParameterProperty
                        {
                            Type = "integer",
                            Description = "Maximum time to wait for command completion in seconds. Default is 30."
                        }
                    },
                    Required = new[] { "command" }
                }
            };
        }

        public async Task<ToolResult> ExecuteAsync(ToolCall call, IAgentContext context, IUIContext uiContext, CancellationToken ct = default)
        {
            if (!call.Arguments.TryGetValue("command", out var cmdObj) || cmdObj == null)
                return ToolResult.Fail("Missing required parameter: command");

            var command = cmdObj.ToString().Trim();
            if (string.IsNullOrEmpty(command))
                return ToolResult.Fail("Command cannot be empty");

            // Detect common Unix commands on Windows and provide helpful error
            if (Environment.OSVersion.Platform == PlatformID.Win32NT)
            {
                var unixCommands = new[] { "head", "tail", "grep", "find", "cat", "ls", "rm", "cp", "mv", "chmod", "chown" };
                var firstWord = command.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault()?.ToLowerInvariant();

                if (firstWord != null && unixCommands.Contains(firstWord))
                {
                    return ToolResult.Fail($"Unix command '{firstWord}' is not available on Windows. Please use the appropriate built-in tool instead:\n" +
                        "- Use 'grep_search' instead of 'grep'\n" +
                        "- Use 'find_by_name' instead of 'find'\n" +
                        "- Use 'read_file' instead of 'cat', 'head', or 'tail'\n" +
                        "- Use 'list_dir' instead of 'ls'");
                }
            }

            // Parse optional parameters
            var cwd = context.WorkingDirectory;
            if (call.Arguments.TryGetValue("cwd", out var cwdObj) && cwdObj != null)
            {
                var cwdPath = cwdObj.ToString();
                if (!string.IsNullOrWhiteSpace(cwdPath))
                {
                    if (System.IO.Path.IsPathRooted(cwdPath))
                        cwd = cwdPath;
                    else
                        cwd = System.IO.Path.Combine(context.WorkingDirectory, cwdPath);
                }
            }

            int timeoutSeconds = 30;
            if (call.Arguments.TryGetValue("timeout_seconds", out var timeoutObj) && timeoutObj != null)
                int.TryParse(timeoutObj.ToString(), out timeoutSeconds);

            // Safety check via injected checker
            if (CommandSafetyChecker != null)
            {
                var safety = CommandSafetyChecker(command);
                if (safety.IsDenied)
                    return ToolResult.Fail($"Command denied: {safety.Reason}");
            }

            // Request user confirmation
            var confirmed = await context.RequestConfirmationAsync(
                "Run Command",
                $"Execute command:\n```\n{command}\n```\nIn directory: {cwd}",
                ct);

            if (!confirmed)
                return ToolResult.Fail("Command execution cancelled by user.");

            // Execute the command
            try
            {
                var result = await ExecuteCommandAsync(command, cwd, timeoutSeconds, ct);
                return result;
            }
            catch (OperationCanceledException)
            {
                return ToolResult.Fail("Command execution was cancelled.");
            }
            catch (Exception ex)
            {
                return ToolResult.Fail($"Command execution failed: {ex.Message}");
            }
        }

        private async Task<ToolResult> ExecuteCommandAsync(string command, string workingDirectory, int timeoutSeconds, CancellationToken ct)
        {
            // Determine shell and arguments based on OS
            string shell, shellArgs;
            if (Environment.OSVersion.Platform == PlatformID.Win32NT)
            {
                shell = "cmd.exe";
                shellArgs = $"/c {command}";
            }
            else
            {
                shell = "/bin/bash";
                shellArgs = $"-c \"{command.Replace("\"", "\\\"")}\"";
            }

            var startInfo = new ProcessStartInfo
            {
                FileName = shell,
                Arguments = shellArgs,
                WorkingDirectory = workingDirectory,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                // On Windows, cmd.exe pipes output in OEM codepage (e.g. CP936 for Chinese)
                // On other platforms, use UTF-8
                StandardOutputEncoding = Environment.OSVersion.Platform == PlatformID.Win32NT
                    ? Encoding.GetEncoding(System.Globalization.CultureInfo.CurrentCulture.TextInfo.OEMCodePage)
                    : Encoding.UTF8,
                StandardErrorEncoding = Environment.OSVersion.Platform == PlatformID.Win32NT
                    ? Encoding.GetEncoding(System.Globalization.CultureInfo.CurrentCulture.TextInfo.OEMCodePage)
                    : Encoding.UTF8
            };

            var stdoutBuilder = new StringBuilder();
            var stderrBuilder = new StringBuilder();

            using (var process = new Process { StartInfo = startInfo })
            {
                process.OutputDataReceived += (s, e) =>
                {
                    if (e.Data != null && stdoutBuilder.Length < 16000)
                        stdoutBuilder.AppendLine(e.Data);
                };

                process.ErrorDataReceived += (s, e) =>
                {
                    if (e.Data != null && stderrBuilder.Length < 8000)
                        stderrBuilder.AppendLine(e.Data);
                };

                process.Start();
                process.BeginOutputReadLine();
                process.BeginErrorReadLine();

                // Wait with timeout and cancellation
                var timeoutMs = timeoutSeconds * 1000;
                var completed = await Task.Run(() => process.WaitForExit(timeoutMs), ct).ConfigureAwait(false);

                if (!completed)
                {
                    try { process.Kill(); } catch { }
                    return ToolResult.Fail($"Command timed out after {timeoutSeconds} seconds.\n\nPartial stdout:\n{Truncate(stdoutBuilder.ToString(), 2000)}\n\nPartial stderr:\n{Truncate(stderrBuilder.ToString(), 1000)}");
                }

                var exitCode = process.ExitCode;
                var stdout = stdoutBuilder.ToString();
                var stderr = stderrBuilder.ToString();

                var resultBuilder = new StringBuilder();
                resultBuilder.AppendLine($"Exit code: {exitCode}");

                if (!string.IsNullOrWhiteSpace(stdout))
                {
                    resultBuilder.AppendLine("\nstdout:");
                    resultBuilder.AppendLine(Truncate(stdout, 6000));
                }

                if (!string.IsNullOrWhiteSpace(stderr))
                {
                    resultBuilder.AppendLine("\nstderr:");
                    resultBuilder.AppendLine(Truncate(stderr, 3000));
                }

                if (exitCode == 0)
                    return ToolResult.Ok(resultBuilder.ToString());
                else
                    return ToolResult.Fail(resultBuilder.ToString());
            }
        }

        private string Truncate(string text, int maxLength)
        {
            if (string.IsNullOrEmpty(text) || text.Length <= maxLength) return text;
            return text.Substring(0, maxLength) + "\n... (truncated)";
        }

        public Task HandlePartialAsync(ToolCall call, IUIContext ui, CancellationToken ct = default)
        {
            return Task.CompletedTask;
        }
    }

    /// <summary>
    /// Command safety check result (used by VS layer to inject SafetyGuard checks)
    /// </summary>
    public class CommandSafetyInfo
    {
        public bool IsDenied { get; set; }
        public bool RequiresApproval { get; set; }
        public string Reason { get; set; }

        public static CommandSafetyInfo Allow() => new CommandSafetyInfo();
        public static CommandSafetyInfo Deny(string reason) => new CommandSafetyInfo { IsDenied = true, Reason = reason };
        public static CommandSafetyInfo NeedApproval(string reason) => new CommandSafetyInfo { RequiresApproval = true, Reason = reason };
    }
}

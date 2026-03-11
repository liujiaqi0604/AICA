using Markdig;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using AICA.Options;
using AICA.Core.LLM;
using AICA.Core.Agent;
using AICA.Core.Tools;
using AICA.Core.Storage;
using AICA.Agent;
using EnvDTE80;

namespace AICA.ToolWindows
{
    public partial class ChatToolWindowControl : UserControl
    {
        private readonly MarkdownPipeline _markdownPipeline;
        private readonly List<ConversationMessage> _conversation = new List<ConversationMessage>();
        private readonly List<ChatMessage> _llmHistory = new List<ChatMessage>();
        private bool _isBrowserReady;
        private bool _isSending;
        private OpenAIClient _llmClient;
        private CancellationTokenSource _currentCts;
        private bool _agentMode = true; // Default to Agent mode
        private AgentExecutor _agentExecutor;
        private ToolDispatcher _toolDispatcher;
        private VSAgentContext _agentContext;
        private VSUIContext _uiContext;
        private readonly ConversationStorage _conversationStorage = new ConversationStorage();
        private string _currentConversationId;
        private int _globalToolCallCounter = 0; // Global counter for unique tool call IDs across all conversation turns
        private DTE2 _dte; // Visual Studio DTE for project information
        private EnvDTE.SolutionEvents _solutionEvents; // Solution events for project switching detection
        private bool _isSidebarOpen = false; // Track sidebar state
        private List<ConversationViewModel> _allConversations = new List<ConversationViewModel>(); // Cache all conversations
        private string _lastProjectPath = null; // Track last project path to detect project switching

        public ChatToolWindowControl()
        {
            InitializeComponent();

            _markdownPipeline = new MarkdownPipelineBuilder()
                .UseAdvancedExtensions()
                .UseSoftlineBreakAsHardlineBreak()
                .Build();

            ChatBrowser.LoadCompleted += ChatBrowser_LoadCompleted;
            ChatBrowser.Navigating += ChatBrowser_Navigating;
            ChatBrowser.NavigateToString(BuildPageHtml(string.Empty));

            Loaded += ChatToolWindowControl_Loaded;
            Unloaded += ChatToolWindowControl_Unloaded;

            // Get DTE2 instance
            ThreadHelper.ThrowIfNotOnUIThread();
            _dte = Package.GetGlobalService(typeof(EnvDTE.DTE)) as DTE2;

            // Subscribe to solution events for automatic project switching
            if (_dte != null)
            {
                _solutionEvents = _dte.Events.SolutionEvents;
                _solutionEvents.Opened += OnSolutionOpened;
                _solutionEvents.AfterClosing += OnSolutionClosed;
            }
        }

        private async void ChatToolWindowControl_Unloaded(object sender, RoutedEventArgs e)
        {
            // Auto-save conversation when window is unloaded
            await SaveConversationAsync();

            // Unsubscribe from solution events
            if (_solutionEvents != null)
            {
                _solutionEvents.Opened -= OnSolutionOpened;
                _solutionEvents.AfterClosing -= OnSolutionClosed;
            }
        }

        private void OnSolutionOpened()
        {
            // Handle solution opened event - switch to new project's conversation list
            ThreadHelper.JoinableTaskFactory.RunAsync(async () =>
            {
                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();

                // Save current conversation before switching
                await SaveConversationAsync();

                // Clear current conversation and start fresh
                ClearConversation();

                // Update last project path
                _lastProjectPath = GetCurrentProjectPath();

                // If sidebar is open, refresh the conversation list
                if (_isSidebarOpen)
                {
                    await LoadConversationListAsync();
                }

                System.Diagnostics.Debug.WriteLine("[AICA] Solution opened - conversation list refreshed");
            }).FireAndForget();
        }

        private void OnSolutionClosed()
        {
            // Handle solution closed event
            ThreadHelper.JoinableTaskFactory.RunAsync(async () =>
            {
                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();

                // Save current conversation before closing
                await SaveConversationAsync();

                // Clear current conversation
                ClearConversation();

                // Clear last project path
                _lastProjectPath = null;

                // Clear conversation list if sidebar is open
                if (_isSidebarOpen)
                {
                    _allConversations.Clear();
                    ConversationListBox.ItemsSource = null;
                }

                System.Diagnostics.Debug.WriteLine("[AICA] Solution closed - conversation saved");
            }).FireAndForget();
        }

        private void ChatToolWindowControl_Loaded(object sender, RoutedEventArgs e)
        {
            // Run path mismatch check on load (independent of agent initialization)
            try
            {
                ThreadHelper.JoinableTaskFactory.Run(async () =>
                {
                    await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                    var dte = await AsyncServiceProvider.GlobalProvider.GetServiceAsync(typeof(EnvDTE.DTE)) as DTE2;
                    if (dte?.Solution == null || string.IsNullOrEmpty(dte.Solution.FullName))
                        return;

                    var slnDir = System.IO.Path.GetDirectoryName(dte.Solution.FullName);
                    var cmakeCachePath = System.IO.Path.Combine(slnDir, "CMakeCache.txt");
                    if (!System.IO.File.Exists(cmakeCachePath))
                        return;

                    string cmakeHomeDir = null;
                    using (var reader = new System.IO.StreamReader(cmakeCachePath))
                    {
                        string line;
                        while ((line = reader.ReadLine()) != null)
                        {
                            if (line.StartsWith("CMAKE_HOME_DIRECTORY:INTERNAL=", StringComparison.OrdinalIgnoreCase))
                            {
                                cmakeHomeDir = line.Substring("CMAKE_HOME_DIRECTORY:INTERNAL=".Length).Trim();
                                cmakeHomeDir = cmakeHomeDir.Replace('/', '\\');
                                break;
                            }
                        }
                    }

                    if (string.IsNullOrEmpty(cmakeHomeDir))
                        return;

                    var workingParent = System.IO.Path.GetDirectoryName(slnDir.TrimEnd('\\', '/'));
                    var cmakeParent = System.IO.Path.GetDirectoryName(cmakeHomeDir.TrimEnd('\\', '/'));

                    bool isUnderSameRoot = false;
                    if (!string.IsNullOrEmpty(workingParent) && !string.IsNullOrEmpty(cmakeParent))
                    {
                        var wpNorm = workingParent.TrimEnd('\\', '/') + "\\";
                        var cpNorm = cmakeParent.TrimEnd('\\', '/') + "\\";
                        isUnderSameRoot =
                            wpNorm.Equals(cpNorm, StringComparison.OrdinalIgnoreCase) ||
                            cmakeHomeDir.StartsWith(wpNorm, StringComparison.OrdinalIgnoreCase) ||
                            slnDir.StartsWith(cpNorm, StringComparison.OrdinalIgnoreCase);
                    }

                    if (!isUnderSameRoot)
                    {
                        WarningText.Text =
                            $"\u5f53\u524d\u9879\u76ee\u4e0d\u5728\u539f\u59cb\u7f16\u8bd1\u76ee\u5f55\u4e2d\u6253\u5f00\uff1a\n" +
                            $"\u539f\u59cb\u6e90\u7801\u76ee\u5f55: {cmakeHomeDir}\n" +
                            $"\u5f53\u524d\u5de5\u4f5c\u76ee\u5f55: {slnDir}\n\n" +
                            $"AICA \u53ef\u80fd\u65e0\u6cd5\u6b63\u786e\u89e3\u6790\u6e90\u7801\u6587\u4ef6\u8def\u5f84\u3002\n" +
                            $"\u8bf7\u5728\u539f\u59cb\u7f16\u8bd1\u76ee\u5f55\u4e2d\u6253\u5f00\u89e3\u51b3\u65b9\u6848\uff0c\u6216\u91cd\u65b0\u8fd0\u884c CMake \u751f\u6210\u4ee5\u66f4\u65b0\u8def\u5f84\u3002";
                        WarningBanner.Visibility = Visibility.Visible;
                    }
                });
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[AICA] Path mismatch check on load failed: {ex.Message}");
            }
        }

        private void ChatBrowser_LoadCompleted(object sender, System.Windows.Navigation.NavigationEventArgs e)
        {
            _isBrowserReady = true;
        }

        private void ChatBrowser_Navigating(object sender, System.Windows.Navigation.NavigatingCancelEventArgs e)
        {
            // Intercept feedback navigation
            if (e.Uri != null && e.Uri.Scheme == "aica" && e.Uri.Host == "feedback")
            {
                e.Cancel = true; // Prevent actual navigation

                try
                {
                    // Parse query parameters
                    var query = e.Uri.Query.TrimStart('?');
                    var parameters = System.Web.HttpUtility.ParseQueryString(query);
                    var messageIdStr = parameters["messageId"];
                    var feedback = parameters["feedback"];

                    if (int.TryParse(messageIdStr, out int messageId))
                    {
                        RecordFeedback(messageId, feedback);
                    }
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"[AICA] Error handling feedback navigation: {ex.Message}");
                }
            }
        }

        public void UpdateContent(string content)
        {
            var html = Markdig.Markdown.ToHtml(content ?? string.Empty, _markdownPipeline);
            UpdateBrowserContent($"<div class=\"message assistant\"><div class=\"role\">AI</div><div class=\"content\">{html}</div></div>");
        }

        public void AppendMessage(string role, string content)
        {
            _conversation.Add(new ConversationMessage { Role = role, Content = content });
            RenderConversation();
        }

        /// <summary>
        /// Send a message programmatically (from right-click commands) and trigger LLM response
        /// </summary>
        public async System.Threading.Tasks.Task SendProgrammaticMessageAsync(string userMessage)
        {
            if (_isSending || string.IsNullOrWhiteSpace(userMessage)) return;

            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            InputTextBox.Text = userMessage;
            await SendMessageAsync();
        }

        public void ClearConversation()
        {
            _conversation.Clear();
            _llmHistory.Clear();
            _llmClient?.Dispose();
            _llmClient = null;
            _agentExecutor = null;
            _toolDispatcher = null;
            _agentContext = null;
            _uiContext = null;
            WarningBanner.Visibility = Visibility.Collapsed;
            _currentConversationId = null;
            UpdateBrowserContent(string.Empty);
        }

        /// <summary>
        /// Public method to save current conversation (called from ChatToolWindow)
        /// </summary>
        public async System.Threading.Tasks.Task SaveCurrentConversationAsync()
        {
            await SaveConversationAsync();
        }

        private void InitializeAgentComponents(GeneralOptions options)
        {
            // Initialize tool dispatcher with available tools
            _toolDispatcher = new ToolDispatcher();
            _toolDispatcher.RegisterTool(new ReadFileTool());
            _toolDispatcher.RegisterTool(new WriteFileTool());
            _toolDispatcher.RegisterTool(new EditFileTool());
            _toolDispatcher.RegisterTool(new ListDirTool());
            _toolDispatcher.RegisterTool(new GrepSearchTool());
            _toolDispatcher.RegisterTool(new FindByNameTool());
            _toolDispatcher.RegisterTool(new RunCommandTool());
            _toolDispatcher.RegisterTool(new UpdatePlanTool());
            _toolDispatcher.RegisterTool(new AttemptCompletionTool());
            _toolDispatcher.RegisterTool(new CondenseTool());
            _toolDispatcher.RegisterTool(new ListCodeDefinitionsTool());
            _toolDispatcher.RegisterTool(new AskFollowupQuestionTool());
            _toolDispatcher.RegisterTool(new ListProjectsTool());

            // Initialize LLM client
            var clientOptions = new LLMClientOptions
            {
                ApiEndpoint = options.ApiEndpoint,
                ApiKey = options.ApiKey,
                Model = options.ModelName,
                MaxTokens = options.MaxTokens,
                Temperature = options.Temperature,
                TopP = options.TopP,
                TopK = options.TopK,
                TimeoutSeconds = options.RequestTimeoutSeconds,
                Stream = true
            };
            _llmClient = new OpenAIClient(clientOptions);

            // Initialize VS-specific contexts
            ThreadHelper.JoinableTaskFactory.Run(async () =>
            {
                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                var dte = await AsyncServiceProvider.GlobalProvider.GetServiceAsync(typeof(EnvDTE.DTE)) as DTE2;

                // Create AgentContext first with confirmation handler
                _agentContext = new VSAgentContext(
                    dte,
                    workingDirectory: null,
                    confirmationHandler: async (title, message, ct) =>
                    {
                        try
                        {
                            System.Diagnostics.Debug.WriteLine($"[AICA] AgentContext confirmationHandler called: title={title}");
                            System.Diagnostics.Debug.WriteLine($"[AICA] AgentContext confirmationHandler message: {message}");

                            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(ct);
                            System.Diagnostics.Debug.WriteLine($"[AICA] Switched to main thread");

                            var result = VsShellUtilities.ShowMessageBox(
                                ServiceProvider.GlobalProvider,
                                message,
                                title,
                                OLEMSGICON.OLEMSGICON_QUERY,
                                OLEMSGBUTTON.OLEMSGBUTTON_YESNO,
                                OLEMSGDEFBUTTON.OLEMSGDEFBUTTON_FIRST);

                            System.Diagnostics.Debug.WriteLine($"[AICA] MessageBox result: {result}");
                            return result == 6; // IDYES = 6
                        }
                        catch (Exception ex)
                        {
                            System.Diagnostics.Debug.WriteLine($"[AICA] AgentContext confirmationHandler exception: {ex.Message}");
                            System.Diagnostics.Debug.WriteLine($"[AICA] Stack trace: {ex.StackTrace}");
                            return false;
                        }
                    });

                // Create UIContext with all handlers
                _uiContext = new VSUIContext(
                    streamingContentUpdater: content =>
                    {
                        ThreadHelper.JoinableTaskFactory.Run(async () =>
                        {
                            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                            RenderConversation(content);
                        });
                    },
                    confirmationHandler: async (title, message, ct) =>
                    {
                        try
                        {
                            System.Diagnostics.Debug.WriteLine($"[AICA] confirmationHandler called: title={title}");
                            System.Diagnostics.Debug.WriteLine($"[AICA] confirmationHandler message: {message}");

                            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(ct);
                            System.Diagnostics.Debug.WriteLine($"[AICA] Switched to main thread");

                            var result = VsShellUtilities.ShowMessageBox(
                                ServiceProvider.GlobalProvider,
                                message,
                                title,
                                OLEMSGICON.OLEMSGICON_QUERY,
                                OLEMSGBUTTON.OLEMSGBUTTON_YESNO,
                                OLEMSGDEFBUTTON.OLEMSGDEFBUTTON_FIRST);

                            System.Diagnostics.Debug.WriteLine($"[AICA] MessageBox result: {result}");
                            return result == 6; // IDYES = 6
                        }
                        catch (Exception ex)
                        {
                            System.Diagnostics.Debug.WriteLine($"[AICA] confirmationHandler exception: {ex.Message}");
                            System.Diagnostics.Debug.WriteLine($"[AICA] Stack trace: {ex.StackTrace}");
                            return false;
                        }
                    },
                    diffPreviewHandler: async (filePath, originalContent, newContent, ct) =>
                    {
                        return await _agentContext.ShowDiffPreviewAsync(filePath, originalContent, newContent, ct);
                    });

                // Check for path mismatch warning (project opened from non-original location)
                if (!string.IsNullOrEmpty(_agentContext.PathMismatchWarning))
                {
                    WarningText.Text = _agentContext.PathMismatchWarning;
                    WarningBanner.Visibility = Visibility.Visible;
                }
            });

            // Initialize Agent executor with custom instructions and token budget from options
            // Token budget: maxTokens * 8 gives rough context window size (response tokens vs context tokens)
            int tokenBudget = Math.Max(8000, options.MaxTokens * 8);
            _agentExecutor = new AgentExecutor(
                _llmClient,
                _toolDispatcher,
                maxIterations: options.MaxAgentIterations,
                maxTokenBudget: tokenBudget,
                customInstructions: options.CustomInstructions);
        }

        /// <summary>
        /// Render conversation with interleaved tool calls and text
        /// </summary>
        private void RenderConversationStreamingInterleaved(List<ToolCallBlock> toolCallBlocks, ToolCallBlock currentBlock, string remainingText)
        {
            var bodyBuilder = new StringBuilder();

            // Render all previous conversation messages
            for (int i = 0; i < _conversation.Count; i++)
            {
                var message = _conversation[i];
                var roleClass = message.Role == "user" ? "user" : "assistant";
                var roleName = message.Role == "user" ? "You" : "AI";

                // Check if this is a completion message
                if (message.Role == "assistant" && !string.IsNullOrEmpty(message.CompletionData))
                {
                    var completionResult = CompletionResult.Deserialize(message.CompletionData);
                    if (completionResult != null)
                    {
                        bodyBuilder.AppendLine($"<div class=\"message {roleClass}\">");
                        bodyBuilder.AppendLine($"<div class=\"role\">{roleName}</div>");

                        if (!string.IsNullOrEmpty(message.ToolLogsHtml))
                        {
                            bodyBuilder.AppendLine($"<div class=\"content\">{message.ToolLogsHtml}</div>");
                        }

                        if (!string.IsNullOrEmpty(message.Content))
                        {
                            var contentHtml = Markdig.Markdown.ToHtml(message.Content, _markdownPipeline);
                            bodyBuilder.AppendLine($"<div class=\"content\">{contentHtml}</div>");
                        }

                        bodyBuilder.AppendLine(BuildCompletionCardHtml(completionResult.Summary, completionResult.Command, i));
                        bodyBuilder.AppendLine("</div>");
                        continue;
                    }
                }

                // Regular message rendering
                if (message.Content != null && message.Content.Contains("<div class=\"tool-call-container\">"))
                {
                    bodyBuilder.AppendLine($"<div class=\"message {roleClass}\"><div class=\"role\">{roleName}</div><div class=\"content\">{message.Content}</div></div>");
                }
                else
                {
                    var html = Markdig.Markdown.ToHtml(message.Content ?? string.Empty, _markdownPipeline);
                    bodyBuilder.AppendLine($"<div class=\"message {roleClass}\"><div class=\"role\">{roleName}</div><div class=\"content\">{html}</div></div>");
                }
            }

            // Render streaming message with interleaved tool blocks
            if (toolCallBlocks.Count > 0 || !string.IsNullOrEmpty(remainingText))
            {
                bodyBuilder.AppendLine("<div class=\"message assistant streaming\"><div class=\"role\">AI</div><div class=\"content\">");

                // Render each tool call block with its associated text
                foreach (var block in toolCallBlocks)
                {
                    // Render tool call HTML
                    bodyBuilder.AppendLine(block.ToolHtml);

                    // Render text that came after this tool call
                    if (block.TextAfter.Length > 0)
                    {
                        var textHtml = Markdig.Markdown.ToHtml(block.TextAfter.ToString(), _markdownPipeline);
                        bodyBuilder.AppendLine(textHtml);
                    }
                }

                // Render any remaining text that hasn't been associated with a tool block yet
                // (this happens for non-tool responses or text before the first tool call)
                if (!string.IsNullOrEmpty(remainingText) && currentBlock == null)
                {
                    var streamingHtml = Markdig.Markdown.ToHtml(remainingText, _markdownPipeline);
                    bodyBuilder.AppendLine(streamingHtml);
                }

                bodyBuilder.AppendLine("</div></div>");
            }

            UpdateBrowserContent(bodyBuilder.ToString());
        }

        /// <summary>
        /// Build final HTML from interleaved tool call blocks (for persisting to conversation)
        /// </summary>
        private string BuildInterleavedToolLogsHtml(List<ToolCallBlock> toolCallBlocks)
        {
            var html = new StringBuilder();

            foreach (var block in toolCallBlocks)
            {
                // Add tool call HTML
                html.AppendLine(block.ToolHtml);

                // Add text that came after this tool call
                if (block.TextAfter.Length > 0)
                {
                    var textHtml = Markdig.Markdown.ToHtml(block.TextAfter.ToString(), _markdownPipeline);
                    html.AppendLine(textHtml);
                }
            }

            return html.ToString();
        }

        private void RenderConversationStreaming(string toolLogsHtml, string streamingMarkdown)
        {
            var bodyBuilder = new StringBuilder();

            for (int i = 0; i < _conversation.Count; i++)
            {
                var message = _conversation[i];
                var roleClass = message.Role == "user" ? "user" : "assistant";
                var roleName = message.Role == "user" ? "You" : "AI";

                // Check if this is a completion message
                if (message.Role == "assistant" && !string.IsNullOrEmpty(message.CompletionData))
                {
                    var completionResult = CompletionResult.Deserialize(message.CompletionData);
                    if (completionResult != null)
                    {
                        // Render final layout in the desired order:
                        // 1. tool logs 2. streamed body content 3. completion card
                        bodyBuilder.AppendLine($"<div class=\"message {roleClass}\">");
                        bodyBuilder.AppendLine($"<div class=\"role\">{roleName}</div>");

                        if (!string.IsNullOrEmpty(message.ToolLogsHtml))
                        {
                            bodyBuilder.AppendLine($"<div class=\"content\">{message.ToolLogsHtml}</div>");
                        }

                        if (!string.IsNullOrEmpty(message.Content))
                        {
                            var contentHtml = Markdig.Markdown.ToHtml(message.Content, _markdownPipeline);
                            bodyBuilder.AppendLine($"<div class=\"content\">{contentHtml}</div>");
                        }

                        bodyBuilder.AppendLine(BuildCompletionCardHtml(completionResult.Summary, completionResult.Command, i));
                        bodyBuilder.AppendLine("</div>");
                        continue;
                    }
                }

                // Regular message rendering
                // Check if content contains tool call HTML (starts with <div class="tool-call-container">)
                if (message.Content != null && message.Content.Contains("<div class=\"tool-call-container\">"))
                {
                    // Content contains raw HTML, don't process through Markdown
                    bodyBuilder.AppendLine($"<div class=\"message {roleClass}\"><div class=\"role\">{roleName}</div><div class=\"content\">{message.Content}</div></div>");
                }
                else
                {
                    // Regular markdown content
                    var html = Markdig.Markdown.ToHtml(message.Content ?? string.Empty, _markdownPipeline);
                    bodyBuilder.AppendLine($"<div class=\"message {roleClass}\"><div class=\"role\">{roleName}</div><div class=\"content\">{html}</div></div>");
                }
            }

            if (!string.IsNullOrEmpty(toolLogsHtml) || !string.IsNullOrEmpty(streamingMarkdown))
            {
                bodyBuilder.AppendLine("<div class=\"message assistant streaming\"><div class=\"role\">AI</div><div class=\"content\">");

                if (!string.IsNullOrEmpty(toolLogsHtml))
                {
                    bodyBuilder.AppendLine(toolLogsHtml);
                }

                if (!string.IsNullOrEmpty(streamingMarkdown))
                {
                    var streamingHtml = Markdig.Markdown.ToHtml(streamingMarkdown, _markdownPipeline);
                    bodyBuilder.AppendLine(streamingHtml);
                }

                bodyBuilder.AppendLine("</div></div>");
            }

            UpdateBrowserContent(bodyBuilder.ToString());
        }

        private void RenderConversation(string streamingContent = null)
        {
            var bodyBuilder = new StringBuilder();

            for (int i = 0; i < _conversation.Count; i++)
            {
                var message = _conversation[i];
                var roleClass = message.Role == "user" ? "user" : "assistant";
                var roleName = message.Role == "user" ? "You" : "AI";

                // Check if this is a completion message
                if (message.Role == "assistant" && !string.IsNullOrEmpty(message.CompletionData))
                {
                    var completionResult = CompletionResult.Deserialize(message.CompletionData);
                    if (completionResult != null)
                    {
                        // Render final layout in the desired order:
                        // 1. tool logs 2. streamed body content 3. completion card
                        bodyBuilder.AppendLine($"<div class=\"message {roleClass}\">");
                        bodyBuilder.AppendLine($"<div class=\"role\">{roleName}</div>");

                        if (!string.IsNullOrEmpty(message.ToolLogsHtml))
                        {
                            bodyBuilder.AppendLine($"<div class=\"content\">{message.ToolLogsHtml}</div>");
                        }

                        if (!string.IsNullOrEmpty(message.Content))
                        {
                            var contentHtml = Markdig.Markdown.ToHtml(message.Content, _markdownPipeline);
                            bodyBuilder.AppendLine($"<div class=\"content\">{contentHtml}</div>");
                        }

                        bodyBuilder.AppendLine(BuildCompletionCardHtml(completionResult.Summary, completionResult.Command, i));
                        bodyBuilder.AppendLine("</div>");
                        continue;
                    }
                }

                // Regular message rendering
                // Check if content contains tool call HTML (starts with <div class="tool-call-container">)
                if (message.Content != null && message.Content.Contains("<div class=\"tool-call-container\">"))
                {
                    // Content contains raw HTML, don't process through Markdown
                    bodyBuilder.AppendLine($"<div class=\"message {roleClass}\"><div class=\"role\">{roleName}</div><div class=\"content\">{message.Content}</div></div>");
                }
                else
                {
                    // Regular markdown content
                    var html = Markdig.Markdown.ToHtml(message.Content ?? string.Empty, _markdownPipeline);
                    bodyBuilder.AppendLine($"<div class=\"message {roleClass}\"><div class=\"role\">{roleName}</div><div class=\"content\">{html}</div></div>");
                }
            }

            if (!string.IsNullOrEmpty(streamingContent))
            {
                var streamingHtml = Markdig.Markdown.ToHtml(streamingContent, _markdownPipeline);
                bodyBuilder.AppendLine($"<div class=\"message assistant streaming\"><div class=\"role\">AI</div><div class=\"content\">{streamingHtml}</div></div>");
            }

            UpdateBrowserContent(bodyBuilder.ToString());
        }

        private void UpdateBrowserContent(string innerHtml)
        {
            if (!_isBrowserReady || ChatBrowser.Document == null)
            {
                ChatBrowser.NavigateToString(BuildPageHtml(innerHtml));
                return;
            }

            try
            {
                dynamic doc = ChatBrowser.Document;
                dynamic log = doc?.getElementById("chat-log");
                if (log != null)
                {
                    log.innerHTML = innerHtml;
                    dynamic window = doc?.parentWindow;
                    window?.scrollTo(0, doc?.body?.scrollHeight ?? 0);
                    return;
                }
            }
            catch { }

            ChatBrowser.NavigateToString(BuildPageHtml(innerHtml));
        }

        private async void SendButton_Click(object sender, RoutedEventArgs e)
        {
            await SendMessageAsync();
        }

        private async void InputTextBox_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter && Keyboard.Modifiers == ModifierKeys.None)
            {
                e.Handled = true;
                await SendMessageAsync();
            }
        }

        private async System.Threading.Tasks.Task SendMessageAsync()
        {
            var userMessage = InputTextBox.Text.Trim();
            if (string.IsNullOrWhiteSpace(userMessage)) return;

            if (_isSending) return;

            _isSending = true;
            InputTextBox.IsEnabled = false;
            SendButton.IsEnabled = false;
            _currentCts = new CancellationTokenSource();

            // Check if this is the first message (for title update)
            bool isFirstMessage = _conversation.Count == 0;

            try
            {
                InputTextBox.Text = string.Empty;
                AppendMessage("user", userMessage);

                var options = await GeneralOptions.GetLiveInstanceAsync();

                if (string.IsNullOrEmpty(options.ApiEndpoint))
                {
                    AppendMessage("assistant", "⚠️ Please configure the LLM API endpoint in Tools > Options > AICA > General");
                    return;
                }

                // Initialize components if needed
                if (_llmClient == null)
                {
                    InitializeAgentComponents(options);
                }

                // Use Agent mode only if tool calling is enabled
                if (_agentMode && _agentExecutor != null && options.EnableToolCalling)
                {
                    await ExecuteAgentModeAsync(userMessage);
                }
                else
                {
                    await ExecuteChatModeAsync(userMessage);
                }

                // Auto-save conversation after each exchange
                await SaveConversationAsync();

                // Update conversation title if this is the first message
                if (isFirstMessage)
                {
                    await UpdateConversationTitleAsync(userMessage);
                }
            }
            catch (OperationCanceledException)
            {
                AppendMessage("assistant", "🛑 Request cancelled.");
            }
            catch (LLMException ex)
            {
                AppendMessage("assistant", $"❌ LLM Error: {ex.Message}");
            }
            catch (Exception ex)
            {
                AppendMessage("assistant", $"❌ Error: {ex.Message}");
            }
            finally
            {
                _isSending = false;
                _currentCts?.Dispose();
                _currentCts = null;
                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                InputTextBox.IsEnabled = true;
                SendButton.IsEnabled = true;
                InputTextBox.Focus();
            }
        }

        /// <summary>
        /// Represents a tool call block with its associated text response
        /// </summary>
        private class ToolCallBlock
        {
            public string ToolHtml { get; set; }
            public StringBuilder TextAfter { get; set; } = new StringBuilder();
            public int ToolId { get; set; }
            public string ToolCallId { get; set; }
        }

        /// <summary>
        /// Update conversation title based on first user message
        /// </summary>
        private async System.Threading.Tasks.Task UpdateConversationTitleAsync(string firstMessage)
        {
            if (string.IsNullOrEmpty(_currentConversationId))
                return;

            try
            {
                // Truncate message to max 50 characters for title
                var title = firstMessage.Length > 50 ? firstMessage.Substring(0, 50) + "..." : firstMessage;

                // Load conversation record
                var record = await _conversationStorage.LoadConversationAsync(_currentConversationId);
                if (record != null)
                {
                    record.Title = title;
                    await _conversationStorage.SaveConversationAsync(record);

                    // Update in UI list (INotifyPropertyChanged will handle UI update)
                    var viewModel = _allConversations.FirstOrDefault(c => c.Id == _currentConversationId);
                    if (viewModel != null)
                    {
                        viewModel.Title = title;
                    }

                    System.Diagnostics.Debug.WriteLine($"[AICA] Updated conversation title: {title}");
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[AICA] Failed to update conversation title: {ex.Message}");
            }
        }

        /// <summary>
        /// View model for conversation list item
        /// </summary>
        private class ConversationViewModel : System.ComponentModel.INotifyPropertyChanged
        {
            private string _title;

            public string Id { get; set; }

            public string Title
            {
                get => _title;
                set
                {
                    if (_title != value)
                    {
                        _title = value;
                        PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(nameof(Title)));
                        PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(nameof(DisplayInfo)));
                    }
                }
            }

            public string TimeAgo { get; set; }
            public DateTimeOffset UpdatedAt { get; set; }
            public string ProjectName { get; set; } // For /allresume mode

            public string DisplayInfo
            {
                get
                {
                    if (!string.IsNullOrEmpty(ProjectName))
                    {
                        return $"{ProjectName} | {TimeAgo}";
                    }
                    return TimeAgo;
                }
            }

            public event System.ComponentModel.PropertyChangedEventHandler PropertyChanged;
        }

        private async System.Threading.Tasks.Task ExecuteAgentModeAsync(string userMessage)
        {
            var responseBuilder = new StringBuilder();
            var hasToolCalls = false;
            var pendingToolCalls = new Dictionary<string, (ToolCall call, int id)>();

            // NEW: Track tool call blocks in order, each with its associated text
            var toolCallBlocks = new List<ToolCallBlock>();
            ToolCallBlock currentBlock = null; // Current block being built

            // Add user message to history BEFORE executing agent
            // This ensures the next turn can see this message in previousMessages
            _llmHistory.Add(ChatMessage.User(userMessage));

            // Show immediate feedback while waiting for LLM's first token (TTFT)
            RenderConversation("💭 *思考中...*");

            // Capture the WPF dispatcher so we can marshal UI updates from background thread
            var dispatcher = this.Dispatcher;

            // Run the entire agent loop on a background thread to prevent UI deadlocks.
            // The IAsyncEnumerable pattern causes MoveNextAsync() to resume the generator
            // on the caller's thread. If the caller is the UI thread, all awaits in the
            // generator chain (AgentExecutor -> OpenAIClient -> HttpClient) capture the UI
            // sync context, causing deadlocks when tool execution tries to post back.
            await System.Threading.Tasks.Task.Run(async () =>
            {
                // Pass previous conversation history to Agent (excluding current user message which is already in history)
                // We pass all messages except the system prompt (which will be regenerated)
                var previousMessages = _llmHistory
                    .Where(m => m.Role != ChatRole.System)
                    .ToList();

                await foreach (var step in _agentExecutor.ExecuteAsync(userMessage, _agentContext, _uiContext, previousMessages, _currentCts.Token))
                {
                    // Marshal UI updates to the UI thread via Dispatcher.Invoke.
                    // This blocks the background thread until the UI processes the update,
                    // ensuring sequential rendering. The UI thread is free (awaiting Task.Run).
                    dispatcher.Invoke(new Action(() =>
                    {
                        switch (step.Type)
                        {
                            case AgentStepType.TextChunk:
                                responseBuilder.Append(step.Text);

                                // NEW: If we have a current tool block, append text to it
                                // Otherwise, append to the global response builder (for non-tool responses)
                                if (currentBlock != null)
                                {
                                    currentBlock.TextAfter.Append(step.Text);
                                }

                                // Render with interleaved tool blocks and text
                                RenderConversationStreamingInterleaved(toolCallBlocks, currentBlock, responseBuilder.ToString());
                                break;

                            case AgentStepType.ToolStart:
                                // When first tool arrives: discard any buffered pre-tool text
                                if (!hasToolCalls)
                                {
                                    if (responseBuilder.Length > 100)
                                    {
                                        System.Diagnostics.Debug.WriteLine($"[AICA-UI] Discarding {responseBuilder.Length} chars of pre-tool text");
                                    }
                                    responseBuilder.Clear();
                                }
                                hasToolCalls = true;

                                // Don't show attempt_completion in tool logs (its text becomes the main response)
                                if (step.ToolCall.Name != "attempt_completion")
                                {
                                    var toolId = _globalToolCallCounter++;
                                    pendingToolCalls[step.ToolCall.Id] = (step.ToolCall, toolId);

                                    // Generate enhanced tool call HTML (without result yet)
                                    var toolHtml = BuildToolCallHtml(
                                        step.ToolCall.Name,
                                        step.ToolCall.Arguments,
                                        null,
                                        true,
                                        toolId
                                    );

                                    // NEW: Create a new tool call block
                                    currentBlock = new ToolCallBlock
                                    {
                                        ToolHtml = toolHtml,
                                        ToolId = toolId,
                                        ToolCallId = step.ToolCall.Id
                                    };
                                    toolCallBlocks.Add(currentBlock);
                                }

                                RenderConversationStreamingInterleaved(toolCallBlocks, currentBlock, responseBuilder.ToString());
                                break;

                            case AgentStepType.ToolResult:
                                // Skip attempt_completion results in tool log (shown as main response)
                                if (step.ToolCall?.Name == "attempt_completion")
                                {
                                    break;
                                }

                                // Update the tool call with result
                                if (pendingToolCalls.TryGetValue(step.ToolCall.Id, out var toolInfo))
                                {
                                    var resultText = step.Result.Success ? step.Result.Content : step.Result.Error;

                                    // Regenerate the tool call HTML with result
                                    var toolHtml = BuildToolCallHtml(
                                        toolInfo.call.Name,
                                        toolInfo.call.Arguments,
                                        resultText,
                                        step.Result.Success,
                                        toolInfo.id
                                    );

                                    // NEW: Find and update the corresponding block
                                    var block = toolCallBlocks.FirstOrDefault(b => b.ToolCallId == step.ToolCall.Id);
                                    if (block != null)
                                    {
                                        block.ToolHtml = toolHtml;
                                    }
                                }

                                RenderConversationStreamingInterleaved(toolCallBlocks, currentBlock, responseBuilder.ToString());
                                break;

                            case AgentStepType.Complete:
                                // Try to parse CompletionResult from step.Text
                                CompletionResult completionResult = null;
                                if (!string.IsNullOrEmpty(step.Text) && step.Text.StartsWith("TASK_COMPLETED:"))
                                {
                                    completionResult = CompletionResult.Deserialize(step.Text);
                                }

                                // For completion responses, always trust the structured completion summary
                                // as the final user-facing answer. Streaming text before attempt_completion
                                // often contains internal tool-decision language and should not be persisted.
                                string finalContent = responseBuilder.ToString().Trim();

                                // NEW: Build final tool logs HTML from interleaved blocks
                                string finalToolLogs = null;
                                if (hasToolCalls && toolCallBlocks.Count > 0)
                                {
                                    finalToolLogs = BuildInterleavedToolLogsHtml(toolCallBlocks);
                                }

                                if (!hasToolCalls && string.IsNullOrWhiteSpace(finalContent) && completionResult != null)
                                {
                                    finalContent = completionResult.Summary;
                                }

                                // Diagnostic hint: only if no tools AND response has action-like language
                                // AND response is substantial (>200 chars to avoid false positives on greetings)
                                if (!hasToolCalls && responseBuilder.Length > 200 && ContainsToolIntentLanguage(responseBuilder.ToString()))
                                {
                                    finalContent += "\n\n---\n⚠️ **提示**: AI 描述了要执行的操作但未实际调用工具。\n" +
                                        "可能原因：\n" +
                                        "1. LLM 服务器未启用 function calling（需要 `--enable-auto-tool-choice`）\n" +
                                        "2. 模型不支持 OpenAI 格式的工具调用\n" +
                                        "3. 在选项中检查 'Enable Tool Calling' 是否已启用";
                                }

                                if (!string.IsNullOrWhiteSpace(finalContent) || completionResult != null || !string.IsNullOrWhiteSpace(finalToolLogs))
                                {
                                    var message = new ConversationMessage
                                    {
                                        Role = "assistant",
                                        Content = finalContent,
                                        ToolLogsHtml = finalToolLogs,
                                        CompletionData = completionResult != null ? step.Text : null
                                    };
                                    _conversation.Add(message);
                                    // Also add to LLM history for next turn's context
                                    _llmHistory.Add(ChatMessage.Assistant(completionResult?.Summary ?? finalContent));
                                }
                                RenderConversation();
                                break;

                            case AgentStepType.Error:
                                // Preserve any tool logs and text accumulated before the error
                                string errorToolLogs = null;
                                if (hasToolCalls && toolCallBlocks.Count > 0)
                                {
                                    errorToolLogs = BuildInterleavedToolLogsHtml(toolCallBlocks);
                                }

                                var errorContent = responseBuilder.ToString().Trim();
                                var errorMessage = $"❌ Agent Error: {step.ErrorMessage}";

                                if (!string.IsNullOrWhiteSpace(errorToolLogs) || !string.IsNullOrWhiteSpace(errorContent))
                                {
                                    // Append the error message after any existing content
                                    var combinedContent = string.IsNullOrWhiteSpace(errorContent)
                                        ? errorMessage
                                        : errorContent + "\n\n---\n" + errorMessage;

                                    _conversation.Add(new ConversationMessage
                                    {
                                        Role = "assistant",
                                        Content = combinedContent,
                                        ToolLogsHtml = errorToolLogs
                                    });
                                    RenderConversation();
                                }
                                else
                                {
                                    AppendMessage("assistant", errorMessage);
                                }
                                break;
                        }
                    }));
                }
            });
        }

        private async System.Threading.Tasks.Task ExecuteChatModeAsync(string userMessage)
        {
            // Add system prompt if this is the first message
            if (_llmHistory.Count == 0)
            {
                _llmHistory.Add(ChatMessage.System(
                    "You are AICA, an AI coding assistant for Visual Studio. " +
                    "Help the user with programming tasks, code explanations, debugging, and more. " +
                    "Be concise but thorough. Use markdown for code formatting."));
            }

            _llmHistory.Add(ChatMessage.User(userMessage));

            var responseBuilder = new StringBuilder();
            var dispatcher = this.Dispatcher;

            // Show immediate feedback while waiting for LLM's first token (TTFT)
            RenderConversation("💭 *思考中...*");

            // Run LLM streaming on background thread, dispatch UI updates via Dispatcher.Invoke
            await System.Threading.Tasks.Task.Run(async () =>
            {
                await foreach (var chunk in _llmClient.StreamChatAsync(_llmHistory, null, _currentCts.Token))
                {
                    if (chunk.Type == LLMChunkType.Text && !string.IsNullOrEmpty(chunk.Text))
                    {
                        responseBuilder.Append(chunk.Text);
                        var content = responseBuilder.ToString();
                        dispatcher.Invoke(new Action(() => RenderConversation(content)));
                    }
                    else if (chunk.Type == LLMChunkType.Done)
                    {
                        break;
                    }
                }
            });

            var finalResponse = responseBuilder.ToString();
            if (!string.IsNullOrEmpty(finalResponse))
            {
                _llmHistory.Add(ChatMessage.Assistant(finalResponse));
                _conversation.Add(new ConversationMessage { Role = "assistant", Content = finalResponse });
                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                RenderConversation();
            }
            else
            {
                AppendMessage("assistant", "⚠️ No response received from the LLM.");
            }
        }

        private string TruncateForDisplay(string text, int maxLength)
        {
            if (string.IsNullOrEmpty(text)) return "(empty)";
            if (text.Length <= maxLength) return text.Replace("\n", " ").Replace("\r", "");
            return text.Substring(0, maxLength).Replace("\n", " ").Replace("\r", "") + "...";
        }

        /// <summary>
        /// Build enhanced HTML for tool call visualization
        /// </summary>
        private string BuildToolCallHtml(string toolName, Dictionary<string, object> arguments, string result, bool success, int toolCallId)
        {
            var html = new StringBuilder();
            var toolIcon = GetToolIcon(toolName);

            html.AppendLine($"<div class=\"tool-call-container\">");
            html.AppendLine($"  <input type=\"checkbox\" id=\"tool-call-toggle-{toolCallId}\" class=\"tool-call-toggle\" />");
            html.AppendLine($"  <label for=\"tool-call-toggle-{toolCallId}\" class=\"tool-call-header\">");
            html.AppendLine($"    <span class=\"tool-call-icon\">{toolIcon}</span>");
            html.AppendLine($"    <span class=\"tool-call-name\">{System.Web.HttpUtility.HtmlEncode(toolName)}</span>");
            html.AppendLine($"    <span class=\"tool-call-expand\">▶</span>");
            html.AppendLine($"  </label>");
            html.AppendLine($"  <div class=\"tool-call-body\">");

            // Parameters section
            if (arguments != null && arguments.Count > 0)
            {
                html.AppendLine("    <div class=\"tool-call-params\">");
                foreach (var arg in arguments.Take(5)) // Limit to 5 params for display
                {
                    var valueStr = arg.Value?.ToString() ?? "(null)";
                    var displayValue = TruncateForDisplay(valueStr, 100);
                    html.AppendLine("      <div class=\"tool-call-param\">");
                    html.AppendLine($"        <span class=\"tool-call-param-name\">{System.Web.HttpUtility.HtmlEncode(arg.Key)}:</span>");
                    html.AppendLine($"        <span class=\"tool-call-param-value\">{System.Web.HttpUtility.HtmlEncode(displayValue)}</span>");
                    html.AppendLine("      </div>");
                }
                if (arguments.Count > 5)
                {
                    html.AppendLine($"      <div class=\"tool-call-param\" style=\"color: #9ca3af; font-size: 11px;\">... and {arguments.Count - 5} more parameters</div>");
                }
                html.AppendLine("    </div>");
            }

            // Result section
            if (!string.IsNullOrEmpty(result))
            {
                var resultClass = success ? "success" : "error";
                var resultIcon = success ? "✅" : "❌";
                var resultLabel = success ? "Result" : "Error";

                html.AppendLine($"    <div class=\"tool-call-result {(success ? "" : "error")}\">");
                html.AppendLine($"      <div class=\"tool-call-result-header {resultClass}\">");
                html.AppendLine($"        <span>{resultIcon}</span>");
                html.AppendLine($"        <span>{resultLabel}</span>");
                html.AppendLine("      </div>");
                html.AppendLine($"      <div class=\"tool-call-result-content\">{System.Web.HttpUtility.HtmlEncode(TruncateForDisplay(result, 500))}</div>");
                html.AppendLine("    </div>");
            }

            html.AppendLine("  </div>");
            html.AppendLine("</div>");

            return html.ToString();
        }

        /// <summary>
        /// Get icon for tool based on tool name
        /// </summary>
        private string GetToolIcon(string toolName)
        {
            if (string.IsNullOrEmpty(toolName)) return "🔧";

            var lowerName = toolName.ToLowerInvariant();
            if (lowerName.Contains("read") || lowerName.Contains("list")) return "📖";
            if (lowerName.Contains("write") || lowerName.Contains("create")) return "✏️";
            if (lowerName.Contains("edit") || lowerName.Contains("modify")) return "📝";
            if (lowerName.Contains("delete") || lowerName.Contains("remove")) return "🗑️";
            if (lowerName.Contains("search") || lowerName.Contains("find") || lowerName.Contains("grep")) return "🔍";
            if (lowerName.Contains("command") || lowerName.Contains("run") || lowerName.Contains("execute")) return "⚡";
            if (lowerName.Contains("git")) return "🔀";
            if (lowerName.Contains("project")) return "📁";

            return "🔧";
        }

        private bool ContainsToolIntentLanguage(string text)
        {
            if (string.IsNullOrEmpty(text)) return false;

            // Detect phrases that indicate the AI intended to use tools but didn't
            var intentPhrases = new[]
            {
                "让我", "我将", "我来", "我会", "让我们",
                "查看", "读取", "列出", "打开", "检查",
                "let me", "i will", "i'll", "let's",
                "read the file", "list the", "check the"
            };

            var lowerText = text.ToLowerInvariant();
            foreach (var phrase in intentPhrases)
            {
                if (lowerText.Contains(phrase.ToLowerInvariant()))
                    return true;
            }
            return false;
        }

        /// <summary>
        /// Get current project path from Visual Studio
        /// </summary>
        private string GetCurrentProjectPath()
        {
            try
            {
                ThreadHelper.ThrowIfNotOnUIThread();

                // 优先使用解决方案路径
                if (_dte?.Solution?.FullName != null && !string.IsNullOrEmpty(_dte.Solution.FullName))
                {
                    return System.IO.Path.GetDirectoryName(_dte.Solution.FullName);
                }

                // 回退到工作目录
                return _agentContext?.WorkingDirectory;
            }
            catch
            {
                return _agentContext?.WorkingDirectory;
            }
        }

        /// <summary>
        /// Get project name from project path
        /// </summary>
        private string GetProjectName(string projectPath)
        {
            if (string.IsNullOrEmpty(projectPath))
                return null;

            try
            {
                return System.IO.Path.GetFileName(projectPath);
            }
            catch
            {
                return null;
            }
        }

        /// <summary>
        /// Get project name from solution path (extract .sln file name without extension)
        /// </summary>
        private string GetProjectNameFromSolutionPath(string solutionPath)
        {
            if (string.IsNullOrEmpty(solutionPath))
                return null;

            try
            {
                return System.IO.Path.GetFileNameWithoutExtension(solutionPath);
            }
            catch
            {
                return null;
            }
        }

        /// <summary>
        /// Find solution name by searching for .sln file in project path
        /// </summary>
        private string FindSolutionNameInPath(string projectPath)
        {
            if (string.IsNullOrEmpty(projectPath))
                return null;

            try
            {
                // Search for .sln files in the project directory
                if (System.IO.Directory.Exists(projectPath))
                {
                    var slnFiles = System.IO.Directory.GetFiles(projectPath, "*.sln", System.IO.SearchOption.TopDirectoryOnly);
                    if (slnFiles.Length > 0)
                    {
                        var solutionName = System.IO.Path.GetFileNameWithoutExtension(slnFiles[0]);
                        System.Diagnostics.Debug.WriteLine($"[AICA] Found solution in path: {projectPath} -> {solutionName}");
                        return solutionName;
                    }
                    else
                    {
                        System.Diagnostics.Debug.WriteLine($"[AICA] No .sln files found in: {projectPath}");
                    }
                }
                else
                {
                    System.Diagnostics.Debug.WriteLine($"[AICA] Project path does not exist: {projectPath}");
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[AICA] Error finding solution in path {projectPath}: {ex.Message}");
            }

            return null;
        }

        /// <summary>
        /// Get current solution path from Visual Studio
        /// </summary>
        private string GetCurrentSolutionPath()
        {
            try
            {
                ThreadHelper.ThrowIfNotOnUIThread();
                return _dte?.Solution?.FullName;
            }
            catch
            {
                return null;
            }
        }

        /// <summary>
        /// Get current project name from Visual Studio
        /// </summary>
        private string GetCurrentProjectName()
        {
            try
            {
                ThreadHelper.ThrowIfNotOnUIThread();

                if (_dte?.Solution?.FullName != null && !string.IsNullOrEmpty(_dte.Solution.FullName))
                {
                    return System.IO.Path.GetFileNameWithoutExtension(_dte.Solution.FullName);
                }

                if (_agentContext?.WorkingDirectory != null)
                {
                    return System.IO.Path.GetFileName(_agentContext.WorkingDirectory);
                }

                return "未命名项目";
            }
            catch
            {
                return "未命名项目";
            }
        }

        private async System.Threading.Tasks.Task LoadConversationAsync(string conversationId)
        {
            try
            {
                var record = await _conversationStorage.LoadConversationAsync(conversationId);
                if (record == null)
                {
                    System.Windows.MessageBox.Show("无法加载会话，文件可能已损坏", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
                    return;
                }

                // Save current conversation before switching (only if it's different)
                if (_conversation.Count > 0 && _currentConversationId != conversationId)
                {
                    await SaveConversationAsync();
                }

                // Clear current conversation
                _conversation.Clear();
                _llmHistory.Clear();

                // Restore messages
                foreach (var msg in record.Messages)
                {
                    _conversation.Add(new ConversationMessage
                    {
                        Role = msg.Role,
                        Content = msg.Content,
                        ToolLogsHtml = msg.ToolLogsHtml,
                        CompletionData = msg.CompletionData
                    });

                    // Restore to LLM history (for context)
                    if (msg.Role == "user")
                    {
                        _llmHistory.Add(ChatMessage.User(msg.Content));
                    }
                    else if (msg.Role == "assistant")
                    {
                        _llmHistory.Add(ChatMessage.Assistant(msg.Content ?? string.Empty));
                    }
                }

                // Set current conversation ID
                _currentConversationId = conversationId;

                // Re-render interface
                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                RenderConversation();

                System.Diagnostics.Debug.WriteLine($"[AICA] Loaded conversation {conversationId} with {record.Messages.Count} messages");
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[AICA] Failed to load conversation: {ex.Message}");
                System.Windows.MessageBox.Show($"加载会话失败: {ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private async System.Threading.Tasks.Task SaveConversationAsync()
        {
            try
            {
                if (_conversation.Count == 0) return;

                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();

                bool isNewConversation = _currentConversationId == null;

                ConversationRecord record;

                if (isNewConversation)
                {
                    // New conversation: create with current project info
                    record = new ConversationRecord
                    {
                        Id = null, // Will be generated
                        WorkingDirectory = _agentContext?.WorkingDirectory,
                        ProjectPath = GetCurrentProjectPath(),
                        ProjectName = GetCurrentProjectName(),
                        SolutionPath = GetCurrentSolutionPath(),
                        CreatedAt = DateTimeOffset.UtcNow,
                        Messages = new List<ConversationMessageRecord>()
                    };
                }
                else
                {
                    // Existing conversation: load and preserve original project info
                    record = await _conversationStorage.LoadConversationAsync(_currentConversationId);
                    if (record == null)
                    {
                        // Conversation file was deleted, treat as new
                        record = new ConversationRecord
                        {
                            Id = _currentConversationId,
                            WorkingDirectory = _agentContext?.WorkingDirectory,
                            ProjectPath = GetCurrentProjectPath(),
                            ProjectName = GetCurrentProjectName(),
                            SolutionPath = GetCurrentSolutionPath(),
                            CreatedAt = DateTimeOffset.UtcNow,
                            Messages = new List<ConversationMessageRecord>()
                        };
                    }
                    else
                    {
                        // Clear messages to rebuild from current conversation
                        record.Messages.Clear();
                    }
                }

                // Set title from first user message
                foreach (var msg in _conversation)
                {
                    if (msg.Role == "user" && string.IsNullOrEmpty(record.Title))
                    {
                        // 改进标题生成：只取第一行，最多50字符
                        var firstLine = msg.Content.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? msg.Content;
                        record.Title = firstLine.Length > 50
                            ? firstLine.Substring(0, 47) + "..."
                            : firstLine;
                    }

                    record.Messages.Add(new ConversationMessageRecord
                    {
                        Role = msg.Role,
                        Content = msg.Content,
                        ToolLogsHtml = msg.ToolLogsHtml,
                        CompletionData = msg.CompletionData
                    });
                }

                await _conversationStorage.SaveConversationAsync(record);

                // Update current conversation ID if it was new
                if (isNewConversation)
                {
                    _currentConversationId = record.Id;
                }

                // If this is a new conversation and sidebar is open, add it to the list
                if (isNewConversation && _isSidebarOpen)
                {
                    var newViewModel = new ConversationViewModel
                    {
                        Id = record.Id,
                        Title = record.Title ?? "未命名会话",
                        TimeAgo = "刚刚",
                        UpdatedAt = record.UpdatedAt,
                        ProjectName = record.ProjectName
                    };

                    _allConversations.Insert(0, newViewModel);
                    ConversationListBox.ItemsSource = null;
                    ConversationListBox.ItemsSource = _allConversations;
                    ConversationListBox.SelectedItem = newViewModel;

                    System.Diagnostics.Debug.WriteLine($"[AICA] New conversation added to list: {record.Id}");
                }

                // Periodic cleanup
                if (_conversation.Count % 20 == 0)
                    await _conversationStorage.CleanupOldConversationsAsync(100);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[AICA] Failed to save conversation: {ex.Message}");
            }
        }

        private void ClearButton_Click(object sender, RoutedEventArgs e)
        {
            ClearConversation();
        }

        private async void HistoryButton_Click(object sender, RoutedEventArgs e)
        {
            _isSidebarOpen = !_isSidebarOpen;
            await ToggleSidebarAsync();
        }

        private async System.Threading.Tasks.Task ToggleSidebarAsync()
        {
            System.Diagnostics.Debug.WriteLine($"[AICA] ToggleSidebarAsync called, _isSidebarOpen={_isSidebarOpen}");

            if (_isSidebarOpen)
            {
                // Check if project has switched
                var currentProjectPath = GetCurrentProjectPath();
                System.Diagnostics.Debug.WriteLine($"[AICA] Opening sidebar, currentProjectPath={currentProjectPath}, _lastProjectPath={_lastProjectPath}");

                if (_lastProjectPath != null && _lastProjectPath != currentProjectPath)
                {
                    // Project switched - save current conversation and clear
                    await SaveConversationAsync();
                    ClearConversation();
                    System.Diagnostics.Debug.WriteLine($"[AICA] Project switched from {_lastProjectPath} to {currentProjectPath}");
                }
                _lastProjectPath = currentProjectPath;

                // Load conversations when opening
                System.Diagnostics.Debug.WriteLine($"[AICA] About to call LoadConversationListAsync");
                await LoadConversationListAsync();
                System.Diagnostics.Debug.WriteLine($"[AICA] LoadConversationListAsync completed");

                // Animate sidebar opening
                var animation = new System.Windows.Media.Animation.DoubleAnimation
                {
                    From = 0,
                    To = 250,
                    Duration = TimeSpan.FromMilliseconds(300),
                    EasingFunction = new System.Windows.Media.Animation.CubicEase { EasingMode = System.Windows.Media.Animation.EasingMode.EaseOut }
                };
                HistorySidebar.BeginAnimation(System.Windows.Controls.Border.WidthProperty, animation);
            }
            else
            {
                // Animate sidebar closing
                var animation = new System.Windows.Media.Animation.DoubleAnimation
                {
                    From = 250,
                    To = 0,
                    Duration = TimeSpan.FromMilliseconds(300),
                    EasingFunction = new System.Windows.Media.Animation.CubicEase { EasingMode = System.Windows.Media.Animation.EasingMode.EaseIn }
                };
                HistorySidebar.BeginAnimation(System.Windows.Controls.Border.WidthProperty, animation);
            }
        }

        private async System.Threading.Tasks.Task LoadConversationListAsync()
        {
            try
            {
                System.Diagnostics.Debug.WriteLine($"[AICA] LoadConversationListAsync started");
                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();

                var projectPath = GetCurrentProjectPath();
                System.Diagnostics.Debug.WriteLine($"[AICA] Current project path: {projectPath}");

                var summaries = await _conversationStorage.ListConversationsForProjectAsync(projectPath, 50);
                System.Diagnostics.Debug.WriteLine($"[AICA] Loaded {summaries.Count} conversation summaries");

                _allConversations = summaries.Select(s =>
                {
                    var solutionName = GetProjectNameFromSolutionPath(s.SolutionPath);
                    var foundName = solutionName ?? FindSolutionNameInPath(s.ProjectPath);
                    var finalName = foundName ?? s.ProjectName;

                    System.Diagnostics.Debug.WriteLine($"[AICA] Conversation {s.Id}: solutionPath={s.SolutionPath}, projectPath={s.ProjectPath}, solutionName={solutionName}, foundName={foundName}, finalName={finalName}");

                    return new ConversationViewModel
                    {
                        Id = s.Id,
                        Title = s.Title ?? "未命名会话",
                        TimeAgo = GetTimeAgo(s.UpdatedAt),
                        UpdatedAt = s.UpdatedAt,
                        ProjectName = finalName
                    };
                }).ToList();

                ConversationListBox.ItemsSource = _allConversations;

                // Highlight current conversation
                if (!string.IsNullOrEmpty(_currentConversationId))
                {
                    var currentItem = _allConversations.FirstOrDefault(c => c.Id == _currentConversationId);
                    if (currentItem != null)
                    {
                        ConversationListBox.SelectedItem = currentItem;
                    }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[AICA] Failed to load conversation list: {ex.Message}");
            }
        }

        private string GetTimeAgo(DateTimeOffset time)
        {
            var now = DateTimeOffset.UtcNow;
            var diff = now - time;

            if (diff.TotalMinutes < 1)
                return "刚刚";
            if (diff.TotalMinutes < 60)
                return $"{(int)diff.TotalMinutes}分钟前";
            if (diff.TotalHours < 24)
                return $"{(int)diff.TotalHours}小时前";
            if (diff.TotalDays < 2)
                return "昨天";
            if (diff.TotalDays < 7)
                return $"{(int)diff.TotalDays}天前";
            if (time.Year == now.Year)
                return time.ToString("MM-dd");
            return time.ToString("yyyy-MM-dd");
        }

        private async void NewConversationButton_Click(object sender, RoutedEventArgs e)
        {
            // Save current conversation first
            await SaveConversationAsync();

            // Clear and start new conversation
            ClearConversation();

            // Create a new conversation record with "未命名会话" title
            var projectPath = GetCurrentProjectPath();
            var projectName = GetProjectName(projectPath);
            var newConversation = new ConversationRecord
            {
                ProjectPath = projectPath,
                ProjectName = projectName,
                CreatedAt = DateTimeOffset.UtcNow,
                UpdatedAt = DateTimeOffset.UtcNow,
                Title = "未命名会话",
                Messages = new List<ConversationMessageRecord>()
            };

            // Generate ID and save
            await _conversationStorage.SaveConversationAsync(newConversation);
            _currentConversationId = newConversation.Id;

            // Add to list immediately
            var newViewModel = new ConversationViewModel
            {
                Id = newConversation.Id,
                Title = "未命名会话",
                TimeAgo = "刚刚",
                UpdatedAt = newConversation.UpdatedAt,
                ProjectName = projectName // Show project name
            };

            _allConversations.Insert(0, newViewModel);
            ConversationListBox.ItemsSource = null;
            ConversationListBox.ItemsSource = _allConversations;
            ConversationListBox.SelectedItem = newViewModel;

            System.Diagnostics.Debug.WriteLine($"[AICA] New conversation created: {_currentConversationId}");
        }

        private async void ConversationListBox_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
        {
            if (ConversationListBox.SelectedItem is ConversationViewModel selected)
            {
                // Don't reload if it's the current conversation
                if (selected.Id == _currentConversationId)
                    return;

                await LoadConversationAsync(selected.Id);
            }
        }

        private async void DeleteConversationButton_Click(object sender, RoutedEventArgs e)
        {
            if (sender is System.Windows.Controls.Button button && button.Tag is string conversationId)
            {
                var result = System.Windows.MessageBox.Show(
                    $"确定要删除这个会话吗？此操作无法撤销。",
                    "确认删除",
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Warning);

                if (result == MessageBoxResult.Yes)
                {
                    await _conversationStorage.DeleteConversationAsync(conversationId);

                    // If deleting current conversation, create new one
                    if (conversationId == _currentConversationId)
                    {
                        ClearConversation();
                    }

                    // Reload list
                    await LoadConversationListAsync();
                }
            }
        }

        private bool _isAllResumeMode = false; // Track if we're in /allresume mode

        private async void SearchTextBox_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
        {
            var searchText = SearchTextBox.Text?.Trim() ?? string.Empty;

            // Check for /allresume command
            if (searchText.Equals("/allresume", StringComparison.OrdinalIgnoreCase))
            {
                _isAllResumeMode = true;
                await LoadAllConversationsAsync();
                return;
            }

            // If we were in /allresume mode and now search is cleared or changed, reload project conversations
            if (_isAllResumeMode && string.IsNullOrWhiteSpace(searchText))
            {
                _isAllResumeMode = false;
                await LoadConversationListAsync();
                return;
            }

            // If we were in /allresume mode and user types something else, reload project conversations first
            if (_isAllResumeMode)
            {
                _isAllResumeMode = false;
                await LoadConversationListAsync();
            }

            // Normal search/filter
            var searchLower = searchText.ToLowerInvariant();

            if (string.IsNullOrWhiteSpace(searchLower))
            {
                ConversationListBox.ItemsSource = _allConversations;
            }
            else
            {
                var filtered = _allConversations
                    .Where(c => c.Title.ToLowerInvariant().Contains(searchLower))
                    .ToList();
                ConversationListBox.ItemsSource = filtered;
            }
        }

        private async System.Threading.Tasks.Task LoadAllConversationsAsync()
        {
            try
            {
                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();

                var summaries = await _conversationStorage.ListAllConversationsAsync(100);

                _allConversations = summaries.Select(s => new ConversationViewModel
                {
                    Id = s.Id,
                    Title = s.Title ?? "未命名会话",
                    TimeAgo = GetTimeAgo(s.UpdatedAt),
                    UpdatedAt = s.UpdatedAt,
                    ProjectName = s.ProjectName ?? "未知项目"
                }).ToList();

                ConversationListBox.ItemsSource = _allConversations;

                System.Diagnostics.Debug.WriteLine($"[AICA] Loaded all conversations: {_allConversations.Count} items");
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[AICA] Failed to load all conversations: {ex.Message}");
            }
        }

        private void SettingsButton_Click(object sender, RoutedEventArgs e)
        {
            VS.Commands.ExecuteAsync("Tools.Options").FireAndForget();
        }

        private string BuildPageHtml(string innerContent)
        {
            return $@"<!DOCTYPE html>
<html>
<head>
    <meta charset=""utf-8"" />
    <style>
        :root {{ color-scheme: light dark; }}
        body {{
            margin: 0; padding: 0;
            font-family: 'Segoe UI', 'Helvetica Neue', Arial, sans-serif;
            font-size: 14px; line-height: 1.5;
            background: #1e1e1e; color: #d4d4d4;
        }}
        .container {{ padding: 12px 16px 20px 16px; max-width: 1100px; margin: 0 auto; }}
        @media (prefers-color-scheme: light) {{
            body {{ background: #ffffff; color: #1e1e1e; }}
            pre code {{ background: #f6f8fa; color: #1e1e1e; }}
            .message {{ background: #f5f7fb; border-color: #d0d7de; }}
            .message.user {{ background: #e8f1ff; border-color: #b7cff9; }}
            .completion-card {{ background: #e8f5e9; border-color: #81c784; }}
            .feedback-btn {{ background: #f5f5f5; color: #333; }}
            .feedback-btn:hover {{ background: #e0e0e0; }}
            .feedback-btn.selected {{ background: #4caf50; color: white; }}
        }}
        .message {{
            margin: 0 0 12px 0; padding: 10px 12px;
            border-radius: 8px; border: 1px solid #3c3c3c;
            background: #252526; box-shadow: 0 1px 2px rgba(0,0,0,0.35);
        }}
        .message.user {{ background: #0e3a5c; border-color: #2d5f8a; }}
        .message.streaming {{ opacity: 0.85; }}
        .completion-card {{
            background: #1b3a1b; border-color: #4caf50;
            border-left: 4px solid #4caf50;
        }}
        .completion-header {{
            font-size: 13px; font-weight: 600;
            color: #81c784; margin-bottom: 8px;
            display: flex; align-items: center; gap: 6px;
        }}
        .completion-icon {{ font-size: 16px; }}
        .completion-command {{
            margin-top: 10px; padding: 8px 10px;
            background: rgba(0,0,0,0.2); border-radius: 4px;
            font-family: Consolas, monospace; font-size: 12px;
        }}
        .feedback-section {{
            margin-top: 12px; padding-top: 12px;
            border-top: 1px solid rgba(255,255,255,0.1);
        }}
        .feedback-label {{
            font-size: 12px; color: #9ca3af;
            margin-bottom: 6px;
        }}
        .feedback-buttons {{
            display: flex; gap: 8px;
        }}
        .feedback-btn {{
            padding: 6px 14px; border-radius: 4px;
            border: 1px solid #3c3c3c;
            background: #2d2d30; color: #d4d4d4;
            cursor: pointer; font-size: 12px;
            transition: all 0.2s;
        }}
        .feedback-btn:hover {{
            background: #3c3c3c; border-color: #4caf50;
        }}
        .feedback-btn.selected {{
            background: #4caf50; color: white;
            border-color: #4caf50;
        }}
        .feedback-btn.unsatisfied.selected {{
            background: #f44336; border-color: #f44336;
        }}
        .role {{
            font-size: 11px; letter-spacing: 0.03em;
            text-transform: uppercase; color: #9ca3af; margin-bottom: 6px;
        }}
        .content p {{ margin: 0 0 0.75em 0; }}
        pre {{ overflow-x: auto; }}
        pre code {{
            display: block; padding: 12px; border-radius: 6px;
            background: #1e1e1e; color: #d4d4d4;
            font-family: Consolas, 'Courier New', monospace; font-size: 13px;
        }}
        code {{
            font-family: Consolas, 'Courier New', monospace;
            background: rgba(255,255,255,0.08); padding: 0 3px; border-radius: 3px;
        }}
        a {{ color: #4aa3ff; text-decoration: none; }}
        a:hover {{ text-decoration: underline; }}
        h1, h2, h3, h4, h5, h6 {{ margin-top: 1.4em; margin-bottom: 0.6em; }}

        /* Tool Call Visualization Styles */
        .tool-call-container {{
            margin: 8px 0;
            border-left: 3px solid #4aa3ff;
            background: rgba(74, 163, 255, 0.08);
            border-radius: 4px;
            overflow: hidden;
        }}
        .tool-call-toggle {{
            display: none;
        }}
        .tool-call-header {{
            padding: 8px 12px;
            background: rgba(74, 163, 255, 0.12);
            display: flex;
            align-items: center;
            gap: 8px;
            font-size: 13px;
            font-weight: 600;
            color: #4aa3ff;
            cursor: pointer;
            user-select: none;
        }}
        .tool-call-header:hover {{
            background: rgba(74, 163, 255, 0.18);
        }}
        .tool-call-icon {{
            font-size: 14px;
        }}
        .tool-call-name {{
            font-family: Consolas, monospace;
            font-size: 12px;
        }}
        .tool-call-expand {{
            margin-left: auto;
            font-size: 10px;
            transition: transform 0.2s;
        }}
        .tool-call-toggle:checked ~ .tool-call-header .tool-call-expand {{
            transform: rotate(90deg);
        }}
        .tool-call-body {{
            padding: 10px 12px;
            display: none;
        }}
        .tool-call-toggle:checked ~ .tool-call-body {{
            display: block;
        }}
        .tool-call-params {{
            margin-bottom: 10px;
        }}
        .tool-call-param {{
            margin: 4px 0;
            font-size: 12px;
        }}
        .tool-call-param-name {{
            color: #9ca3af;
            font-weight: 500;
        }}
        .tool-call-param-value {{
            color: #d4d4d4;
            font-family: Consolas, monospace;
            background: rgba(255,255,255,0.05);
            padding: 2px 6px;
            border-radius: 3px;
            margin-left: 6px;
        }}
        .tool-call-result {{
            margin-top: 10px;
            padding: 10px;
            background: rgba(0,0,0,0.2);
            border-radius: 4px;
            border-left: 3px solid #4caf50;
        }}
        .tool-call-result.error {{
            border-left-color: #f44336;
        }}
        .tool-call-result-header {{
            font-size: 12px;
            font-weight: 600;
            margin-bottom: 6px;
            display: flex;
            align-items: center;
            gap: 6px;
        }}
        .tool-call-result-header.success {{
            color: #81c784;
        }}
        .tool-call-result-header.error {{
            color: #e57373;
        }}
        .tool-call-result-content {{
            font-size: 12px;
            font-family: Consolas, monospace;
            color: #d4d4d4;
            white-space: pre-wrap;
            word-break: break-word;
            max-height: 300px;
            overflow-y: auto;
        }}
        @media (prefers-color-scheme: light) {{
            .tool-call-container {{
                background: rgba(74, 163, 255, 0.05);
            }}
            .tool-call-header {{
                background: rgba(74, 163, 255, 0.1);
            }}
            .tool-call-header:hover {{
                background: rgba(74, 163, 255, 0.15);
            }}
            .tool-call-param-value {{
                background: rgba(0,0,0,0.05);
            }}
            .tool-call-result {{
                background: rgba(0,0,0,0.03);
            }}
        }}
    </style>
    <script>
        function provideFeedback(messageId, feedback) {{
            // Toggle selection
            var btns = document.querySelectorAll('[data-message-id=""' + messageId + '""]');
            var currentFeedback = 'none';
            btns.forEach(function(btn) {{
                if (btn.dataset.feedback === feedback) {{
                    if (btn.classList.contains('selected')) {{
                        btn.classList.remove('selected');
                        currentFeedback = 'none';
                    }} else {{
                        btn.classList.add('selected');
                        currentFeedback = feedback;
                    }}
                }} else {{
                    btn.classList.remove('selected');
                }}
            }});

            // Notify host via navigation (will be intercepted)
            window.location.href = 'aica://feedback?messageId=' + messageId + '&feedback=' + currentFeedback;
        }}

        function toggleToolCall(toolCallId) {{
            var body = document.getElementById('tool-call-body-' + toolCallId);
            var expand = document.getElementById('tool-call-expand-' + toolCallId);

            if (body && expand) {{
                if (body.classList.contains('expanded')) {{
                    body.classList.remove('expanded');
                    expand.classList.remove('expanded');
                }} else {{
                    body.classList.add('expanded');
                    expand.classList.add('expanded');
                }}
            }} else {{
                console.error('Tool call elements not found for ID: ' + toolCallId);
            }}
        }}
    </script>
</head>
<body>
<div id=""chat-log"" class=""container"">
{innerContent}
</div>
</body>
</html>";
        }

        private class ConversationMessage
        {
            public string Role { get; set; }
            public string Content { get; set; }
            public string ToolLogsHtml { get; set; }
            public string CompletionData { get; set; } // Stores serialized CompletionResult
        }

        /// <summary>
        /// Build HTML for a completion card with feedback buttons
        /// </summary>
        private string BuildCompletionCardHtml(string summary, string command, int messageIndex)
        {
            var html = new StringBuilder();
            html.AppendLine("<div class=\"completion-card\">");
            html.AppendLine("  <div class=\"completion-header\">");
            html.AppendLine("    <span class=\"completion-icon\">✅</span>");
            html.AppendLine("    <span>Task Completed</span>");
            html.AppendLine("  </div>");
            html.AppendLine($"  <div class=\"content\">{Markdown.ToHtml(summary, _markdownPipeline)}</div>");

            if (!string.IsNullOrWhiteSpace(command))
            {
                html.AppendLine("  <div class=\"completion-command\">");
                html.AppendLine($"    <strong>Suggested command:</strong> <code>{System.Web.HttpUtility.HtmlEncode(command)}</code>");
                html.AppendLine("  </div>");
            }

            html.AppendLine("  <div class=\"feedback-section\">");
            html.AppendLine("    <div class=\"feedback-label\">Was this helpful?</div>");
            html.AppendLine("    <div class=\"feedback-buttons\">");
            html.AppendLine($"      <button class=\"feedback-btn\" data-message-id=\"{messageIndex}\" data-feedback=\"satisfied\" onclick=\"provideFeedback({messageIndex}, 'satisfied')\">👍 Yes</button>");
            html.AppendLine($"      <button class=\"feedback-btn unsatisfied\" data-message-id=\"{messageIndex}\" data-feedback=\"unsatisfied\" onclick=\"provideFeedback({messageIndex}, 'unsatisfied')\">👎 No</button>");
            html.AppendLine("    </div>");
            html.AppendLine("  </div>");
            html.AppendLine("</div>");

            return html.ToString();
        }

        /// <summary>
        /// Record user feedback on task completion
        /// </summary>
        public void RecordFeedback(int messageIndex, string feedback)
        {
            try
            {
                if (messageIndex < 0 || messageIndex >= _conversation.Count)
                    return;

                var message = _conversation[messageIndex];
                if (string.IsNullOrEmpty(message.CompletionData))
                    return;

                var completionResult = CompletionResult.Deserialize(message.CompletionData);
                if (completionResult == null)
                    return;

                // Update feedback
                if (feedback == "satisfied")
                    completionResult.Feedback = CompletionFeedback.Satisfied;
                else if (feedback == "unsatisfied")
                    completionResult.Feedback = CompletionFeedback.Unsatisfied;
                else
                    completionResult.Feedback = CompletionFeedback.None;

                // Log feedback
                System.Diagnostics.Debug.WriteLine($"[AICA] User feedback on completion: {completionResult.Feedback}");

                // TODO: Persist feedback to storage or analytics
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[AICA] Error recording feedback: {ex.Message}");
            }
        }
    }
}

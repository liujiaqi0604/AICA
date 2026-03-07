using System.ComponentModel;
using System.Runtime.InteropServices;

namespace AICA.Options
{
    internal partial class OptionsProvider
    {
        [ComVisible(true)]
        public class GeneralOptionsPage : BaseOptionPage<GeneralOptions> { }
    }

    public class GeneralOptions : BaseOptionModel<GeneralOptions>
    {
        [Category("LLM Configuration")]
        [DisplayName("API Endpoint")]
        [Description("The base URL of the LLM API endpoint (e.g., http://localhost:8000/v1/)")]
        [DefaultValue("http://localhost:8000/v1/")]
        public string ApiEndpoint { get; set; } = "http://localhost:8000/v1/";

        [Category("LLM Configuration")]
        [DisplayName("API Key")]
        [Description("API key for authentication (leave empty if not required)")]
        [PasswordPropertyText(true)]
        public string ApiKey { get; set; } = string.Empty;

        [Category("LLM Configuration")]
        [DisplayName("Model Name")]
        [Description("The model identifier to use (e.g., MiniMax-M2.5, qwen3-coder, deepseek-coder)")]
        [DefaultValue("MiniMax-M2.5")]
        public string ModelName { get; set; } = "MiniMax-M2.5";

        [Category("LLM Configuration")]
        [DisplayName("Max Tokens")]
        [Description("Maximum number of tokens for LLM response")]
        [DefaultValue(4096)]
        public int MaxTokens { get; set; } = 4096;

        [Category("LLM Configuration")]
        [DisplayName("Temperature")]
        [Description("Temperature for LLM response (0.0 - 2.0)")]
        [DefaultValue(0.7)]
        public double Temperature { get; set; } = 0.7;

        [Category("Agent Configuration")]
        [DisplayName("Enable Tool Calling")]
        [Description("Enable AI to use tools (read/write files, etc.). Requires LLM server to support function calling. Disable if you see 'tool choice' errors.")]
        [DefaultValue(true)]
        public bool EnableToolCalling { get; set; } = true;

        [Category("Agent Configuration")]
        [DisplayName("Max Agent Iterations")]
        [Description("Maximum number of iterations for the Agent loop")]
        [DefaultValue(25)]
        public int MaxAgentIterations { get; set; } = 25;

        [Category("Agent Configuration")]
        [DisplayName("Request Timeout (seconds)")]
        [Description("Timeout in seconds for LLM requests")]
        [DefaultValue(120)]
        public int RequestTimeoutSeconds { get; set; } = 120;

        [Category("Agent Configuration")]
        [DisplayName("Custom Instructions")]
        [Description("Additional custom instructions to include in the system prompt (optional)")]
        [DefaultValue("")]
        public string CustomInstructions { get; set; } = string.Empty;

        [Category("UI Settings")]
        [DisplayName("Format Changed Text")]
        [Description("Automatically format code after AI modifications")]
        [DefaultValue(true)]
        public bool FormatChangedText { get; set; } = true;

        [Category("UI Settings")]
        [DisplayName("Show Notifications")]
        [Description("Show system notifications for important events")]
        [DefaultValue(true)]
        public bool ShowNotifications { get; set; } = true;
    }
}

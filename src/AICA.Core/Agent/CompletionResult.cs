using System;

namespace AICA.Core.Agent
{
    /// <summary>
    /// Represents the result of an attempt_completion tool call
    /// </summary>
    public class CompletionResult
    {
        /// <summary>
        /// Summary of what was accomplished
        /// </summary>
        public string Summary { get; set; }

        /// <summary>
        /// Optional command to demonstrate the result
        /// </summary>
        public string Command { get; set; }

        /// <summary>
        /// Timestamp when the task was completed
        /// </summary>
        public DateTime Timestamp { get; set; }

        /// <summary>
        /// User feedback (satisfied/unsatisfied/none)
        /// </summary>
        public CompletionFeedback Feedback { get; set; }

        /// <summary>
        /// Optional feedback comment from user
        /// </summary>
        public string FeedbackComment { get; set; }

        public CompletionResult()
        {
            Timestamp = DateTime.Now;
            Feedback = CompletionFeedback.None;
        }

        /// <summary>
        /// Serialize to string format for tool result
        /// </summary>
        public string Serialize()
        {
            // Use a more robust serialization format that handles special characters
            // Format: TASK_COMPLETED:SUMMARY_START<summary>SUMMARY_END|COMMAND:<command>|TIMESTAMP:<timestamp>
            var sb = new System.Text.StringBuilder();
            sb.Append("TASK_COMPLETED:");

            // Wrap summary in delimiters to preserve content with special characters
            sb.Append("SUMMARY_START");
            sb.Append(Summary ?? string.Empty);
            sb.Append("SUMMARY_END");

            if (!string.IsNullOrWhiteSpace(Command))
            {
                sb.Append("|COMMAND:");
                sb.Append(Command);
            }

            sb.Append("|TIMESTAMP:");
            sb.Append(Timestamp.ToString("O"));

            return sb.ToString();
        }

        /// <summary>
        /// Deserialize from tool result string
        /// </summary>
        public static CompletionResult Deserialize(string serialized)
        {
            if (string.IsNullOrEmpty(serialized) || !serialized.StartsWith("TASK_COMPLETED:"))
                return null;

            var result = new CompletionResult();
            var content = serialized.Substring("TASK_COMPLETED:".Length);

            // Extract summary using delimiters
            var summaryStartIdx = content.IndexOf("SUMMARY_START");
            var summaryEndIdx = content.IndexOf("SUMMARY_END");

            if (summaryStartIdx >= 0 && summaryEndIdx > summaryStartIdx)
            {
                var summaryStart = summaryStartIdx + "SUMMARY_START".Length;
                result.Summary = content.Substring(summaryStart, summaryEndIdx - summaryStart);

                // Parse remaining fields after SUMMARY_END
                var remainingContent = content.Substring(summaryEndIdx + "SUMMARY_END".Length);
                var parts = remainingContent.Split('|');

                foreach (var part in parts)
                {
                    if (part.StartsWith("COMMAND:"))
                    {
                        result.Command = part.Substring("COMMAND:".Length);
                    }
                    else if (part.StartsWith("TIMESTAMP:"))
                    {
                        var timestampStr = part.Substring("TIMESTAMP:".Length);
                        if (DateTime.TryParse(timestampStr, out var timestamp))
                        {
                            result.Timestamp = timestamp;
                        }
                    }
                }
            }
            else
            {
                // Fallback for old format or malformed data
                var parts = content.Split('|');
                foreach (var part in parts)
                {
                    if (part.StartsWith("SUMMARY:"))
                    {
                        result.Summary = part.Substring("SUMMARY:".Length);
                    }
                    else if (part.StartsWith("COMMAND:"))
                    {
                        result.Command = part.Substring("COMMAND:".Length);
                    }
                    else if (part.StartsWith("TIMESTAMP:"))
                    {
                        var timestampStr = part.Substring("TIMESTAMP:".Length);
                        if (DateTime.TryParse(timestampStr, out var timestamp))
                        {
                            result.Timestamp = timestamp;
                        }
                    }
                }

                // If still no summary, treat entire content as summary
                if (string.IsNullOrEmpty(result.Summary))
                {
                    result.Summary = content;
                }
            }

            return result;
        }
    }

    /// <summary>
    /// User feedback on task completion
    /// </summary>
    public enum CompletionFeedback
    {
        None,
        Satisfied,
        Unsatisfied
    }
}

namespace AICA.Core.Agent
{
    /// <summary>
    /// Classification of tool failures for recovery handling.
    /// </summary>
    public enum ToolFailureKind
    {
        RecoverableFeedback,
        Blocking
    }

    /// <summary>
    /// Tracks the state of an Agent task execution.
    /// Extracted from AgentExecutor for clarity and reuse.
    /// </summary>
    public class TaskState
    {
        // Streaming flags
        public bool IsStreaming { get; set; }

        // Tool execution flags
        public bool DidRejectTool { get; set; }
        public bool DidEditFile { get; set; }
        public bool HasEverUsedTools { get; set; }
        public string LastToolName { get; set; } = string.Empty;

        // Error tracking
        public int ConsecutiveBlockingFailureCount { get; set; }
        public int ConsecutiveRecoverableFailureCount { get; set; }
        public int RecoveryPromptCount { get; set; }
        public int MaxConsecutiveMistakes { get; set; } = 3;
        public int MaxRecoveryPrompts { get; set; } = 2;

        // Task control
        public bool Abort { get; set; }
        public bool IsCompleted { get; set; }
        public int ApiRequestCount { get; set; }
        public int Iteration { get; set; }

        // Tool call counting for force-completion
        public int TotalToolCallCount { get; set; }

        // noToolsUsed tracking
        public int ConsecutiveNoToolCount { get; set; }

        // User cancellation/rejection tracking
        public int UserCancellationCount { get; set; }
        public const int MaxUserCancellations = 3;

        // Context pressure tracking
        public bool HasCondenseHinted { get; set; }

        /// <summary>
        /// Reset failure counters after a successful step.
        /// </summary>
        public void ResetFailureCounts()
        {
            ConsecutiveBlockingFailureCount = 0;
            ConsecutiveRecoverableFailureCount = 0;
        }

        /// <summary>
        /// Record a tool failure classification.
        /// </summary>
        /// <returns>True if blocking failure threshold reached.</returns>
        public bool RecordToolFailure(ToolFailureKind kind)
        {
            if (kind == ToolFailureKind.Blocking)
            {
                ConsecutiveBlockingFailureCount++;
                ConsecutiveRecoverableFailureCount = 0;
                return ConsecutiveBlockingFailureCount >= MaxConsecutiveMistakes;
            }

            ConsecutiveRecoverableFailureCount++;
            return false;
        }

        /// <summary>
        /// Check whether blocking failures have reached the recovery threshold.
        /// </summary>
        public bool HasReachedBlockingFailureThreshold()
        {
            return ConsecutiveBlockingFailureCount >= MaxConsecutiveMistakes;
        }

        /// <summary>
        /// Check whether a recovery prompt can still be injected before aborting.
        /// </summary>
        public bool CanPromptRecovery()
        {
            return RecoveryPromptCount < MaxRecoveryPrompts;
        }

        /// <summary>
        /// Mark that a recovery prompt was injected.
        /// </summary>
        public void RecordRecoveryPrompt()
        {
            RecoveryPromptCount++;
        }

        /// <summary>
        /// Reset blocking failure count after a recovery prompt so the model gets a fresh recovery window.
        /// </summary>
        public void ResetBlockingFailuresForRecovery()
        {
            ConsecutiveBlockingFailureCount = 0;
        }

        /// <summary>
        /// Record that no tools were used in this iteration
        /// </summary>
        /// <returns>True if consecutive no-tool count exceeds 2</returns>
        public bool RecordNoToolsUsed()
        {
            ConsecutiveNoToolCount++;
            return ConsecutiveNoToolCount > 2;
        }

        /// <summary>
        /// Reset the no-tools counter (called when a tool is executed)
        /// </summary>
        public void ResetNoToolCount()
        {
            ConsecutiveNoToolCount = 0;
        }
    }
}

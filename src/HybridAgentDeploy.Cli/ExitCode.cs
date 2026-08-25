namespace HybridAgentDeploy.Cli;

/// <summary>
/// Process exit codes (PRD 9).
/// </summary>
/// <remarks>
/// These are the CLI's contract with whatever script invoked it, so the values are fixed and
/// the meanings do not overlap. In particular a deployment that ran and had failures (1) is
/// distinct from one that never started because the invocation was wrong (3), and from one
/// halted part way by the circuit breaker (2) — a script that retries should treat those
/// three very differently.
/// </remarks>
internal static class ExitCode
{
    /// <summary>Every target succeeded, or a non-deployment command completed.</summary>
    public const int Success = 0;

    /// <summary>The run completed, but at least one target failed.</summary>
    public const int CompletedWithFailures = 1;

    /// <summary>The circuit breaker halted the run (PRD R7.3). Targets were left unattempted.</summary>
    public const int HaltedByCircuitBreaker = 2;

    /// <summary>Invalid arguments, or the PRD 11 pre-flight checklist failed. Nothing was attempted.</summary>
    public const int InvalidArgumentsOrPreflight = 3;

    /// <summary>The operator cancelled. In-flight targets were allowed to finish.</summary>
    public const int Cancelled = 4;
}

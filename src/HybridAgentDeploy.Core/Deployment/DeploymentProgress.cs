using HybridAgentDeploy.Core.Models;

namespace HybridAgentDeploy.Core.Deployment;

/// <summary>What happened to one target, in memory, for the caller and the run log.</summary>
public sealed class TargetOutcome
{
    public required DeploymentTarget Target { get; init; }

    /// <summary>The <c>deployment_result</c> row id, so the caller can drill into history.</summary>
    public long ResultId { get; init; }

    public required DeploymentOutcome Outcome { get; init; }

    /// <summary>The stage reached, or the stage that failed.</summary>
    public DeploymentStage? Stage { get; init; }

    public int? ExitCode { get; init; }
    public ErrorCategory? ErrorCategory { get; init; }

    /// <summary>PRD 10.4: names the stage, the DC, and the next diagnostic step.</summary>
    public string? ErrorDetail { get; init; }

    /// <summary>1 except for a 1618 retry sequence (PRD 10.1, R7.6).</summary>
    public int AttemptCount { get; init; } = 1;

    /// <summary>Local path of the retrieved msiexec verbose log, when it was retrieved.</summary>
    public string? MsiLogPath { get; init; }

    public DateTimeOffset StartedUtc { get; init; }
    public DateTimeOffset CompletedUtc { get; init; }

    public TimeSpan Duration => CompletedUtc - StartedUtc;

    /// <summary>
    /// True for 1641, where a reboot was initiated on a domain controller. Surfaced
    /// separately so it is not lost inside a success count.
    /// </summary>
    public bool RequiresProminentWarning { get; init; }

    /// <summary>Non-fatal problems, such as a staging directory that could not be removed.</summary>
    public IReadOnlyList<string> Warnings { get; init; } = [];

    public bool IsSuccess => Outcome.IsSuccess();
}

/// <summary>What a target is currently doing, for the live progress grid (PRD 8.4).</summary>
public enum TargetProgressState
{
    Pending,
    WaitingForSlot,
    Running,
    Completed,
    SkippedByHalt,
    SkippedByCancellation,
}

/// <summary>
/// A progress notification. Reported through <see cref="IProgress{T}"/> so the GUI marshals
/// to the UI thread itself and Core stays free of any UI concern.
/// </summary>
/// <param name="OccupiedSlots">
/// How many concurrency slots are in use. PRD 8.4 requires this to be visible, and it is the
/// clearest signal that the tool is respecting its own ceiling.
/// </param>
public sealed record DeploymentProgress(
    DeploymentTarget Target,
    TargetProgressState State,
    DeploymentStage? Stage,
    int CompletedCount,
    int TotalCount,
    int SuccessCount,
    int FailureCount,
    int OccupiedSlots,
    string? Message);

/// <summary>
/// The result of a whole run.
/// </summary>
public sealed class DeploymentRunSummary
{
    public required Guid RunGuid { get; init; }

    /// <summary>The <c>deployment_run</c> row id.</summary>
    public required long RunId { get; init; }

    public required IReadOnlyList<TargetOutcome> Outcomes { get; init; }

    public required DateTimeOffset StartedUtc { get; init; }
    public required DateTimeOffset CompletedUtc { get; init; }

    /// <summary>True when the circuit breaker halted the run (PRD R7.3).</summary>
    public bool WasHalted { get; init; }

    public string? HaltReason { get; init; }

    /// <summary>True when the operator cancelled (PRD 8.4).</summary>
    public bool WasCancelled { get; init; }

    public required string LogDirectory { get; init; }

    /// <summary>
    /// The msiexec command as it was run, with the Org ID in place. Recorded so a customer
    /// can show a change-control board exactly what executed on their domain controllers
    /// (PRD 12.2).
    /// </summary>
    public required string CommandTemplate { get; init; }

    public int SuccessCount => Outcomes.Count(o => o.IsSuccess);

    public int FailureCount => Outcomes.Count(o => !o.IsSuccess && o.Outcome != DeploymentOutcome.Skipped);

    public int SkippedCount => Outcomes.Count(o => o.Outcome == DeploymentOutcome.Skipped);

    /// <summary>
    /// Targets worth offering a retry for (PRD 8.4). Excludes successes and excludes targets
    /// that were skipped because the run halted — those were never attempted, so retrying
    /// them without fixing the underlying cause would simply re-trip the breaker.
    /// </summary>
    public IReadOnlyList<DeploymentTarget> FailedTargets =>
        [.. Outcomes.Where(o => !o.IsSuccess && o.Outcome != DeploymentOutcome.Skipped)
            .Select(o => o.Target)];

    /// <summary>Targets never attempted, because the run halted or was cancelled.</summary>
    public IReadOnlyList<DeploymentTarget> UnattemptedTargets =>
        [.. Outcomes.Where(o => o.Outcome == DeploymentOutcome.Skipped).Select(o => o.Target)];
}

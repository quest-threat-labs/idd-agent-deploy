using HybridAgentDeploy.Core.Models;

namespace HybridAgentDeploy.Core.Deployment;

/// <summary>One thing checked about a target, and what was found.</summary>
/// <param name="Detail">
/// On failure, what is wrong and what to do about it (PRD 10.4). On success, what was
/// confirmed — a checklist that only ever says "OK" stops being read.
/// </param>
public sealed record ValidationCheck(string Name, bool Passed, string Detail);

/// <summary>The Change Auditor agent found on a target, if any.</summary>
public sealed record InstalledAgent(string DisplayName, string Version, string ProductCode);

/// <summary>
/// What deploying the selected package to a target would actually do.
/// </summary>
/// <remarks>
/// Worked out before the run rather than discovered during it. The difference between a fresh
/// install, an upgrade, a reinstall of the same build, and a refused downgrade is the
/// difference between exit code 0 and exit code 1638 — and finding that out from a failed
/// deployment against sixty domain controllers is an expensive way to learn it.
/// </remarks>
public enum PredictedAction
{
    /// <summary>No agent present: a clean install.</summary>
    FreshInstall,

    /// <summary>An older version is present and will be replaced.</summary>
    Upgrade,

    /// <summary>The same version is already present; msiexec would reinstall it.</summary>
    Reinstall,

    /// <summary>A newer version is present. The installer is likely to refuse (1638).</summary>
    DowngradeBlocked,

    /// <summary>
    /// An agent is present but the two versions could not be compared.
    /// </summary>
    /// <remarks>
    /// Reported rather than guessed at. A wrong prediction is worse than none, because the
    /// operator would act on it.
    /// </remarks>
    Unknown,
}

/// <summary>Everything validation established about one target.</summary>
public sealed class ValidationOutcome
{
    public required DeploymentTarget Target { get; init; }

    public required IReadOnlyList<ValidationCheck> Checks { get; init; }

    /// <summary>Null when no agent is installed, or when it could not be read.</summary>
    public InstalledAgent? InstalledAgent { get; init; }

    public PredictedAction Predicted { get; init; } = PredictedAction.Unknown;

    /// <summary>Set when a check failed, for the same categories a deployment reports.</summary>
    public ErrorCategory? ErrorCategory { get; init; }

    public DateTimeOffset StartedUtc { get; init; }
    public DateTimeOffset CompletedUtc { get; init; }

    public TimeSpan Duration => CompletedUtc - StartedUtc;

    public bool Passed => Checks.All(c => c.Passed);

    public IReadOnlyList<ValidationCheck> Failures => [.. Checks.Where(c => !c.Passed)];

    /// <summary>
    /// A one-line verdict for the results grid.
    /// </summary>
    /// <remarks>
    /// A target that is reachable but would be refused by the installer is not simply "ready" —
    /// the prediction belongs in the headline, not buried in a detail column.
    /// </remarks>
    public string Summary => Passed
        ? Predicted switch
        {
            PredictedAction.FreshInstall => "ready - fresh install",
            PredictedAction.Upgrade => $"ready - upgrade from {InstalledAgent?.Version}",
            PredictedAction.Reinstall => $"ready - would reinstall {InstalledAgent?.Version}",
            PredictedAction.DowngradeBlocked =>
                $"reachable, but {InstalledAgent?.Version} is newer - the installer will refuse",
            _ => $"ready - {InstalledAgent?.Version ?? "the installed version"} could not be " +
                 "compared with the package",
        }
        : Failures[0].Detail;
}

/// <summary>What a validation run produced.</summary>
public sealed class ValidationRunSummary
{
    public required Guid RunGuid { get; init; }
    public required IReadOnlyList<ValidationOutcome> Outcomes { get; init; }
    public required DateTimeOffset StartedUtc { get; init; }
    public required DateTimeOffset CompletedUtc { get; init; }
    public required string LogDirectory { get; init; }
    public bool WasCancelled { get; init; }

    public int ReadyCount => Outcomes.Count(o => o.Passed);

    public int ProblemCount => Outcomes.Count(o => !o.Passed);

    /// <summary>
    /// Targets that are reachable but where the installer would refuse the package.
    /// </summary>
    /// <remarks>
    /// Counted separately because they are neither a failure of the environment nor a green
    /// light. Nothing is wrong with the domain controller; the package is simply not the one
    /// to send it.
    /// </remarks>
    public int WouldBeRefusedCount =>
        Outcomes.Count(o => o.Passed && o.Predicted == PredictedAction.DowngradeBlocked);
}

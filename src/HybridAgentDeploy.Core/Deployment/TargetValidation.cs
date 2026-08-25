using HybridAgentDeploy.Core.Models;

namespace HybridAgentDeploy.Core.Deployment;

/// <summary>One thing checked about a target, and what was found.</summary>
/// <param name="Detail">
/// On failure, what is wrong and what to do about it (PRD 10.4). On success, what was
/// confirmed — a checklist that only ever says "OK" stops being read.
/// </param>
public sealed record ValidationCheck(string Name, bool Passed, string Detail);

/// <summary>
/// Which product an installed agent reports to.
/// </summary>
/// <remarks>
/// Read from <c>HKLM\SOFTWARE\Quest\ChangeAuditor\Agent\SgConnectionMode</c>, which is the
/// same value the installer itself reads as <c>SG_MODE_FROM_REGISTRY</c> when deciding
/// whether an upgrade crosses modes.
/// </remarks>
public enum AgentMode
{
    /// <summary>On-premises Change Auditor. <c>SgConnectionMode</c> is 0, and SG is omitted.</summary>
    ChangeAuditor,

    /// <summary>The Identity Defense cloud product. <c>SgConnectionMode</c> is 1, from SG=1.</summary>
    IdentityDefense,

    /// <summary>The value was present but not one this tool recognises.</summary>
    Unrecognised,
}

/// <summary>The Change Auditor agent found on a target, if any.</summary>
/// <param name="Mode">
/// Null when the agent is installed but its connection mode could not be read — an older
/// build, or a registry layout this tool does not know. Reported as unknown rather than
/// assumed, because assuming it matches is the assumption that produces a failed deployment.
/// </param>
public sealed record InstalledAgent(
    string DisplayName,
    string Version,
    string ProductCode,
    AgentMode? Mode = null);

/// <summary>The target's operating system, as read from the target itself.</summary>
/// <remarks>
/// Read live rather than taken from the inventory. Enumeration is an explicit operator action
/// and the inventory can be days old; a domain controller rebuilt since then would be checked
/// against a stale answer, which is the failure this check exists to prevent.
/// </remarks>
public sealed record TargetOperatingSystem(int BuildNumber, string? ProductName)
{
    /// <summary>
    /// Windows Server 2016. The agent MSI refuses anything older through a launch condition,
    /// so an older domain controller cannot receive this package at all.
    /// </summary>
    /// <remarks>
    /// PRD Q7 assumed Server 2012 R2 and later. The package itself says otherwise, and the
    /// package is the authority: its <c>LaunchCondition</c> reads "The minimum supported
    /// version for the agent is Windows Server 2016."
    /// </remarks>
    public const int MinimumSupportedBuild = 14393;

    public bool IsSupported => BuildNumber >= MinimumSupportedBuild;

    public string Describe() =>
        string.IsNullOrWhiteSpace(ProductName)
            ? $"build {BuildNumber}"
            : $"{ProductName} (build {BuildNumber})";
}

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

    /// <summary>The mode this run would install in, from the cloud-mode setting.</summary>
    public AgentMode RequestedMode { get; init; } = AgentMode.IdentityDefense;

    /// <summary>Null when the OS could not be read.</summary>
    public TargetOperatingSystem? OperatingSystem { get; init; }

    /// <summary>
    /// True when an agent is installed in a different mode from the one this run would use.
    /// </summary>
    /// <remarks>
    /// The installer detects this itself and sets <c>UPGRADE_SG_MISMATCH</c>, so the
    /// deployment would fail. Worked out here so the operator learns before the run rather
    /// than from sixty failed installs — and because the fix is a checkbox, not a package.
    /// </remarks>
    public bool HasModeMismatch =>
        InstalledAgent?.Mode is { } installed &&
        installed != AgentMode.Unrecognised &&
        installed != RequestedMode;

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
    /// the prediction belongs in the headline, not buried in a detail column. A mode mismatch
    /// outranks the version comparison: an upgrade that crosses modes fails regardless of which
    /// version is newer, so saying "ready - upgrade" would be actively misleading.
    /// </remarks>
    public string Summary => Passed
        ? HasModeMismatch
            ? $"reachable, but the installed agent reports to " +
              $"{Describe(InstalledAgent!.Mode!.Value)} and this run would install it for " +
              $"{Describe(RequestedMode)} - the installer refuses a mode change"
            : Predicted switch
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

    /// <summary>The product a mode points at, named as the operator knows it.</summary>
    public static string Describe(AgentMode mode) => mode switch
    {
        AgentMode.IdentityDefense => "Identity Defense (cloud)",
        AgentMode.ChangeAuditor => "Change Auditor (on-premises)",
        _ => "an unrecognised product",
    };
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
    /// Targets that are reachable but where the installer would refuse this deployment.
    /// </summary>
    /// <remarks>
    /// Counted separately because they are neither a failure of the environment nor a green
    /// light. Nothing is wrong with the domain controller; the package or the mode is simply
    /// not the one to send it. Covers both a blocked downgrade and a mode change.
    /// </remarks>
    public int WouldBeRefusedCount =>
        Outcomes.Count(o => o.Passed &&
            (o.Predicted == PredictedAction.DowngradeBlocked || o.HasModeMismatch));

    /// <summary>Targets where an agent is installed for the other product.</summary>
    public int ModeMismatchCount => Outcomes.Count(o => o.Passed && o.HasModeMismatch);
}

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

/// <summary>
/// What deploying in the selected mode would do to the mode already installed.
/// </summary>
/// <remarks>
/// <para>
/// <b>Asymmetric.</b> Per the agent's developer documentation, installing with <c>SG=1</c>
/// over an agent running in Change Auditor mode migrates it to the cloud product. The reverse
/// does not work: an agent already reporting to Identity Defense is not moved back to Change
/// Auditor by installing without <c>SG</c>.
/// </para>
/// <para>
/// The MSI corroborates the supported direction rather than contradicting it. Its
/// <c>UPGRADE_SG_MISMATCH</c> property gates exactly one thing — whether the previously
/// registered installation name is carried forward — which is precisely what a migration
/// needs, since a Change Auditor installation name is meaningless as an Identity Defense
/// tenant GUID. No launch condition, error row, or custom action blocks on it. An earlier
/// version of this code read the property's name as "the installer refuses a mode change";
/// it does not.
/// </para>
/// <para>
/// The unsupported direction is therefore taken from the documentation, not from anything
/// enforced in the package. The utility reports it rather than blocking on it.
/// </para>
/// </remarks>
public enum ModeChange
{
    /// <summary>Nothing installed, or the installed mode already matches.</summary>
    None,

    /// <summary>
    /// Change Auditor to Identity Defense. Supported — the installer migrates the agent.
    /// </summary>
    MigratesToCloud,

    /// <summary>
    /// Identity Defense to Change Auditor. The agent does not move back this way.
    /// </summary>
    NotSupported,
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
    /// What this run would do to the mode already installed. See <see cref="ModeChange"/> for
    /// why the two directions are not equivalent.
    /// </summary>
    /// <remarks>
    /// Worked out before the run because both directions are worth knowing about in advance:
    /// one changes which product a domain controller reports to, and the other will not do
    /// what the operator asked. Neither is visible from a version comparison.
    /// </remarks>
    public ModeChange ModeChange
    {
        get
        {
            if (InstalledAgent?.Mode is not { } installed ||
                installed == AgentMode.Unrecognised ||
                installed == RequestedMode)
            {
                return ModeChange.None;
            }

            return RequestedMode == AgentMode.IdentityDefense
                ? ModeChange.MigratesToCloud
                : ModeChange.NotSupported;
        }
    }

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
    /// the prediction belongs in the headline, not buried in a detail column. A mode change
    /// outranks the version comparison in both of its forms: a migration changes which product
    /// the domain controller reports to, which matters more than which build it lands on, and
    /// the unsupported direction will not happen at all.
    /// </remarks>
    public string Summary => Passed
        ? ModeChange switch
        {
            ModeChange.MigratesToCloud =>
                "ready - MIGRATES from Change Auditor (on-premises) to Identity Defense " +
                $"(cloud){VersionSuffix}",

            ModeChange.NotSupported =>
                "reachable, but the agent reports to Identity Defense (cloud) and does not " +
                "move back to Change Auditor (on-premises) - this run would not change it",

            _ => Predicted switch
            {
                PredictedAction.FreshInstall => "ready - fresh install",
                PredictedAction.Upgrade => $"ready - upgrade from {InstalledAgent?.Version}",
                PredictedAction.Reinstall => $"ready - would reinstall {InstalledAgent?.Version}",
                PredictedAction.DowngradeBlocked =>
                    $"reachable, but {InstalledAgent?.Version} is newer - the installer will refuse",
                _ => $"ready - {InstalledAgent?.Version ?? "the installed version"} could not be " +
                     "compared with the package",
            },
        }
        : Failures[0].Detail;

    /// <summary>
    /// What the migration does to the version, appended to the migration headline.
    /// </summary>
    /// <remarks>
    /// A migration is also an install of some version, and the operator still needs to know
    /// which — but it is the smaller of the two facts, so it follows rather than leads. A
    /// downgrade is called out because the installer would refuse it and the migration would
    /// not happen either.
    /// </remarks>
    private string VersionSuffix => Predicted switch
    {
        PredictedAction.Upgrade => $", upgrading from {InstalledAgent?.Version}",
        PredictedAction.Reinstall => $", staying on {InstalledAgent?.Version}",
        PredictedAction.DowngradeBlocked =>
            $" - but {InstalledAgent?.Version} is newer than this package, so the installer " +
            "will refuse and neither the migration nor the install will happen",
        _ => string.Empty,
    };

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
    /// Targets that are reachable but where this deployment would not do what was asked.
    /// </summary>
    /// <remarks>
    /// Counted separately because they are neither a failure of the environment nor a green
    /// light. Nothing is wrong with the domain controller; the package or the mode is simply
    /// not the one to send it. A migration is deliberately <em>not</em> counted here — it is a
    /// supported operation that succeeds.
    /// </remarks>
    public int WouldBeRefusedCount =>
        Outcomes.Count(o => o.Passed &&
            (o.Predicted == PredictedAction.DowngradeBlocked ||
             o.ModeChange == ModeChange.NotSupported));

    /// <summary>
    /// Targets that would be migrated from Change Auditor to Identity Defense.
    /// </summary>
    /// <remarks>
    /// Called out on its own because it is the one supported operation in this tool that
    /// changes which product a domain controller reports to, and because the checkbox driving
    /// it defaults to on — so it can be reached by inaction rather than by decision.
    /// </remarks>
    public int MigrationCount => Outcomes.Count(o => o.Passed && o.ModeChange == ModeChange.MigratesToCloud);

    /// <summary>Targets already on Identity Defense that this run cannot move back.</summary>
    public int UnsupportedModeChangeCount =>
        Outcomes.Count(o => o.Passed && o.ModeChange == ModeChange.NotSupported);
}

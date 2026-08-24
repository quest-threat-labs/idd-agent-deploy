using HybridAgentDeploy.Core.Models;

namespace HybridAgentDeploy.Core.Deployment;

/// <summary>
/// One domain controller selected for a run.
/// </summary>
/// <param name="SiteName">
/// Drives the R7.2 site guard. Null for hosts that arrived by file import, which never
/// contacts Active Directory; those share one serialised bucket.
/// </param>
public sealed record DeploymentTarget(long DcId, string Fqdn, string? SiteName)
{
    public static DeploymentTarget From(DomainControllerRecord record) =>
        new(record.Id, record.Fqdn, record.SiteName);
}

/// <summary>
/// Everything one deployment run needs.
/// </summary>
/// <remarks>
/// Values are clamped on construction rather than trusted, so no caller — GUI, CLI, or a
/// future one — can route around the R7.1 ceiling or the R7.5 timeout bounds.
/// </remarks>
public sealed class DeploymentRequest
{
    public required MsiPackageInfo Msi { get; init; }

    /// <summary>Validated and trimmed by <see cref="OrgId.Validate"/> before reaching here.</summary>
    public required string OrgId { get; init; }

    public required IReadOnlyList<DeploymentTarget> Targets { get; init; }

    /// <summary>
    /// Emits <c>SG=1</c>. PRD-OPEN-Q: Q1 — defaults to on pending confirmation that cloud
    /// mode is the only mode this utility targets.
    /// </summary>
    public bool CloudMode { get; init; } = true;

    /// <summary>DOMAIN\user. Recorded against the run; never a password (SEC1, PRD 12.3).</summary>
    public required string OperatorAccount { get; init; }

    /// <summary>The run's own log directory (PRD 12.1).</summary>
    public required string LogDirectory { get; init; }

    /// <summary>
    /// Active DCs per site, for the R7.2 guard. Counts the inventory, not the selection.
    /// </summary>
    public IReadOnlyDictionary<string, int> ActiveDcsPerSite { get; init; } =
        new Dictionary<string, int>();

    private readonly int _maxParallel = DeploymentLimits.MaxConcurrencyCeiling;

    /// <summary>
    /// Requested concurrency, clamped to 1..5 on assignment. PRD R7.1: there must be no code
    /// path, configuration file, environment variable, or CLI flag that raises it above five.
    /// </summary>
    public int MaxParallel
    {
        get => _maxParallel;
        init => _maxParallel = DeploymentLimits.ClampParallelism(value);
    }

    /// <summary>True when the operator asked for more than the ceiling, for the run log.</summary>
    public bool ParallelismWasClamped { get; private init; }

    private readonly TimeSpan _perTargetTimeout = TimeSpan.FromMinutes(DeploymentLimits.DefaultTimeoutMinutes);

    /// <summary>
    /// Bounds the <em>entire</em> per-target sequence — pre-flight through cleanup, including
    /// any 1618 backoff — not just the msiexec call. R7.5 calls this the timeout "per
    /// deployment operation", and the operation is the whole sequence.
    /// </summary>
    public TimeSpan PerTargetTimeout
    {
        get => _perTargetTimeout;
        init => _perTargetTimeout = DeploymentLimits.ClampTimeout((int)Math.Round(value.TotalMinutes));
    }

    /// <summary>
    /// Builds a request, clamping concurrency and recording whether clamping occurred.
    /// </summary>
    public static DeploymentRequest Create(
        MsiPackageInfo msi,
        string orgId,
        IReadOnlyList<DeploymentTarget> targets,
        string operatorAccount,
        string logDirectory,
        bool cloudMode = true,
        int maxParallel = DeploymentLimits.MaxConcurrencyCeiling,
        int timeoutMinutes = DeploymentLimits.DefaultTimeoutMinutes,
        IReadOnlyDictionary<string, int>? activeDcsPerSite = null) =>
        new()
        {
            Msi = msi,
            OrgId = orgId,
            Targets = targets,
            OperatorAccount = operatorAccount,
            LogDirectory = logDirectory,
            CloudMode = cloudMode,
            MaxParallel = maxParallel,
            PerTargetTimeout = TimeSpan.FromMinutes(timeoutMinutes),
            ActiveDcsPerSite = activeDcsPerSite ?? new Dictionary<string, int>(),
            ParallelismWasClamped = DeploymentLimits.WasParallelismClamped(maxParallel),
        };
}

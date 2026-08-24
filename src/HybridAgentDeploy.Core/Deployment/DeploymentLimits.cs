namespace HybridAgentDeploy.Core.Deployment;

/// <summary>
/// The safety ceilings that exist because the targets are domain controllers (PRD 7).
/// </summary>
/// <remarks>
/// <para>
/// <see cref="MaxConcurrencyCeiling"/> is a compile-time constant, and every operator-supplied
/// value is clamped to it by <see cref="ClampParallelism"/>. There MUST be no configuration
/// file, environment variable, or CLI flag that raises it (PRD R7.1, CLAUDE.md). If a change
/// would introduce one, stop and raise it rather than implementing it.
/// </para>
/// <para>
/// Exceeding the ceiling is not an error the operator has to work around: clamp silently
/// and note it in the run log.
/// </para>
/// </remarks>
public static class DeploymentLimits
{
    /// <summary>
    /// Never deploy to more DCs at once than an administrator can reason about (PRD G4).
    /// A concurrency slot spans the entire PRD 5.4 sequence — pre-flight through cleanup —
    /// and is released only when that whole sequence terminates, not when msiexec returns.
    /// </summary>
    public const int MaxConcurrencyCeiling = 5;

    public const int MinConcurrency = 1;

    public const int MinTimeoutMinutes = 5;
    public const int DefaultTimeoutMinutes = 15;
    public const int MaxTimeoutMinutes = 60;

    /// <summary>
    /// Clamps a requested parallelism to [1, 5]. Callers do not get to opt out of this.
    /// </summary>
    public static int ClampParallelism(int requested) =>
        Math.Clamp(requested, MinConcurrency, MaxConcurrencyCeiling);

    /// <summary>
    /// True when the requested value was above the ceiling, so the run log can record that
    /// the operator asked for more than they received.
    /// </summary>
    public static bool WasParallelismClamped(int requested) => requested > MaxConcurrencyCeiling;

    public static TimeSpan ClampTimeout(int requestedMinutes) =>
        TimeSpan.FromMinutes(Math.Clamp(requestedMinutes, MinTimeoutMinutes, MaxTimeoutMinutes));

    /// <summary>
    /// Within one AD site, never occupy more than half the active DCs at once, rounded
    /// down, with a floor of 1 (PRD R7.2). In a two-DC site this means one at a time.
    /// </summary>
    public static int SiteConcurrencyLimit(int activeDcsInSite) =>
        Math.Max(1, activeDcsInSite / 2);
}

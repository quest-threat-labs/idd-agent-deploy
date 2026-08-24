namespace HybridAgentDeploy.Core.Inventory;

/// <summary>
/// How current the inventory is.
/// </summary>
/// <remarks>
/// <para>
/// Active Directory enumeration is the only way a domain controller enters the inventory, and
/// it is an explicit operator action (PRD 8.1's button, PRD 9's <c>enumerate</c> command)
/// rather than something the tool does on its own. That makes it possible to deploy from a
/// stale picture, so the staleness is surfaced: to the operator before a run, and in the run
/// log afterwards, where it forms part of the record of what the tool believed at the time.
/// </para>
/// <para>
/// No schema addition was needed. The most recent <c>last_seen_utc</c> across AD-sourced rows
/// is by definition when enumeration last ran.
/// </para>
/// </remarks>
/// <param name="ActiveDomainControllers">Count of active DCs available as deployment targets.</param>
/// <param name="LastEnumerationUtc">
/// When Active Directory was last enumerated, or null if it never has been.
/// </param>
public sealed record InventoryStatus(int ActiveDomainControllers, DateTimeOffset? LastEnumerationUtc)
{
    /// <summary>
    /// Beyond this age the operator is warned before deploying. Not a hard block: an
    /// administrator who knows their forest has not changed should not be stopped, and a
    /// hard expiry would be worked around rather than heeded.
    /// </summary>
    public static readonly TimeSpan StaleAfter = TimeSpan.FromDays(7);

    public bool HasNeverEnumerated => LastEnumerationUtc is null;

    public bool IsEmpty => ActiveDomainControllers == 0;

    public TimeSpan? Age(DateTimeOffset now) =>
        LastEnumerationUtc is null ? null : now - LastEnumerationUtc.Value;

    public bool IsStale(DateTimeOffset now) =>
        HasNeverEnumerated || Age(now) > StaleAfter;

    /// <summary>
    /// A warning for the operator, or null when the inventory is current. PRD 10.4: says what
    /// is wrong and what to do about it.
    /// </summary>
    public string? StalenessWarning(DateTimeOffset now)
    {
        if (HasNeverEnumerated)
        {
            return "Active Directory has never been enumerated in this inventory. The target " +
                   "list and the site information the concurrency guard depends on are both " +
                   "unknown. Enumerate before deploying.";
        }

        var age = Age(now)!.Value;
        if (age <= StaleAfter)
        {
            return null;
        }

        return $"Active Directory was last enumerated {age.TotalDays:0} days ago " +
               $"({UtcTimestamp.Format(LastEnumerationUtc!.Value)}). A domain controller " +
               "promoted, demoted, or moved between sites since then will not be reflected " +
               "here. Re-enumerate before deploying if the forest may have changed.";
    }

    /// <summary>Describes the inventory's currency for the run log (PRD 12.2).</summary>
    public string Describe(DateTimeOffset now) =>
        HasNeverEnumerated
            ? "never enumerated"
            : $"{UtcTimestamp.Format(LastEnumerationUtc!.Value)} " +
              $"({Age(now)!.Value.TotalHours:0} hours before this run)";
}

namespace HybridAgentDeploy.Core.Models;

/// <summary>
/// A row of the <c>domain_controller</c> table (PRD 6).
/// </summary>
/// <remarks>
/// The inventory model is DC-shaped by design (PRD 3.2 rules out non-DC targets).
/// Enumeration and import are additive and non-destructive: a DC that disappears from
/// Active Directory has <see cref="IsActive"/> cleared, but its row, tags, and deployment
/// history are retained (PRD 6.2).
/// </remarks>
public sealed class DomainControllerRecord
{
    /// <summary>Zero until the row has been written; assigned by SQLite on insert.</summary>
    public long Id { get; set; }

    /// <summary>Unique, case-insensitive. The identity of a DC in this inventory.</summary>
    public required string Fqdn { get; init; }

    public string? NetbiosName { get; init; }
    public string? Domain { get; init; }
    public string? SiteName { get; init; }
    public string? OsVersion { get; init; }

    /// <summary>
    /// True for a read-only domain controller. Populated from the LDAP computer object,
    /// not from System.DirectoryServices.ActiveDirectory, which does not expose it.
    /// </summary>
    public bool IsReadOnly { get; init; }

    public bool IsGlobalCatalog { get; init; }

    public required DiscoverySource Source { get; init; }

    public string FirstSeenUtc { get; set; } = UtcTimestamp.Now();
    public string LastSeenUtc { get; set; } = UtcTimestamp.Now();
    public bool IsActive { get; set; } = true;
}

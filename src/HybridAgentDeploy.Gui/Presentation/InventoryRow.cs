using HybridAgentDeploy.Core.Models;

namespace HybridAgentDeploy.Gui.Presentation;

/// <summary>
/// One row of the inventory grid (PRD 8.1): a domain controller joined to its tags and its
/// last known deployment state.
/// </summary>
/// <remarks>
/// Assembled once from three queries rather than looked up per row. NFR3 allows one second to
/// render 500 domain controllers, which a per-row lookup would not fit.
/// </remarks>
public sealed record InventoryRow
{
    public required long DcId { get; init; }
    public required string Fqdn { get; init; }
    public string? Domain { get; init; }
    public string? SiteName { get; init; }
    public string? OsVersion { get; init; }
    public bool IsReadOnly { get; init; }
    public bool IsGlobalCatalog { get; init; }
    public IReadOnlyList<string> Tags { get; init; } = [];

    /// <summary>Null when this DC has never been deployed to.</summary>
    public DcLastDeployment? LastDeployment { get; init; }

    public string? LastDeployedVersion => LastDeployment?.MsiProductVersion;

    public string? LastDeployedUtc => LastDeployment?.CompletedUtc;

    public DeploymentOutcome? LastOutcome => LastDeployment?.Outcome;

    public static InventoryRow From(
        DomainControllerRecord record,
        IReadOnlyList<string> tags,
        DcLastDeployment? lastDeployment) => new()
        {
            DcId = record.Id,
            Fqdn = record.Fqdn,
            Domain = record.Domain,
            SiteName = record.SiteName,
            OsVersion = record.OsVersion,
            IsReadOnly = record.IsReadOnly,
            IsGlobalCatalog = record.IsGlobalCatalog,
            Tags = tags,
            LastDeployment = lastDeployment,
        };
}

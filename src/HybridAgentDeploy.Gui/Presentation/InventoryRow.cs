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

    /// <summary>
    /// The OS with its <c>Windows Server</c> prefix removed — <c>2025 Datacenter</c>.
    /// </summary>
    /// <remarks>
    /// Every row in a domain controller inventory begins with the same two words, so those two
    /// words are the one part of the string carrying no information while costing the most
    /// width. Dropping them from the display let the column shrink far enough to fit a domain
    /// column beside the FQDN without truncating anything that matters. The full value is still
    /// in <see cref="OsVersion"/> and is shown as the cell's tooltip.
    /// </remarks>
    public string? OsShortName
    {
        get
        {
            const string Prefix = "Windows Server ";

            if (string.IsNullOrWhiteSpace(OsVersion))
            {
                return OsVersion;
            }

            var trimmed = OsVersion.Trim();

            return trimmed.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase)
                ? trimmed[Prefix.Length..]
                : trimmed;
        }
    }

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

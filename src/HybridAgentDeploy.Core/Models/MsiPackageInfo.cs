namespace HybridAgentDeploy.Core.Models;

/// <summary>
/// Identity of a selected MSI, read from its <c>Property</c> table plus the file itself
/// (PRD 5.5, 8.3).
/// </summary>
public sealed class MsiPackageInfo
{
    public required string FilePath { get; init; }
    public required string FileName { get; init; }
    public required long FileSizeBytes { get; init; }

    /// <summary>
    /// Verified against the staged copy on each target before msiexec runs (SEC6). A file
    /// that changed in transit must never be installed on a domain controller.
    /// </summary>
    public required string Sha256 { get; init; }

    public string? ProductName { get; init; }
    public required string ProductVersion { get; init; }
    public string? ProductCode { get; init; }
    public string? UpgradeCode { get; init; }

    /// <summary>
    /// False when <see cref="ProductName"/> does not look like a Quest Change Auditor agent
    /// package. The operator is warned prominently but may proceed: PRD 5.5 is explicit that
    /// the tool must not hard-block on a name string that may change between releases.
    /// PRD-OPEN-Q: Q6 — the utility is named for the Hybrid Audit Agent while the MSI ships
    /// as "Quest Change Auditor Agent (x64).msi"; history records the MSI's actual ProductName.
    /// </summary>
    public bool LooksLikeExpectedProduct { get; init; }
}

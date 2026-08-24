namespace HybridAgentDeploy.Core.Models;

/// <summary>
/// A row of the <c>dc_last_deployment</c> view (PRD 6.1): the last known deployment state
/// of one domain controller.
/// </summary>
/// <remarks>
/// Deliberately a view rather than denormalised columns, so the GUI history grid and the
/// CLI <c>history</c> command consume one definition and cannot drift apart.
/// </remarks>
public sealed class DcLastDeployment
{
    public required long DcId { get; init; }
    public required string Fqdn { get; init; }
    public required string MsiProductVersion { get; init; }
    public required string OrgId { get; init; }
    public string? CompletedUtc { get; init; }
    public required DeploymentOutcome Outcome { get; init; }
    public int? ExitCode { get; init; }
    public string? MsiLogPath { get; init; }
}

namespace HybridAgentDeploy.Core.Models;

/// <summary>
/// The outcome of one target within one run (PRD 6).
/// </summary>
public sealed class DeploymentResultRecord
{
    public long Id { get; set; }

    public required long RunId { get; init; }
    public required long DcId { get; init; }

    public string? StartedUtc { get; set; }
    public string? CompletedUtc { get; set; }

    public DeploymentOutcome Outcome { get; set; } = DeploymentOutcome.Skipped;

    /// <summary>Which step of the PRD 5.4 sequence the target reached or failed in.</summary>
    public DeploymentStage? Stage { get; set; }

    /// <summary>The raw msiexec exit code, including codes not in the PRD 10.1 table.</summary>
    public int? ExitCode { get; set; }

    /// <summary>
    /// 1 for everything except a 1618 retry sequence, which may reach 3 (PRD 10.1, R7.6).
    /// Nothing else is ever retried automatically.
    /// </summary>
    public int AttemptCount { get; set; } = 1;

    public ErrorCategory? ErrorCategory { get; set; }

    /// <summary>
    /// Operator-facing detail. PRD 10.4 requires the stage, the DC, and the next
    /// diagnostic step — "Deployment failed" is not acceptable.
    /// </summary>
    public string? ErrorDetail { get; set; }

    /// <summary>Local path of the retrieved msiexec verbose log, if it was retrieved.</summary>
    public string? MsiLogPath { get; set; }

    /// <summary>
    /// The staging directory created on the target, recorded <em>before</em> it is created
    /// so an interrupted run leaves no orphan the tool cannot subsequently identify (NFR7).
    /// Schema addition beyond the PRD 6 DDL; see Migrations/001_initial_schema.sql.
    /// </summary>
    public string? StagingPath { get; set; }

    /// <summary>Cleared until cleanup has confirmed the staging directory is gone (SEC8).</summary>
    public bool StagingCleaned { get; set; }
}

namespace HybridAgentDeploy.Core.Models;

/// <summary>
/// One deployment run: a single operator action against a set of targets (PRD 6).
/// </summary>
/// <remarks>
/// The MSI identity fields are denormalised onto the run on purpose. The record must stay
/// meaningful after the MSI file has been moved or deleted, because its whole job is to
/// answer "what was deployed where, when, and at what version" (PRD G2) months later.
/// </remarks>
public sealed class DeploymentRunRecord
{
    public long Id { get; set; }

    public required Guid RunGuid { get; init; }
    public string StartedUtc { get; set; } = UtcTimestamp.Now();
    public string? CompletedUtc { get; set; }

    /// <summary>DOMAIN\user. Never a password — PRD 12.3, SEC1.</summary>
    public required string OperatorAccount { get; init; }

    public required string MsiPath { get; init; }
    public required string MsiFileName { get; init; }
    public required string MsiSha256 { get; init; }
    public required string MsiProductVersion { get; init; }
    public string? MsiProductName { get; init; }
    public string? MsiProductCode { get; init; }

    public required string OrgId { get; init; }

    /// <summary>The SG=1 switch. PRD-OPEN-Q: Q1 — checkbox defaulting to on until PM confirms.</summary>
    public bool CloudMode { get; init; } = true;

    /// <summary>Already clamped to the ceiling in <see cref="DeploymentLimits"/> before storage.</summary>
    public required int MaxParallel { get; init; }

    public required int TargetCount { get; init; }
    public int SuccessCount { get; set; }
    public int FailureCount { get; set; }

    /// <summary>Set when the circuit breaker halted the run (PRD R7.3).</summary>
    public bool WasHalted { get; set; }

    public string? HaltReason { get; set; }

    public required string LogDirectory { get; init; }
}

namespace HybridAgentDeploy.Core.Models;

/// <summary>Per-target outcome. PRD 10.2 — this list is closed.</summary>
public enum DeploymentOutcome
{
    Success,
    SuccessRebootRequired,
    Failure,
    Timeout,
    Cancelled,
    Skipped,
}

/// <summary>
/// Why a target failed. PRD 10.3. Only <see cref="Authentication"/>,
/// <see cref="Connectivity"/> and <see cref="Staging"/> trip the circuit breaker (R7.3);
/// those three normally indicate an operator-side problem affecting every target, whereas
/// the rest are genuinely per-host conditions.
/// </summary>
public enum ErrorCategory
{
    Authentication,
    Connectivity,
    Staging,
    InstallFailure,
    Contended,
    Policy,
    AlreadyInstalled,
    Timeout,
    Internal,
}

/// <summary>Which step of the PRD 5.4 sequence a target was in. Order is significant.</summary>
public enum DeploymentStage
{
    Preflight,
    Stage,
    Execute,
    Retrieve,
    Cleanup,
}

/// <summary>How a domain controller entered the inventory.</summary>
public enum DiscoverySource
{
    AdEnumeration,
    FileImport,
    Manual,
}

/// <summary>
/// Categories that halt a run when three consecutive operations fail with them (PRD R7.3).
/// </summary>
public static class ErrorCategoryExtensions
{
    public static bool TripsCircuitBreaker(this ErrorCategory category) => category switch
    {
        ErrorCategory.Authentication => true,
        ErrorCategory.Connectivity => true,
        ErrorCategory.Staging => true,
        _ => false,
    };

    /// <summary>True for the two outcomes PRD 10.1 treats as successful installs.</summary>
    public static bool IsSuccess(this DeploymentOutcome outcome) =>
        outcome is DeploymentOutcome.Success or DeploymentOutcome.SuccessRebootRequired;
}

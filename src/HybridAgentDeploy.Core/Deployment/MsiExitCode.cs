using HybridAgentDeploy.Core.Models;

namespace HybridAgentDeploy.Core.Deployment;

/// <summary>
/// How one msiexec exit code is to be treated.
/// </summary>
/// <param name="Outcome">The recorded outcome (PRD 10.2).</param>
/// <param name="ErrorCategory">Null when the install succeeded.</param>
/// <param name="IsRetryable">
/// True only for 1618. PRD R7.6: nothing else is ever retried automatically, because a
/// silent retry of an MSI that failed for an unknown reason against a domain controller is
/// not acceptable behaviour.
/// </param>
/// <param name="RequiresProminentWarning">
/// True for 1641. A reboot initiated on a domain controller is significant and must not be
/// buried in a success count.
/// </param>
/// <param name="Description">
/// Operator-facing, per PRD 10.4: says what happened and what to do next.
/// </param>
public sealed record MsiExitCodeMapping(
    DeploymentOutcome Outcome,
    ErrorCategory? ErrorCategory,
    bool IsRetryable,
    bool RequiresProminentWarning,
    string Description)
{
    public bool IsSuccess => Outcome.IsSuccess();
}

/// <summary>
/// The msiexec exit-code table of PRD 10.1.
/// </summary>
/// <remarks>
/// The Hybrid Audit Agent does not require a reboot to function, so 3010 and 1641 generally
/// indicate a reboot already pending on the target from an unrelated cause rather than
/// anything this deployment did. They are reported accurately without alarming the operator
/// about this installation.
/// </remarks>
public static class MsiExitCode
{
    public const int Success = 0;
    public const int UserCancelled = 1602;
    public const int FatalError = 1603;
    public const int ProductNotInstalled = 1605;
    public const int AnotherInstallationInProgress = 1618;
    public const int PackageCouldNotBeOpened = 1619;
    public const int PackageInvalid = 1620;
    public const int BlockedBySystemPolicy = 1625;
    public const int AnotherVersionInstalled = 1638;
    public const int SuccessRebootInitiated = 1641;
    public const int SuccessRebootRequired = 3010;

    /// <summary>
    /// Maps an exit code to its treatment. Codes outside the PRD 10.1 table are recorded as
    /// <see cref="ErrorCategory.InstallFailure"/> with the raw code preserved, never
    /// silently normalised into a neighbouring case.
    /// </summary>
    public static MsiExitCodeMapping Map(int exitCode, string targetFqdn) => exitCode switch
    {
        Success => new(
            DeploymentOutcome.Success,
            null,
            IsRetryable: false,
            RequiresProminentWarning: false,
            $"The agent installed successfully on {targetFqdn}."),

        SuccessRebootRequired => new(
            DeploymentOutcome.SuccessRebootRequired,
            null,
            IsRetryable: false,
            RequiresProminentWarning: false,
            $"The agent installed successfully on {targetFqdn}; the target reports a reboot is " +
            "pending. This agent does not require a reboot to function, so the pending reboot " +
            "is almost certainly from an unrelated change already made on that host."),

        SuccessRebootInitiated => new(
            DeploymentOutcome.SuccessRebootRequired,
            null,
            IsRetryable: false,
            // A domain controller restarting is significant regardless of what caused it.
            RequiresProminentWarning: true,
            $"The agent installed successfully on {targetFqdn}, but msiexec reported that a " +
            "REBOOT WAS INITIATED (1641). This agent does not require a reboot, so confirm " +
            "immediately whether this domain controller is restarting and whether the remaining " +
            "controllers can carry the load."),

        AnotherInstallationInProgress => new(
            DeploymentOutcome.Failure,
            Models.ErrorCategory.Contended,
            // The single exception to R7.6, because the condition is transient by definition.
            IsRetryable: true,
            RequiresProminentWarning: false,
            $"Another installation was already in progress on {targetFqdn} (1618). This is " +
            "transient — usually Windows Update or a scheduled maintenance task holding the " +
            "Windows Installer service."),

        FatalError => new(
            DeploymentOutcome.Failure,
            Models.ErrorCategory.InstallFailure,
            IsRetryable: false,
            RequiresProminentWarning: false,
            $"The installation failed on {targetFqdn} with a fatal error (1603). The cause is in " +
            "the retrieved verbose log — search it for 'Return value 3' and read the action " +
            "immediately above."),

        UserCancelled => new(
            DeploymentOutcome.Failure,
            Models.ErrorCategory.InstallFailure,
            IsRetryable: false,
            RequiresProminentWarning: false,
            // /qn is always passed, so this should be unreachable. PRD 10.1 says record it
            // verbatim rather than suppressing it if it does occur.
            $"msiexec on {targetFqdn} reported the installation was cancelled (1602). This is " +
            "unexpected for an unattended install and should be reported with the verbose log."),

        ProductNotInstalled => new(
            DeploymentOutcome.Failure,
            Models.ErrorCategory.InstallFailure,
            IsRetryable: false,
            RequiresProminentWarning: false,
            $"msiexec on {targetFqdn} reported the product is not installed (1605)."),

        PackageCouldNotBeOpened => new(
            DeploymentOutcome.Failure,
            Models.ErrorCategory.Staging,
            IsRetryable: false,
            RequiresProminentWarning: false,
            $"The staged package could not be opened on {targetFqdn} (1619) despite its hash " +
            "verifying after staging. Investigate: this suggests the file was altered or removed " +
            "on the target between verification and execution."),

        PackageInvalid => new(
            DeploymentOutcome.Failure,
            Models.ErrorCategory.Staging,
            IsRetryable: false,
            RequiresProminentWarning: false,
            $"The staged package was rejected as invalid on {targetFqdn} (1620). Confirm the " +
            "source MSI is a complete, uncorrupted package."),

        BlockedBySystemPolicy => new(
            DeploymentOutcome.Failure,
            Models.ErrorCategory.Policy,
            IsRetryable: false,
            RequiresProminentWarning: false,
            $"The installation was blocked by system policy on {targetFqdn} (1625). A software " +
            "restriction policy or AppLocker rule on that domain controller is preventing the " +
            "package from running; this must be resolved on the target, not in this utility."),

        AnotherVersionInstalled => new(
            DeploymentOutcome.Failure,
            Models.ErrorCategory.AlreadyInstalled,
            IsRetryable: false,
            RequiresProminentWarning: false,
            $"Another version of this product is already installed on {targetFqdn} (1638). This " +
            "is an upgrade-path problem rather than a broken deployment: check the installed " +
            "agent version before deciding whether to remove it or deploy a different package."),

        _ => new(
            DeploymentOutcome.Failure,
            Models.ErrorCategory.InstallFailure,
            IsRetryable: false,
            RequiresProminentWarning: false,
            $"The installation failed on {targetFqdn} with exit code {exitCode}, which is not a " +
            "code this utility recognises. The retrieved verbose log is the place to start."),
    };
}

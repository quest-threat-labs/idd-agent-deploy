using HybridAgentDeploy.Core.Deployment;
using HybridAgentDeploy.Core.Models;

namespace HybridAgentDeploy.Core.Tests;

/// <summary>
/// PRD 15.1: exit-code mapping, every row in the PRD 10.1 table.
/// </summary>
public sealed class ExitCodeMappingTests
{
    private const string Dc = "DC01.corp.local";

    [Theory]
    [InlineData(0, DeploymentOutcome.Success, null)]
    [InlineData(1602, DeploymentOutcome.Failure, ErrorCategory.InstallFailure)]
    [InlineData(1603, DeploymentOutcome.Failure, ErrorCategory.InstallFailure)]
    [InlineData(1605, DeploymentOutcome.Failure, ErrorCategory.InstallFailure)]
    [InlineData(1618, DeploymentOutcome.Failure, ErrorCategory.Contended)]
    [InlineData(1619, DeploymentOutcome.Failure, ErrorCategory.Staging)]
    [InlineData(1620, DeploymentOutcome.Failure, ErrorCategory.Staging)]
    [InlineData(1625, DeploymentOutcome.Failure, ErrorCategory.Policy)]
    [InlineData(1638, DeploymentOutcome.Failure, ErrorCategory.AlreadyInstalled)]
    [InlineData(3010, DeploymentOutcome.SuccessRebootRequired, null)]
    [InlineData(1641, DeploymentOutcome.SuccessRebootRequired, null)]
    public void Every_row_of_the_specified_table_maps_as_written(
        int exitCode,
        DeploymentOutcome expectedOutcome,
        ErrorCategory? expectedCategory)
    {
        var mapping = MsiExitCode.Map(exitCode, Dc);

        Assert.Equal(expectedOutcome, mapping.Outcome);
        Assert.Equal(expectedCategory, mapping.ErrorCategory);
    }

    /// <summary>
    /// The agent does not require a reboot, so these indicate a reboot pending from an
    /// unrelated cause. Both are successes (PRD 10.1).
    /// </summary>
    [Theory]
    [InlineData(0)]
    [InlineData(3010)]
    [InlineData(1641)]
    public void The_three_success_codes_are_treated_as_successes(int exitCode)
    {
        Assert.True(MsiExitCode.Map(exitCode, Dc).IsSuccess);
    }

    /// <summary>
    /// PRD 10.1: 1641 must be flagged loudly. A reboot initiated on a domain controller is
    /// significant, and burying it in a success count would be the wrong report.
    /// </summary>
    [Fact]
    public void A_reboot_initiated_on_a_domain_controller_is_flagged_prominently()
    {
        var mapping = MsiExitCode.Map(1641, Dc);

        Assert.True(mapping.IsSuccess);
        Assert.True(mapping.RequiresProminentWarning);
        Assert.Contains("REBOOT WAS INITIATED", mapping.Description, StringComparison.Ordinal);
    }

    [Fact]
    public void A_pending_reboot_is_a_success_without_alarming_the_operator()
    {
        var mapping = MsiExitCode.Map(3010, Dc);

        Assert.True(mapping.IsSuccess);
        Assert.False(mapping.RequiresProminentWarning);
        Assert.Contains("does not require a reboot", mapping.Description, StringComparison.Ordinal);
    }

    /// <summary>
    /// PRD 10.1: 1638 must be surfaced distinctly rather than lumped in with generic
    /// failures — it is an upgrade-path problem, not a broken deployment.
    /// </summary>
    [Fact]
    public void An_existing_version_is_categorised_distinctly_from_a_generic_failure()
    {
        var mapping = MsiExitCode.Map(1638, Dc);

        Assert.Equal(ErrorCategory.AlreadyInstalled, mapping.ErrorCategory);
        Assert.NotEqual(ErrorCategory.InstallFailure, mapping.ErrorCategory);
        Assert.False(mapping.ErrorCategory!.Value.TripsCircuitBreaker());
    }

    /// <summary>PRD 10.1 and R7.6: 1618 is the only automatically retryable code.</summary>
    [Theory]
    [InlineData(0, false)]
    [InlineData(1602, false)]
    [InlineData(1603, false)]
    [InlineData(1605, false)]
    [InlineData(1618, true)]
    [InlineData(1619, false)]
    [InlineData(1620, false)]
    [InlineData(1625, false)]
    [InlineData(1638, false)]
    [InlineData(1641, false)]
    [InlineData(3010, false)]
    [InlineData(9999, false)]
    public void Only_a_contended_installer_is_retryable(int exitCode, bool expected)
    {
        Assert.Equal(expected, MsiExitCode.Map(exitCode, Dc).IsRetryable);
    }

    /// <summary>
    /// PRD 10.1: an unmapped code records the raw value rather than being normalised into a
    /// neighbouring case.
    /// </summary>
    [Fact]
    public void An_unmapped_code_is_recorded_verbatim()
    {
        var mapping = MsiExitCode.Map(1234, Dc);

        Assert.Equal(DeploymentOutcome.Failure, mapping.Outcome);
        Assert.Equal(ErrorCategory.InstallFailure, mapping.ErrorCategory);
        Assert.Contains("1234", mapping.Description, StringComparison.Ordinal);
    }

    /// <summary>
    /// PRD 10.4: every failure surfaced to an operator states which DC and the next
    /// diagnostic step. "Deployment failed" is not acceptable.
    /// </summary>
    [Theory]
    [InlineData(1602)]
    [InlineData(1603)]
    [InlineData(1605)]
    [InlineData(1618)]
    [InlineData(1619)]
    [InlineData(1620)]
    [InlineData(1625)]
    [InlineData(1638)]
    [InlineData(4321)]
    public void Every_failure_description_names_the_domain_controller(int exitCode)
    {
        var mapping = MsiExitCode.Map(exitCode, Dc);

        Assert.Contains(Dc, mapping.Description, StringComparison.Ordinal);
        Assert.True(mapping.Description.Length > 40, "The description must give the operator a next step.");
    }

    /// <summary>
    /// PRD R7.3: only Authentication, Connectivity and Staging trip the breaker. An install
    /// failure is a genuinely per-host condition.
    /// </summary>
    [Theory]
    [InlineData(ErrorCategory.Authentication, true)]
    [InlineData(ErrorCategory.Connectivity, true)]
    [InlineData(ErrorCategory.Staging, true)]
    [InlineData(ErrorCategory.InstallFailure, false)]
    [InlineData(ErrorCategory.Contended, false)]
    [InlineData(ErrorCategory.Policy, false)]
    [InlineData(ErrorCategory.AlreadyInstalled, false)]
    [InlineData(ErrorCategory.Timeout, false)]
    [InlineData(ErrorCategory.Internal, false)]
    public void Only_three_categories_trip_the_circuit_breaker(ErrorCategory category, bool expected)
    {
        Assert.Equal(expected, category.TripsCircuitBreaker());
    }
}

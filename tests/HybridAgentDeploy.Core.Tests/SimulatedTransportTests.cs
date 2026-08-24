using HybridAgentDeploy.Core.Deployment;
using HybridAgentDeploy.Core.Models;
using HybridAgentDeploy.Core.Testing;

namespace HybridAgentDeploy.Core.Tests;

/// <summary>
/// Phase 1 acceptance: <c>SimulatedTransport</c> can be driven to produce every outcome in
/// PRD 10.2.
/// </summary>
/// <remarks>
/// The orchestrator that maps these into outcomes arrives in Phase 2. What is proved here is
/// that the transport can produce every input that mapping will need — that Phase 2 can be
/// built and tested with no domain in sight.
/// </remarks>
public sealed class SimulatedTransportTests
{
    private static readonly CancellationToken Ct = CancellationToken.None;

    private const string Host = "dc01.corp.local";
    private const string StagingDirectory = @"C:\Windows\Temp\HybridAgentDeploy\run";

    /// <summary>
    /// A stand-in command. These tests are about transport behaviour, not command content;
    /// what the orchestrator actually builds is covered by MsiCommandBuilderTests.
    /// </summary>
    private static readonly RemoteCommand TestCommand = new(
        "msiexec",
        ["/i", @"C:\Windows\Temp\HybridAgentDeploy\run\agent.msi", "/qn"]);

    [Fact]
    public async Task An_unconfigured_host_completes_the_whole_sequence_successfully()
    {
        var transport = new SimulatedTransport();

        Assert.True((await transport.PreflightAsync(Host, Ct)).Succeeded);

        var staged = await transport.StageFileAsync(Host, @"C:\packages\agent.msi", StagingDirectory, Ct);
        Assert.True(staged.Succeeded);
        Assert.Equal(Path.Combine(StagingDirectory, "agent.msi"), staged.StagedPath);

        var executed = await transport.ExecuteAsync(Host, TestCommand, TimeSpan.FromMinutes(15), Ct);
        Assert.True(executed.Launched);
        Assert.Equal(0, executed.ExitCode);

        Assert.True((await transport.RetrieveFileAsync(Host, "install.log", "local.log", Ct)).Succeeded);
        await transport.CleanupAsync(Host, StagingDirectory, Ct);

        Assert.Equal(
            [
                DeploymentStage.Preflight,
                DeploymentStage.Stage,
                DeploymentStage.Execute,
                DeploymentStage.Retrieve,
                DeploymentStage.Cleanup,
            ],
            transport.Calls.Select(c => c.Stage));
    }

    /// <summary>
    /// PRD R7.3: the three categories that trip the circuit breaker must all be injectable,
    /// or the breaker cannot be tested in Phase 2.
    /// </summary>
    [Theory]
    [InlineData(ErrorCategory.Authentication)]
    [InlineData(ErrorCategory.Connectivity)]
    [InlineData(ErrorCategory.Staging)]
    public async Task A_preflight_failure_can_be_injected_with_any_category(ErrorCategory category)
    {
        var transport = new SimulatedTransport()
            .ConfigureHost(Host, h => h.PreflightFailure = new SimulatedFailure(
                category, $"{Host} failed pre-flight."));

        var result = await transport.PreflightAsync(Host, Ct);

        Assert.False(result.Succeeded);
        Assert.Equal(category, result.ErrorCategory);
    }

    [Fact]
    public async Task A_staging_failure_can_be_injected()
    {
        var transport = new SimulatedTransport()
            .ConfigureHost(Host, h => h.StagingFailure = new SimulatedFailure(
                ErrorCategory.Staging,
                $@"Staging failed on {Host}: access denied writing to \\{Host}\C$\Windows\Temp — " +
                "confirm the running account holds local administrator rights on this DC"));

        var result = await transport.StageFileAsync(Host, "agent.msi", StagingDirectory, Ct);

        Assert.False(result.Succeeded);
        Assert.Equal(ErrorCategory.Staging, result.ErrorCategory);
        Assert.Contains("local administrator rights", result.ErrorDetail!, StringComparison.Ordinal);
    }

    /// <summary>
    /// Every exit code in the PRD 10.1 table must be injectable so Phase 2 can assert the
    /// mapping row by row.
    /// </summary>
    [Theory]
    [InlineData(0)]
    [InlineData(1602)]
    [InlineData(1603)]
    [InlineData(1605)]
    [InlineData(1618)]
    [InlineData(1619)]
    [InlineData(1620)]
    [InlineData(1625)]
    [InlineData(1638)]
    [InlineData(1641)]
    [InlineData(3010)]
    [InlineData(1234)]
    public async Task Any_msiexec_exit_code_can_be_injected(int exitCode)
    {
        var transport = new SimulatedTransport()
            .ConfigureHost(Host, h => h.ReturnsExitCodes(exitCode));

        var result = await transport.ExecuteAsync(Host, TestCommand, TimeSpan.FromMinutes(15), Ct);

        Assert.True(result.Launched);
        Assert.Equal(exitCode, result.ExitCode);
        Assert.False(result.TimedOut);
    }

    /// <summary>
    /// PRD 10.1: 1618 retries up to three attempts. A queued sequence is what makes that
    /// testable end to end in Phase 2 rather than only at the unit level.
    /// </summary>
    [Fact]
    public async Task Exit_codes_can_be_queued_so_a_retry_sequence_is_reproducible()
    {
        var transport = new SimulatedTransport()
            .ConfigureHost(Host, h => h.ReturnsExitCodes(1618, 1618, 0));

        var codes = new List<int?>();
        for (var attempt = 0; attempt < 3; attempt++)
        {
            codes.Add((await transport.ExecuteAsync(Host, TestCommand, TimeSpan.FromMinutes(15), Ct)).ExitCode);
        }

        Assert.Equal([1618, 1618, 0], codes);
    }

    [Fact]
    public async Task The_last_queued_exit_code_repeats_once_the_queue_is_exhausted()
    {
        var transport = new SimulatedTransport()
            .ConfigureHost(Host, h => h.ReturnsExitCodes(1618));

        for (var attempt = 0; attempt < 4; attempt++)
        {
            var result = await transport.ExecuteAsync(Host, TestCommand, TimeSpan.FromMinutes(15), Ct);
            Assert.Equal(1618, result.ExitCode);
        }
    }

    /// <summary>PRD R7.5: the timeout path, without waiting fifteen minutes for it.</summary>
    [Fact]
    public async Task A_timeout_can_be_injected()
    {
        var transport = new SimulatedTransport()
            .ConfigureHost(Host, h => h.ExecutionTimesOut = true);

        var result = await transport.ExecuteAsync(Host, TestCommand, TimeSpan.FromMinutes(15), Ct);

        Assert.True(result.TimedOut);
        Assert.Null(result.ExitCode);
        Assert.Equal(ErrorCategory.Timeout, result.ErrorCategory);
    }

    [Fact]
    public async Task A_connection_failure_at_execution_time_can_be_injected()
    {
        var transport = new SimulatedTransport()
            .ConfigureHost(Host, h => h.ExecutionLaunchFailure = new SimulatedFailure(
                ErrorCategory.Connectivity,
                $"A PowerShell session to {Host} could not be opened on TCP 5985."));

        var result = await transport.ExecuteAsync(Host, TestCommand, TimeSpan.FromMinutes(15), Ct);

        Assert.False(result.Launched);
        Assert.Null(result.ExitCode);
        Assert.Equal(ErrorCategory.Connectivity, result.ErrorCategory);
    }

    [Fact]
    public async Task A_cancelled_operation_surfaces_as_a_cancellation()
    {
        using var cts = new CancellationTokenSource();
        var transport = new SimulatedTransport()
            .ConfigureHost(Host, h => h.ExecutionDelay = TimeSpan.FromSeconds(30));

        var execution = transport.ExecuteAsync(Host, TestCommand, TimeSpan.FromMinutes(15), cts.Token);
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => execution);
    }

    /// <summary>
    /// PRD 5.4 step 5: a cleanup failure is a warning, never a deployment failure. The
    /// transport must be able to produce one so Phase 2 can prove the orchestrator treats it
    /// that way.
    /// </summary>
    [Fact]
    public async Task A_cleanup_failure_can_be_injected()
    {
        var transport = new SimulatedTransport()
            .ConfigureHost(Host, h => h.CleanupThrows = true);

        await Assert.ThrowsAsync<IOException>(
            () => transport.CleanupAsync(Host, StagingDirectory, Ct));
    }

    [Fact]
    public async Task Default_behaviour_applies_to_hosts_with_no_explicit_configuration()
    {
        var transport = new SimulatedTransport()
            .ConfigureDefault(h => h.ReturnsExitCodes(1603))
            .ConfigureHost("dc02.corp.local", h => h.ReturnsExitCodes(0));

        var unconfigured = await transport.ExecuteAsync("dc50.corp.local", TestCommand, TimeSpan.FromMinutes(15), Ct);
        var configured = await transport.ExecuteAsync("dc02.corp.local", TestCommand, TimeSpan.FromMinutes(15), Ct);

        Assert.Equal(1603, unconfigured.ExitCode);
        Assert.Equal(0, configured.ExitCode);
    }

    /// <summary>
    /// The instrumentation Phase 2 depends on: a sequence counts as in flight from pre-flight
    /// until cleanup, which is the span PRD R7.1 requires a concurrency slot to cover.
    /// </summary>
    [Fact]
    public async Task Concurrency_is_observed_across_the_whole_sequence_not_just_execution()
    {
        var transport = new SimulatedTransport()
            .ConfigureDefault(h => h.ExecutionDelay = TimeSpan.FromMilliseconds(50));

        async Task RunSequenceAsync(string host)
        {
            await transport.PreflightAsync(host, Ct);
            await transport.StageFileAsync(host, "agent.msi", StagingDirectory, Ct);
            await transport.ExecuteAsync(host, TestCommand, TimeSpan.FromMinutes(15), Ct);
            await transport.RetrieveFileAsync(host, "install.log", "local.log", Ct);
            await transport.CleanupAsync(host, StagingDirectory, Ct);
        }

        await Task.WhenAll(
            RunSequenceAsync("dc01.corp.local"),
            RunSequenceAsync("dc02.corp.local"),
            RunSequenceAsync("dc03.corp.local"));

        Assert.Equal(3, transport.ConcurrencyObserved);
    }

    /// <summary>
    /// A host that fails pre-flight attempts nothing further, so its slot must be released
    /// at pre-flight rather than being held until a cleanup that never comes.
    /// </summary>
    [Fact]
    public async Task A_host_that_fails_preflight_releases_its_slot_immediately()
    {
        var transport = new SimulatedTransport()
            .ConfigureDefault(h => h.PreflightFailure = new SimulatedFailure(
                ErrorCategory.Connectivity, "unreachable"));

        for (var i = 0; i < 10; i++)
        {
            await transport.PreflightAsync($"dc{i:00}.corp.local", Ct);
        }

        // Sequential failures never overlap, so the peak must stay at one. A transport that
        // leaked slots would report ten.
        Assert.Equal(1, transport.ConcurrencyObserved);
    }
}

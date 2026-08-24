using HybridAgentDeploy.Core.Deployment;
using HybridAgentDeploy.Core.Models;
using HybridAgentDeploy.Core.Testing;

namespace HybridAgentDeploy.Core.Tests;

/// <summary>
/// The deployment orchestrator, driven entirely through <see cref="SimulatedTransport"/>.
/// PRD 15.1 concurrency, site guard, circuit breaker, retry, and cancellation requirements.
/// </summary>
public sealed class OrchestratorTests
{
    private static readonly CancellationToken Ct = CancellationToken.None;

    [Fact]
    public async Task A_successful_run_records_every_target_and_walks_the_specified_sequence()
    {
        await using var harness = await OrchestratorHarness.CreateAsync();
        var targets = await harness.SeedTargetsAsync("London", 3);

        var summary = await harness.Orchestrator.DeployAsync(harness.Request(targets), null, Ct);

        Assert.Equal(3, summary.SuccessCount);
        Assert.Equal(0, summary.FailureCount);
        Assert.False(summary.WasHalted);
        Assert.False(summary.WasCancelled);

        // PRD 5.4: the sequence is deliberate and is not reordered.
        var forFirst = harness.Transport.Calls
            .Where(c => c.TargetHost == "dc01.corp.local")
            .Select(c => c.Stage);

        Assert.Equal(
            [
                DeploymentStage.Preflight,
                DeploymentStage.Stage,
                DeploymentStage.Execute,
                DeploymentStage.Retrieve,
                DeploymentStage.Cleanup,
            ],
            forFirst);
    }

    /// <summary>
    /// PRD 15.1: assert never more than 5 in flight, and that slots release only after the
    /// full sequence.
    /// </summary>
    [Fact]
    public async Task Concurrency_never_exceeds_the_ceiling_of_five()
    {
        await using var harness = await OrchestratorHarness.CreateAsync();
        var targets = await harness.SeedTargetsAsync("London", 20);

        harness.Transport.ConfigureDefault(h =>
        {
            h.PreflightDelay = TimeSpan.FromMilliseconds(5);
            h.ExecutionDelay = TimeSpan.FromMilliseconds(20);
            h.CleanupDelay = TimeSpan.FromMilliseconds(5);
        });

        // A 20-DC site permits 10 concurrent under R7.2, so the global ceiling is the binding
        // constraint here and the site guard cannot mask a failure to enforce it.
        var request = harness.Request(
            targets,
            maxParallel: DeploymentLimits.MaxConcurrencyCeiling,
            activeDcsPerSite: new Dictionary<string, int> { ["London"] = 20 });

        await harness.Orchestrator.DeployAsync(request, null, Ct);

        Assert.True(
            harness.Transport.ConcurrencyObserved <= DeploymentLimits.MaxConcurrencyCeiling,
            $"Observed {harness.Transport.ConcurrencyObserved} concurrent operations; the ceiling is " +
            $"{DeploymentLimits.MaxConcurrencyCeiling}.");

        // Confirm the test is actually exercising concurrency rather than passing by accident.
        Assert.True(harness.Transport.ConcurrencyObserved > 1);
    }

    /// <summary>
    /// R7.1: a request above the ceiling is clamped silently rather than rejected, and no
    /// configuration path can raise it.
    /// </summary>
    [Fact]
    public async Task A_request_above_the_ceiling_is_clamped_rather_than_refused()
    {
        await using var harness = await OrchestratorHarness.CreateAsync();
        var targets = await harness.SeedTargetsAsync("London", 12);

        harness.Transport.ConfigureDefault(h => h.ExecutionDelay = TimeSpan.FromMilliseconds(20));

        var request = harness.Request(
            targets,
            maxParallel: 50,
            activeDcsPerSite: new Dictionary<string, int> { ["London"] = 12 });

        Assert.Equal(DeploymentLimits.MaxConcurrencyCeiling, request.MaxParallel);
        Assert.True(request.ParallelismWasClamped);

        await harness.Orchestrator.DeployAsync(request, null, Ct);

        Assert.True(harness.Transport.ConcurrencyObserved <= DeploymentLimits.MaxConcurrencyCeiling);

        // R7.1 also requires the clamp to be noted in the run log.
        Assert.Contains("clamped", harness.ReadRunLog(), StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// PRD 15.1: a two-DC site never has both in flight (R7.2).
    /// </summary>
    [Fact]
    public async Task A_two_dc_site_is_never_deployed_to_concurrently()
    {
        await using var harness = await OrchestratorHarness.CreateAsync();
        var targets = await harness.SeedTargetsAsync(
            ("dc01.corp.local", "Belfast"),
            ("dc02.corp.local", "Belfast"));

        harness.Transport.ConfigureDefault(h => h.ExecutionDelay = TimeSpan.FromMilliseconds(50));

        var request = harness.Request(
            targets,
            maxParallel: 5,
            activeDcsPerSite: new Dictionary<string, int> { ["Belfast"] = 2 });

        await harness.Orchestrator.DeployAsync(request, null, Ct);

        // Half of two, rounded down, is one.
        Assert.Equal(1, harness.Transport.ConcurrencyObserved);
    }

    [Fact]
    public async Task A_large_site_runs_in_parallel_up_to_half_its_population()
    {
        await using var harness = await OrchestratorHarness.CreateAsync();
        var targets = await harness.SeedTargetsAsync("London", 8);

        harness.Transport.ConfigureDefault(h => h.ExecutionDelay = TimeSpan.FromMilliseconds(40));

        var request = harness.Request(
            targets,
            maxParallel: 5,
            activeDcsPerSite: new Dictionary<string, int> { ["London"] = 8 });

        await harness.Orchestrator.DeployAsync(request, null, Ct);

        // Half of eight is four, which binds below the global ceiling of five.
        Assert.True(
            harness.Transport.ConcurrencyObserved <= 4,
            $"Observed {harness.Transport.ConcurrencyObserved}; the site limit is 4.");
    }

    /// <summary>
    /// The consequence of treating site-less targets as one bucket: an import-only deployment
    /// serialises. Asserted so the behaviour is deliberate and visible rather than discovered
    /// later as a mystery slowdown.
    /// </summary>
    [Fact]
    public async Task Targets_with_no_recorded_site_are_deployed_one_at_a_time()
    {
        await using var harness = await OrchestratorHarness.CreateAsync();
        var targets = await harness.SeedTargetsAsync(site: null, count: 6);

        harness.Transport.ConfigureDefault(h => h.ExecutionDelay = TimeSpan.FromMilliseconds(30));

        await harness.Orchestrator.DeployAsync(harness.Request(targets, maxParallel: 5), null, Ct);

        Assert.Equal(1, harness.Transport.ConcurrencyObserved);

        // The run log must say why, or a serialised run looks like a hang.
        var log = harness.ReadRunLog();
        Assert.Contains("no Active Directory site recorded", log, StringComparison.Ordinal);
        Assert.Contains("one at a time", log, StringComparison.Ordinal);
    }

    /// <summary>
    /// PRD 15.1: the breaker trips on 3 consecutive Authentication failures.
    /// </summary>
    [Fact]
    public async Task Three_consecutive_authentication_failures_halt_the_run()
    {
        await using var harness = await OrchestratorHarness.CreateAsync();
        var targets = await harness.SeedTargetsAsync("London", 20);

        harness.Transport.ConfigureDefault(h => h.PreflightFailure = new SimulatedFailure(
            ErrorCategory.Authentication,
            "Access denied opening a session; the supplied credentials were rejected."));

        // Serialised so "consecutive" is unambiguous and the halt is deterministic.
        var summary = await harness.Orchestrator.DeployAsync(
            harness.Request(targets, maxParallel: 1), null, Ct);

        Assert.True(summary.WasHalted);
        Assert.NotNull(summary.HaltReason);
        Assert.Contains("locked out", summary.HaltReason, StringComparison.OrdinalIgnoreCase);

        // Exactly the three that tripped it were attempted; the rest were never touched.
        Assert.Equal(3, summary.FailureCount);
        Assert.Equal(17, summary.SkippedCount);
        Assert.Equal(17, summary.UnattemptedTargets.Count);
    }

    [Theory]
    [InlineData(ErrorCategory.Authentication)]
    [InlineData(ErrorCategory.Connectivity)]
    [InlineData(ErrorCategory.Staging)]
    public async Task Each_operator_side_category_can_halt_a_run(ErrorCategory category)
    {
        await using var harness = await OrchestratorHarness.CreateAsync();
        var targets = await harness.SeedTargetsAsync("London", 10);

        harness.Transport.ConfigureDefault(h =>
        {
            if (category == ErrorCategory.Staging)
            {
                h.StagingFailure = new SimulatedFailure(category, "Access denied writing to C$.");
            }
            else
            {
                h.PreflightFailure = new SimulatedFailure(category, "The target was unreachable.");
            }
        });

        var summary = await harness.Orchestrator.DeployAsync(
            harness.Request(targets, maxParallel: 1), null, Ct);

        Assert.True(summary.WasHalted);
        Assert.Equal(3, summary.FailureCount);
    }

    /// <summary>
    /// PRD 15.1: the breaker does NOT trip on 3 consecutive InstallFailure results. A non-zero
    /// msiexec exit code is a per-host condition, and R7.4 requires the run to continue.
    /// </summary>
    [Fact]
    public async Task Three_consecutive_install_failures_do_not_halt_the_run()
    {
        await using var harness = await OrchestratorHarness.CreateAsync();
        var targets = await harness.SeedTargetsAsync("London", 6);

        harness.Transport.ConfigureDefault(h => h.ReturnsExitCodes(1603));

        var summary = await harness.Orchestrator.DeployAsync(
            harness.Request(targets, maxParallel: 1), null, Ct);

        Assert.False(summary.WasHalted);
        Assert.Null(summary.HaltReason);

        // R7.4: every target is attempted and the run completes with a summary.
        Assert.Equal(6, summary.FailureCount);
        Assert.Equal(0, summary.SkippedCount);
        Assert.All(summary.Outcomes, o => Assert.Equal(ErrorCategory.InstallFailure, o.ErrorCategory));
    }

    /// <summary>
    /// A success between failures resets the count, so scattered unreachable DCs do not halt
    /// a healthy run — that is what R7.4 asks for.
    /// </summary>
    [Fact]
    public async Task A_success_between_failures_resets_the_breaker()
    {
        await using var harness = await OrchestratorHarness.CreateAsync();
        var targets = await harness.SeedTargetsAsync("London", 6);

        harness.Transport
            .ConfigureHost("dc01.corp.local", h => h.PreflightFailure =
                new SimulatedFailure(ErrorCategory.Connectivity, "unreachable"))
            .ConfigureHost("dc02.corp.local", h => h.PreflightFailure =
                new SimulatedFailure(ErrorCategory.Connectivity, "unreachable"))
            .ConfigureHost("dc04.corp.local", h => h.PreflightFailure =
                new SimulatedFailure(ErrorCategory.Connectivity, "unreachable"))
            .ConfigureHost("dc05.corp.local", h => h.PreflightFailure =
                new SimulatedFailure(ErrorCategory.Connectivity, "unreachable"));

        var summary = await harness.Orchestrator.DeployAsync(
            harness.Request(targets, maxParallel: 1), null, Ct);

        // dc03 succeeded between the pairs, so no three consecutive failures ever occurred.
        Assert.False(summary.WasHalted);
        Assert.Equal(0, summary.SkippedCount);
    }

    /// <summary>
    /// PRD 15.1: exactly 3 attempts, correct backoff, correct final categorisation.
    /// </summary>
    [Fact]
    public async Task A_contended_installer_is_retried_exactly_three_times_then_recorded_as_contended()
    {
        await using var harness = await OrchestratorHarness.CreateAsync();
        var clock = harness.UseFakeClock();
        var targets = await harness.SeedTargetsAsync("London", 1);

        harness.Transport.ConfigureDefault(h => h.ReturnsExitCodes(1618, 1618, 1618));

        var deployment = harness.Orchestrator.DeployAsync(
            harness.Request(targets, maxParallel: 1), null, Ct);

        // Advance past both backoffs (60s then 120s) so the retries proceed.
        await AdvanceUntilCompleteAsync(clock, deployment, TimeSpan.FromSeconds(30), 40);

        var summary = await deployment;
        var outcome = Assert.Single(summary.Outcomes);

        Assert.Equal(DeploymentOrchestrator.MaxAttemptsForContendedInstaller, outcome.AttemptCount);
        Assert.Equal(3, harness.Transport.ExecutedCommands.Count);

        // A 1618 that survived every retry is Contended, not a generic install failure.
        Assert.Equal(DeploymentOutcome.Failure, outcome.Outcome);
        Assert.Equal(ErrorCategory.Contended, outcome.ErrorCategory);
        Assert.Equal(1618, outcome.ExitCode);
    }

    [Fact]
    public async Task A_contended_installer_that_clears_succeeds_without_using_every_attempt()
    {
        await using var harness = await OrchestratorHarness.CreateAsync();
        var clock = harness.UseFakeClock();
        var targets = await harness.SeedTargetsAsync("London", 1);

        harness.Transport.ConfigureDefault(h => h.ReturnsExitCodes(1618, 0));

        var deployment = harness.Orchestrator.DeployAsync(
            harness.Request(targets, maxParallel: 1), null, Ct);

        await AdvanceUntilCompleteAsync(clock, deployment, TimeSpan.FromSeconds(30), 40);

        var summary = await deployment;
        var outcome = Assert.Single(summary.Outcomes);

        Assert.Equal(2, outcome.AttemptCount);
        Assert.Equal(DeploymentOutcome.Success, outcome.Outcome);
        Assert.Equal(0, outcome.ExitCode);
    }

    /// <summary>
    /// R7.6: nothing except 1618 is retried automatically. A silent retry of an MSI that
    /// failed for an unknown reason against a domain controller is not acceptable.
    /// </summary>
    [Theory]
    [InlineData(1603)]
    [InlineData(1625)]
    [InlineData(1638)]
    [InlineData(9999)]
    public async Task No_other_failure_is_ever_retried(int exitCode)
    {
        await using var harness = await OrchestratorHarness.CreateAsync();
        var targets = await harness.SeedTargetsAsync("London", 1);

        harness.Transport.ConfigureDefault(h => h.ReturnsExitCodes(exitCode));

        var summary = await harness.Orchestrator.DeployAsync(
            harness.Request(targets, maxParallel: 1), null, Ct);

        Assert.Equal(1, Assert.Single(summary.Outcomes).AttemptCount);
        Assert.Single(harness.Transport.ExecutedCommands);
    }

    /// <summary>
    /// PRD 8.4: cancel stops starting new targets but never abandons an in-flight msiexec
    /// without attempting cleanup.
    /// </summary>
    [Fact]
    public async Task Cancellation_stops_new_targets_and_lets_in_flight_ones_finish()
    {
        await using var harness = await OrchestratorHarness.CreateAsync();
        var targets = await harness.SeedTargetsAsync("London", 20);

        using var cts = new CancellationTokenSource();
        var started = 0;

        harness.Transport.ConfigureDefault(h => h.ExecutionDelay = TimeSpan.FromMilliseconds(30));

        var progress = new InlineProgress<DeploymentProgress>(p =>
        {
            if (p.State == TargetProgressState.Running &&
                p.Stage == DeploymentStage.Execute &&
                Interlocked.Increment(ref started) == 3)
            {
                cts.Cancel();
            }
        });

        var request = harness.Request(
            targets,
            maxParallel: 2,
            activeDcsPerSite: new Dictionary<string, int> { ["London"] = 20 });

        var summary = await harness.Orchestrator.DeployAsync(request, progress, cts.Token);

        Assert.True(summary.WasCancelled);
        Assert.True(summary.SkippedCount > 0, "Cancellation must leave targets unattempted.");

        // Every target that got as far as staging had its staging directory cleaned up.
        var attempted = summary.Outcomes.Where(o => o.Outcome != DeploymentOutcome.Skipped).ToList();
        Assert.NotEmpty(attempted);

        var cleanupCalls = harness.Transport.Calls.Count(c => c.Stage == DeploymentStage.Cleanup);
        Assert.Equal(attempted.Count, cleanupCalls);

        Assert.Equal(targets.Count, summary.Outcomes.Count);
    }

    /// <summary>
    /// R7.5: on timeout, attempt cleanup, record Timeout, release the slot, and continue with
    /// the remaining targets.
    /// </summary>
    [Fact]
    public async Task A_target_that_times_out_is_recorded_and_the_run_continues()
    {
        await using var harness = await OrchestratorHarness.CreateAsync();
        var targets = await harness.SeedTargetsAsync("London", 3);

        harness.Transport.ConfigureHost("dc02.corp.local", h => h.ExecutionTimesOut = true);

        var summary = await harness.Orchestrator.DeployAsync(
            harness.Request(targets, maxParallel: 1), null, Ct);

        var timedOut = Assert.Single(summary.Outcomes, o => o.Target.Fqdn == "dc02.corp.local");
        Assert.Equal(DeploymentOutcome.Timeout, timedOut.Outcome);
        Assert.Equal(ErrorCategory.Timeout, timedOut.ErrorCategory);

        // R7.4: one target's failure does not stop the run.
        Assert.Equal(2, summary.SuccessCount);
        Assert.Equal(0, summary.SkippedCount);

        // Cleanup was still attempted on the timed-out host (PRD 8.4).
        Assert.Contains(
            harness.Transport.Calls,
            c => c.TargetHost == "dc02.corp.local" && c.Stage == DeploymentStage.Cleanup);
    }

    /// <summary>
    /// A timeout is not an operator-side fault, so it must not contribute to the breaker.
    /// </summary>
    [Fact]
    public async Task Timeouts_do_not_trip_the_circuit_breaker()
    {
        await using var harness = await OrchestratorHarness.CreateAsync();
        var targets = await harness.SeedTargetsAsync("London", 5);

        harness.Transport.ConfigureDefault(h => h.ExecutionTimesOut = true);

        var summary = await harness.Orchestrator.DeployAsync(
            harness.Request(targets, maxParallel: 1), null, Ct);

        Assert.False(summary.WasHalted);
        Assert.Equal(5, summary.Outcomes.Count(o => o.Outcome == DeploymentOutcome.Timeout));
    }

    /// <summary>
    /// SEC6: a file that changed in transit must never be installed on a domain controller.
    /// </summary>
    [Fact]
    public async Task A_staged_hash_mismatch_blocks_the_install_entirely()
    {
        await using var harness = await OrchestratorHarness.CreateAsync();
        var targets = await harness.SeedTargetsAsync("London", 1);

        harness.Transport.ConfigureDefault(h => h.StagedSha256 = new string('F', 64));

        var summary = await harness.Orchestrator.DeployAsync(
            harness.Request(targets, maxParallel: 1), null, Ct);

        var outcome = Assert.Single(summary.Outcomes);
        Assert.Equal(DeploymentOutcome.Failure, outcome.Outcome);
        Assert.Equal(ErrorCategory.Staging, outcome.ErrorCategory);
        Assert.Equal(DeploymentStage.Stage, outcome.Stage);

        // The decisive assertion: msiexec never ran.
        Assert.Empty(harness.Transport.ExecutedCommands);
        Assert.Contains("was NOT attempted", outcome.ErrorDetail!, StringComparison.Ordinal);

        // And the staging directory was still cleaned up.
        Assert.Contains(harness.Transport.Calls, c => c.Stage == DeploymentStage.Cleanup);
    }

    /// <summary>
    /// PRD 5.4 step 5: a cleanup failure is logged as a warning and does not fail the
    /// deployment. The agent is installed either way.
    /// </summary>
    [Fact]
    public async Task A_cleanup_failure_warns_without_failing_the_deployment()
    {
        await using var harness = await OrchestratorHarness.CreateAsync();
        var targets = await harness.SeedTargetsAsync("London", 1);

        harness.Transport.ConfigureDefault(h => h.CleanupThrows = true);

        var summary = await harness.Orchestrator.DeployAsync(
            harness.Request(targets, maxParallel: 1), null, Ct);

        var outcome = Assert.Single(summary.Outcomes);
        Assert.Equal(DeploymentOutcome.Success, outcome.Outcome);
        Assert.NotEmpty(outcome.Warnings);
        Assert.Contains(outcome.Warnings, w => w.Contains("could not be removed", StringComparison.Ordinal));

        // NFR7: the path stays recorded as an orphan so a later run can clean it up.
        var orphans = await harness.Inventory.Deployments.GetOrphanedStagingDirectoriesAsync(Ct);
        Assert.Single(orphans);
    }

    /// <summary>
    /// PRD 5.4 step 4: the log is retrieved whether the install succeeded or failed, because
    /// it is most valuable on failure.
    /// </summary>
    [Fact]
    public async Task The_verbose_log_is_retrieved_even_when_the_install_fails()
    {
        await using var harness = await OrchestratorHarness.CreateAsync();
        var targets = await harness.SeedTargetsAsync("London", 1);

        harness.Transport.ConfigureDefault(h => h.ReturnsExitCodes(1603));

        var summary = await harness.Orchestrator.DeployAsync(
            harness.Request(targets, maxParallel: 1), null, Ct);

        var outcome = Assert.Single(summary.Outcomes);
        Assert.Equal(DeploymentOutcome.Failure, outcome.Outcome);
        Assert.NotNull(outcome.MsiLogPath);
        Assert.Contains(harness.Transport.Calls, c => c.Stage == DeploymentStage.Retrieve);
    }

    /// <summary>
    /// NFR7: the staging path is recorded before the directory is created, so a process
    /// killed mid-run leaves an orphan the tool can still identify.
    /// </summary>
    [Fact]
    public async Task The_staging_path_is_recorded_before_staging_is_attempted()
    {
        await using var harness = await OrchestratorHarness.CreateAsync();
        var targets = await harness.SeedTargetsAsync("London", 1);

        harness.Transport.ConfigureDefault(h => h.StagingFailure = new SimulatedFailure(
            ErrorCategory.Staging, "Access denied."));

        var summary = await harness.Orchestrator.DeployAsync(
            harness.Request(targets, maxParallel: 1), null, Ct);

        // Staging failed, yet the path is on the result row — which is the whole point: had
        // the process died at that moment, the directory would still be identifiable.
        var results = await harness.Inventory.Deployments.GetResultsForRunAsync(summary.RunId, Ct);
        var result = Assert.Single(results);
        Assert.NotNull(result.StagingPath);
    }

    /// <summary>
    /// PRD 5.4 step 2 and SEC7: the staging directory is
    /// <c>C:\Windows\Temp\HybridAgentDeploy\{run-guid}\</c> — under Windows\Temp, which
    /// inherits admin-only ACLs, and named for the full run GUID so a directory left behind
    /// on a domain controller is traceable to the run that created it (NFR7).
    /// </summary>
    [Fact]
    public async Task The_staging_directory_is_named_for_the_full_run_guid_under_windows_temp()
    {
        await using var harness = await OrchestratorHarness.CreateAsync();
        var targets = await harness.SeedTargetsAsync("London", 1);

        var summary = await harness.Orchestrator.DeployAsync(
            harness.Request(targets, maxParallel: 1), null, Ct);

        var results = await harness.Inventory.Deployments.GetResultsForRunAsync(summary.RunId, Ct);
        var stagingPath = Assert.Single(results).StagingPath;

        Assert.Equal(
            $@"C:\Windows\Temp\HybridAgentDeploy\{summary.RunGuid:D}",
            stagingPath);
    }

    /// <summary>SEC10 end to end, through the orchestrator rather than the builder alone.</summary>
    [Fact]
    public async Task An_injected_org_id_reaches_the_transport_inside_a_single_argument()
    {
        await using var harness = await OrchestratorHarness.CreateAsync();
        var targets = await harness.SeedTargetsAsync("London", 1);

        const string Malicious = "\"; Stop-Service NTDS; \"";

        await harness.Orchestrator.DeployAsync(
            harness.Request(targets, maxParallel: 1, orgId: Malicious), null, Ct);

        var command = Assert.Single(harness.Transport.ExecutedCommands);

        Assert.Equal("msiexec", command.Executable);
        Assert.Contains($"INSTALLATION_NAME={Malicious}", command.Arguments);
        Assert.DoesNotContain(
            command.Arguments,
            a => a.Equals("Stop-Service", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Progress_reports_never_show_more_occupied_slots_than_the_ceiling()
    {
        await using var harness = await OrchestratorHarness.CreateAsync();
        var targets = await harness.SeedTargetsAsync("London", 15);

        harness.Transport.ConfigureDefault(h => h.ExecutionDelay = TimeSpan.FromMilliseconds(15));

        var peak = 0;
        var progress = new InlineProgress<DeploymentProgress>(p =>
        {
            var observed = p.OccupiedSlots;
            var current = Volatile.Read(ref peak);
            while (observed > current)
            {
                Interlocked.CompareExchange(ref peak, observed, current);
                current = Volatile.Read(ref peak);
            }
        });

        var request = harness.Request(
            targets,
            maxParallel: 5,
            activeDcsPerSite: new Dictionary<string, int> { ["London"] = 15 });

        await harness.Orchestrator.DeployAsync(request, progress, Ct);

        Assert.True(
            Volatile.Read(ref peak) <= DeploymentLimits.MaxConcurrencyCeiling,
            $"Progress reported {peak} occupied slots against a ceiling of " +
            $"{DeploymentLimits.MaxConcurrencyCeiling}.");
    }

    /// <summary>
    /// Advances a fake clock in steps until the deployment finishes, so a test never depends
    /// on real backoff time elapsing.
    /// </summary>
    private static async Task AdvanceUntilCompleteAsync(
        Microsoft.Extensions.Time.Testing.FakeTimeProvider clock,
        Task deployment,
        TimeSpan step,
        int maxSteps)
    {
        for (var i = 0; i < maxSteps && !deployment.IsCompleted; i++)
        {
            // Yield first so the orchestrator reaches its next timer before the clock moves.
            await Task.Delay(10, CancellationToken.None);
            clock.Advance(step);
        }
    }
}

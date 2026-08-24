using HybridAgentDeploy.Core.Deployment;
using HybridAgentDeploy.Core.Models;
using HybridAgentDeploy.Core.Testing;

namespace HybridAgentDeploy.Core.Tests;

/// <summary>
/// The per-run artefacts of PRD 12. Written for a customer administrator and possibly for a
/// change-control board, so the content requirements of 12.2 are asserted individually.
/// </summary>
public sealed class RunLogTests
{
    private static readonly CancellationToken Ct = CancellationToken.None;

    [Fact]
    public async Task The_run_log_records_everything_the_specification_requires()
    {
        await using var harness = await OrchestratorHarness.CreateAsync();
        var targets = await harness.SeedTargetsAsync(
            ("dc01.corp.local", "London"),
            ("dc02.corp.local", "Belfast"));

        await harness.Orchestrator.DeployAsync(
            harness.Request(targets, maxParallel: 2, timeoutMinutes: 20), null, Ct);

        var log = harness.ReadRunLog();

        // PRD 12.2, item by item.
        Assert.Contains(@"CORP\admin", log, StringComparison.Ordinal);
        Assert.Contains("agent.msi", log, StringComparison.Ordinal);
        Assert.Contains("7.7.34005.0", log, StringComparison.Ordinal);
        Assert.Contains("{0F4BE828-CE3B-4DC1-9C3A-714952CE6A0A}", log, StringComparison.Ordinal);
        Assert.Contains(OrchestratorHarness.StagedSha256, log, StringComparison.Ordinal);
        Assert.Contains("org-abc-123", log, StringComparison.Ordinal);
        Assert.Contains("Cloud mode (SG=1) : on", log, StringComparison.Ordinal);
        Assert.Contains("Max parallel      : 2", log, StringComparison.Ordinal);
        Assert.Contains("20 minutes", log, StringComparison.Ordinal);

        // Resolved target list with sites.
        Assert.Contains("dc01.corp.local  [site: London]", log, StringComparison.Ordinal);
        Assert.Contains("dc02.corp.local  [site: Belfast]", log, StringComparison.Ordinal);

        // Both UTC and local, and the local one labelled (CLAUDE.md).
        Assert.Contains("local time", log, StringComparison.Ordinal);
        Assert.Contains("Z", log, StringComparison.Ordinal);
    }

    /// <summary>
    /// PRD 12.2: the exact command is logged for auditability, because a customer
    /// administrator may need to show a change-control board precisely what ran against their
    /// domain controllers.
    /// </summary>
    [Fact]
    public async Task The_run_log_records_the_exact_command_that_ran()
    {
        await using var harness = await OrchestratorHarness.CreateAsync();
        var targets = await harness.SeedTargetsAsync("London", 1);

        var summary = await harness.Orchestrator.DeployAsync(
            harness.Request(targets, maxParallel: 1), null, Ct);

        var log = harness.ReadRunLog();

        Assert.Contains("msiexec /i ", log, StringComparison.Ordinal);
        Assert.Contains("SG=1", log, StringComparison.Ordinal);
        Assert.Contains("INSTALLATION_NAME=org-abc-123", log, StringComparison.Ordinal);
        Assert.Contains("INSTALLATION_NAME_VALID=1", log, StringComparison.Ordinal);
        Assert.Contains("/qn", log, StringComparison.Ordinal);

        // The summary carries the same template, so GUI and CLI show what the log recorded.
        Assert.Contains("INSTALLATION_NAME=org-abc-123", summary.CommandTemplate, StringComparison.Ordinal);
    }

    /// <summary>
    /// PRD 12.3 and SEC1: passwords and credential material never appear in any log file.
    /// Account names only.
    /// </summary>
    [Fact]
    public async Task No_credential_material_reaches_the_run_log()
    {
        await using var harness = await OrchestratorHarness.CreateAsync();
        var targets = await harness.SeedTargetsAsync("London", 2);

        await harness.Orchestrator.DeployAsync(harness.Request(targets), null, Ct);

        var log = harness.ReadRunLog();
        var csv = harness.ReadResultsCsv();

        foreach (var artefact in new[] { log, csv })
        {
            Assert.DoesNotContain("password", artefact, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("secret", artefact, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("credential", artefact, StringComparison.OrdinalIgnoreCase);
        }

        // The account name itself is permitted and expected.
        Assert.Contains(@"CORP\admin", log, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_results_csv_has_one_row_per_target_with_the_required_columns()
    {
        await using var harness = await OrchestratorHarness.CreateAsync();
        var targets = await harness.SeedTargetsAsync("London", 3);

        harness.Transport.ConfigureHost("dc02.corp.local", h => h.ReturnsExitCodes(1603));

        await harness.Orchestrator.DeployAsync(harness.Request(targets, maxParallel: 1), null, Ct);

        var lines = harness.ReadResultsCsv()
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        Assert.Equal(4, lines.Length); // header plus one row per target

        // PRD 12.1: outcome, exit code, timings, error category.
        Assert.Equal(
            "fqdn,site,outcome,stage,exit_code,error_category,attempt_count," +
            "started_utc,completed_utc,duration_seconds,msi_log_path,error_detail",
            lines[0]);

        Assert.Contains(lines, l => l.StartsWith("dc01.corp.local,London,Success,", StringComparison.Ordinal));
        Assert.Contains(lines, l => l.Contains(",Failure,", StringComparison.Ordinal) &&
                                    l.Contains("1603", StringComparison.Ordinal) &&
                                    l.Contains("InstallFailure", StringComparison.Ordinal));
    }

    /// <summary>
    /// The error detail is free text full of commas and quotes. A CSV that breaks on it is
    /// worthless to an operator triaging a sixty-DC run in a spreadsheet.
    /// </summary>
    [Fact]
    public async Task The_results_csv_escapes_commas_and_quotes_in_the_error_detail()
    {
        await using var harness = await OrchestratorHarness.CreateAsync();
        var targets = await harness.SeedTargetsAsync("London", 1);

        harness.Transport.ConfigureDefault(h => h.PreflightFailure = new SimulatedFailure(
            ErrorCategory.Connectivity,
            "Unreachable on TCP 445, 5985; check the firewall, then confirm \"C$\" is shared."));

        await harness.Orchestrator.DeployAsync(harness.Request(targets, maxParallel: 1), null, Ct);

        var csv = harness.ReadResultsCsv();

        // Quotes are doubled and the field is wrapped, per RFC 4180.
        Assert.Contains("\"\"C$\"\"", csv, StringComparison.Ordinal);

        // The header plus exactly one data row: the embedded commas did not split it.
        var rows = csv.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(2, rows.Length);
    }

    [Fact]
    public async Task A_halted_run_records_the_halt_reason_prominently()
    {
        await using var harness = await OrchestratorHarness.CreateAsync();
        var targets = await harness.SeedTargetsAsync("London", 10);

        harness.Transport.ConfigureDefault(h => h.PreflightFailure = new SimulatedFailure(
            ErrorCategory.Authentication, "The supplied credentials were rejected."));

        await harness.Orchestrator.DeployAsync(harness.Request(targets, maxParallel: 1), null, Ct);

        var log = harness.ReadRunLog();

        Assert.Contains("CIRCUIT BREAKER TRIPPED", log, StringComparison.Ordinal);
        Assert.Contains("RUN HALTED BY CIRCUIT BREAKER", log, StringComparison.Ordinal);
    }

    /// <summary>
    /// PRD 10.1: a reboot initiated on a domain controller must be flagged loudly rather than
    /// counted quietly as a success.
    /// </summary>
    [Fact]
    public async Task A_reboot_initiated_on_a_domain_controller_is_called_out_in_the_summary()
    {
        await using var harness = await OrchestratorHarness.CreateAsync();
        var targets = await harness.SeedTargetsAsync("London", 2);

        harness.Transport.ConfigureHost("dc02.corp.local", h => h.ReturnsExitCodes(1641));

        var summary = await harness.Orchestrator.DeployAsync(
            harness.Request(targets, maxParallel: 1), null, Ct);

        // It still counts as a success (PRD 10.1) ...
        Assert.Equal(2, summary.SuccessCount);

        // ... but is impossible to miss in the log.
        var log = harness.ReadRunLog();
        Assert.Contains("REQUIRES ATTENTION", log, StringComparison.Ordinal);
        Assert.Contains("a reboot was initiated on this domain controller", log, StringComparison.Ordinal);
    }

    /// <summary>
    /// The log is flushed as the run proceeds, not buffered to the end. The run whose log
    /// matters most is the one that was killed part way through.
    /// </summary>
    [Fact]
    public async Task The_run_log_is_written_incrementally_rather_than_at_the_end()
    {
        await using var harness = await OrchestratorHarness.CreateAsync();
        var targets = await harness.SeedTargetsAsync("London", 4);

        var sawContentMidRun = false;

        var progress = new InlineProgress<DeploymentProgress>(p =>
        {
            if (p.CompletedCount >= 1 && !sawContentMidRun)
            {
                var path = Path.Combine(harness.LogDirectory, "run.log");
                if (File.Exists(path) &&
                    File.ReadAllText(path).Contains("dc01.corp.local", StringComparison.Ordinal))
                {
                    sawContentMidRun = true;
                }
            }
        });

        await harness.Orchestrator.DeployAsync(
            harness.Request(targets, maxParallel: 1), progress, Ct);

        Assert.True(sawContentMidRun, "The run log had no content until the run finished.");
    }

    [Fact]
    public async Task A_skipped_target_appears_in_the_record_rather_than_vanishing()
    {
        await using var harness = await OrchestratorHarness.CreateAsync();
        var targets = await harness.SeedTargetsAsync("London", 8);

        harness.Transport.ConfigureDefault(h => h.PreflightFailure = new SimulatedFailure(
            ErrorCategory.Connectivity, "unreachable"));

        var summary = await harness.Orchestrator.DeployAsync(
            harness.Request(targets, maxParallel: 1), null, Ct);

        // A run that halted after three of eight must still account for the other five.
        Assert.Equal(8, summary.Outcomes.Count);

        var stored = await harness.Inventory.Deployments.GetResultsForRunAsync(summary.RunId, Ct);
        Assert.Equal(8, stored.Count);
        Assert.Equal(5, stored.Count(r => r.Outcome == DeploymentOutcome.Skipped));

        var csv = harness.ReadResultsCsv();
        Assert.Equal(9, csv.Split('\n', StringSplitOptions.RemoveEmptyEntries).Length);
    }

    /// <summary>PRD 8.4: the operator is offered a retry of the failed targets.</summary>
    [Fact]
    public async Task Failed_targets_are_offered_for_retry_but_unattempted_ones_are_not()
    {
        await using var harness = await OrchestratorHarness.CreateAsync();
        var targets = await harness.SeedTargetsAsync("London", 6);

        harness.Transport
            .ConfigureHost("dc02.corp.local", h => h.ReturnsExitCodes(1603))
            .ConfigureHost("dc04.corp.local", h => h.ReturnsExitCodes(1638));

        var summary = await harness.Orchestrator.DeployAsync(
            harness.Request(targets, maxParallel: 1), null, Ct);

        Assert.Equal(
            ["dc02.corp.local", "dc04.corp.local"],
            summary.FailedTargets.Select(t => t.Fqdn).OrderBy(f => f, StringComparer.Ordinal));

        Assert.Empty(summary.UnattemptedTargets);
    }
}

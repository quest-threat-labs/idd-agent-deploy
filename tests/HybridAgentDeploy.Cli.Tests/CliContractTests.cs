using System.Text;
using System.Text.Json;
using HybridAgentDeploy.Cli;
using HybridAgentDeploy.Cli.Commands;
using HybridAgentDeploy.Core.Deployment;
using HybridAgentDeploy.Core.Models;

namespace HybridAgentDeploy.Cli.Tests;

/// <summary>
/// The CLI's contract with a calling script (PRD 9): exit codes, and the strict separation of
/// stdout from stderr.
/// </summary>
/// <remarks>
/// These matter more than they look. A script that retries on the wrong exit code, or a parser
/// that chokes because one progress line reached stdout, fails intermittently and only on the
/// runs that had something to report — which is exactly when the operator needs it to work.
/// </remarks>
public sealed class ExitCodeTests
{
    [Fact]
    public void A_run_where_everything_succeeded_exits_zero()
    {
        Assert.Equal(ExitCode.Success, DeployCommand.ExitCodeFor(Summary()));
    }

    [Fact]
    public void A_run_with_failures_exits_one()
    {
        Assert.Equal(
            ExitCode.CompletedWithFailures,
            DeployCommand.ExitCodeFor(Summary(failures: 1)));
    }

    [Fact]
    public void A_halted_run_exits_two()
    {
        Assert.Equal(
            ExitCode.HaltedByCircuitBreaker,
            DeployCommand.ExitCodeFor(Summary(failures: 3, halted: true)));
    }

    [Fact]
    public void A_cancelled_run_exits_four()
    {
        Assert.Equal(ExitCode.Cancelled, DeployCommand.ExitCodeFor(Summary(cancelled: true)));
    }

    /// <summary>
    /// A halted run is reported as halted even though it also has failures, because a script
    /// deciding whether to retry needs to know the run stopped early rather than merely that
    /// some targets failed.
    /// </summary>
    [Fact]
    public void Halting_takes_precedence_over_failures_and_cancellation()
    {
        Assert.Equal(
            ExitCode.HaltedByCircuitBreaker,
            DeployCommand.ExitCodeFor(Summary(failures: 3, halted: true, cancelled: true)));
    }

    /// <summary>Cancelling a run that had no failures still reports cancellation, not success.</summary>
    [Fact]
    public void Cancellation_is_not_reported_as_success()
    {
        Assert.Equal(ExitCode.Cancelled, DeployCommand.ExitCodeFor(Summary(failures: 0, cancelled: true)));
    }

    /// <summary>The five codes are distinct; PRD 9 gives each a separate meaning.</summary>
    [Fact]
    public void Every_exit_code_is_distinct()
    {
        int[] codes =
        [
            ExitCode.Success,
            ExitCode.CompletedWithFailures,
            ExitCode.HaltedByCircuitBreaker,
            ExitCode.InvalidArgumentsOrPreflight,
            ExitCode.Cancelled,
        ];

        Assert.Equal(codes.Length, codes.Distinct().Count());
        Assert.Equal([0, 1, 2, 3, 4], codes);
    }

    private static DeploymentRunSummary Summary(
        int failures = 0,
        bool halted = false,
        bool cancelled = false)
    {
        var outcomes = new List<TargetOutcome>
        {
            NewOutcome("dc01.corp.local", DeploymentOutcome.Success),
        };

        for (var i = 0; i < failures; i++)
        {
            outcomes.Add(NewOutcome($"dcf{i}.corp.local", DeploymentOutcome.Failure));
        }

        return new DeploymentRunSummary
        {
            RunGuid = Guid.NewGuid(),
            RunId = 1,
            Outcomes = outcomes,
            StartedUtc = DateTimeOffset.UtcNow,
            CompletedUtc = DateTimeOffset.UtcNow,
            WasHalted = halted,
            HaltReason = halted ? "three consecutive authentication failures" : null,
            WasCancelled = cancelled,
            LogDirectory = @"C:\logs\run",
            CommandTemplate = "msiexec /i ...",
        };
    }

    private static TargetOutcome NewOutcome(string fqdn, DeploymentOutcome outcome) => new()
    {
        Target = new DeploymentTarget(1, fqdn, "London"),
        Outcome = outcome,
        StartedUtc = DateTimeOffset.UtcNow,
        CompletedUtc = DateTimeOffset.UtcNow,
    };
}

/// <summary>
/// PRD 9: all output to stdout, diagnostics to stderr, and JSON with no interleaved human text.
/// </summary>
public sealed class OutputTests
{
    [Fact]
    public void Diagnostics_and_warnings_never_reach_stdout()
    {
        var (output, stdout, stderr) = NewOutput();

        output.Line("payload");
        output.Diagnostic("5 domain controller(s).");
        output.Warning("the inventory is stale");
        output.Error("something failed");

        Assert.Equal("payload", stdout.ToString().Trim());

        var diagnostics = stderr.ToString();
        Assert.Contains("5 domain controller(s).", diagnostics, StringComparison.Ordinal);
        Assert.Contains("warning: the inventory is stale", diagnostics, StringComparison.Ordinal);
        Assert.Contains("error: something failed", diagnostics, StringComparison.Ordinal);
    }

    /// <summary>
    /// The property a scripted caller depends on: stdout parses as JSON even when the command
    /// had plenty to say.
    /// </summary>
    [Fact]
    public void Json_output_stays_parseable_while_diagnostics_are_written()
    {
        var (output, stdout, _) = NewOutput();

        output.Diagnostic("Checking dc01...");
        output.Json(new { fqdn = "dc01.corp.local", reachable = true });
        output.Warning("dc02 was unreachable");

        using var document = JsonDocument.Parse(stdout.ToString());

        Assert.Equal("dc01.corp.local", document.RootElement.GetProperty("fqdn").GetString());
        Assert.True(document.RootElement.GetProperty("reachable").GetBoolean());
    }

    [Fact]
    public void Json_uses_snake_case_so_property_names_are_stable_for_scripts()
    {
        var (output, stdout, _) = NewOutput();

        output.Json(new { ProductVersion = "7.7.34005.0", ExitCode = 0 });

        using var document = JsonDocument.Parse(stdout.ToString());

        Assert.True(document.RootElement.TryGetProperty("product_version", out _));
        Assert.True(document.RootElement.TryGetProperty("exit_code", out _));
    }

    [Fact]
    public void A_table_aligns_columns_and_leaves_no_trailing_whitespace()
    {
        var (output, stdout, _) = NewOutput();

        output.Table(
            ["FQDN", "SITE"],
            [
                ["dc01.corp.local", "London"],
                ["a-much-longer-name.corp.local", "Belfast"],
            ]);

        var lines = stdout.ToString().Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);

        Assert.All(lines, line => Assert.Equal(line.TrimEnd(), line));

        // The header, the rule, and both rows put SITE at the same column.
        var siteColumn = lines[0].IndexOf("SITE", StringComparison.Ordinal);
        Assert.Equal(siteColumn, lines[2].IndexOf("London", StringComparison.Ordinal));
        Assert.Equal(siteColumn, lines[3].IndexOf("Belfast", StringComparison.Ordinal));
    }

    [Fact]
    public void A_null_cell_renders_as_empty_rather_than_breaking_alignment()
    {
        var (output, stdout, _) = NewOutput();

        output.Table(["FQDN", "SITE", "OS"], [["dc01.corp.local", null, "Server 2022"]]);

        Assert.Contains("Server 2022", stdout.ToString(), StringComparison.Ordinal);
    }

    /// <summary>
    /// Error detail is free text full of commas and quotes; a CSV that breaks on it is useless
    /// to an operator triaging in a spreadsheet.
    /// </summary>
    [Fact]
    public void Csv_escapes_commas_and_quotes()
    {
        var (output, stdout, _) = NewOutput();

        output.Csv(
            ["FQDN", "DETAIL"],
            [["dc01.corp.local", "Unreachable on 445, 5985; check \"C$\" is shared."]]);

        var lines = stdout.ToString().Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);

        Assert.Equal(2, lines.Length);
        Assert.Contains("\"\"C$\"\"", lines[1], StringComparison.Ordinal);
        Assert.StartsWith("dc01.corp.local,\"", lines[1], StringComparison.Ordinal);
    }

    private static (Output Output, StringBuilder Stdout, StringBuilder Stderr) NewOutput()
    {
        var stdout = new StringBuilder();
        var stderr = new StringBuilder();
        return (new Output(new StringWriter(stdout), new StringWriter(stderr)), stdout, stderr);
    }
}

using HybridAgentDeploy.Core.Configuration;
using HybridAgentDeploy.Core.Deployment;
using HybridAgentDeploy.Core.Models;
using HybridAgentDeploy.Core.Testing;

namespace HybridAgentDeploy.Core.Tests;

/// <summary>
/// PRD 11: confirming a domain controller is ready before anything is installed on it.
/// </summary>
/// <remarks>
/// Driven entirely through <see cref="SimulatedTransport"/>, like the orchestrator tests. The
/// property being asserted throughout is that validation reaches a verdict about a host
/// without ever executing an installer on it.
/// </remarks>
public sealed class ValidationTests
{
    private static readonly MsiPackageInfo Package = new()
    {
        FilePath = @"C:\packages\agent.msi",
        FileName = "agent.msi",
        FileSizeBytes = 67_000_000,
        Sha256 = new string('a', 64),
        ProductName = "Quest Change Auditor Agent (x64)",
        ProductVersion = "7.5.0.100",
        ProductCode = "{11111111-2222-3333-4444-555555555555}",
        LooksLikeExpectedProduct = true,
    };

    private static DeploymentTarget Target(string fqdn, string? site = "HQ") =>
        new(fqdn.GetHashCode(StringComparison.OrdinalIgnoreCase), fqdn, site);

    // ---------------------------------------------------------------------------------------
    // The single most important property of this feature.
    // ---------------------------------------------------------------------------------------

    /// <summary>
    /// Validation must never install anything. The whole feature exists so an operator can
    /// find out whether a deployment would work <em>without</em> running one against Tier 0
    /// infrastructure, and a validation that quietly invoked msiexec would be worse than no
    /// validation at all.
    /// </summary>
    [Fact]
    public async Task Validation_never_runs_msiexec()
    {
        var transport = new SimulatedTransport();
        transport.ConfigureDefault(h => h.StandardOutput = TargetState(agent: "none", mode: "none"));

        var summary = await RunAsync(transport, [Target("dc01.corp.local"), Target("dc02.corp.local")]);

        Assert.Equal(2, summary.ReadyCount);

        Assert.NotEmpty(transport.ExecutedCommands);
        Assert.DoesNotContain(
            transport.ExecutedCommands,
            c => c.Executable.Contains("msiexec", StringComparison.OrdinalIgnoreCase) ||
                 c.Arguments.Any(a => a.Contains("msiexec", StringComparison.OrdinalIgnoreCase)));

        // Nor may the package itself reach a domain controller. Staging a 67 MB installer to
        // Tier 0 hosts is not something a read-only check gets to do as a side effect.
        Assert.DoesNotContain(
            transport.Calls,
            c => c.Stage == DeploymentStage.Stage &&
                 (c.Detail?.Contains("agent.msi", StringComparison.OrdinalIgnoreCase) ?? false));
    }

    /// <summary>
    /// SEC8: the probe is removed from every target, including ones that failed.
    /// </summary>
    [Fact]
    public async Task The_probe_is_cleaned_up_from_every_target_that_was_written_to()
    {
        var transport = new SimulatedTransport();
        transport.ConfigureDefault(h => h.StandardOutput = TargetState(agent: "none", mode: "none"));
        transport.ConfigureHost("dc02.corp.local", h =>
            h.ExecutionLaunchFailure = new SimulatedFailure(
                ErrorCategory.Authentication, "Access is denied opening a session on dc02."));

        await RunAsync(transport, [Target("dc01.corp.local"), Target("dc02.corp.local")]);

        foreach (var fqdn in new[] { "dc01.corp.local", "dc02.corp.local" })
        {
            Assert.Contains(
                transport.Calls,
                c => c.Stage == DeploymentStage.Cleanup &&
                     string.Equals(c.TargetHost, fqdn, StringComparison.OrdinalIgnoreCase));
        }
    }

    /// <summary>
    /// A target that cannot be reached is never written to.
    /// </summary>
    [Fact]
    public async Task An_unreachable_target_is_not_staged_to()
    {
        var transport = new SimulatedTransport();
        transport.ConfigureDefault(h => h.StandardOutput = TargetState(agent: "none", mode: "none"));
        transport.ConfigureHost("dc02.corp.local", h =>
            h.PreflightFailure = new SimulatedFailure(
                ErrorCategory.Connectivity,
                "dc02.corp.local did not answer on TCP 5985."));

        var summary = await RunAsync(transport, [Target("dc01.corp.local"), Target("dc02.corp.local")]);

        var failed = summary.Outcomes.Single(o => o.Target.Fqdn == "dc02.corp.local");
        Assert.False(failed.Passed);
        Assert.Equal(ErrorCategory.Connectivity, failed.ErrorCategory);
        Assert.Contains("5985", failed.Summary, StringComparison.Ordinal);

        Assert.DoesNotContain(
            transport.Calls,
            c => c.TargetHost == "dc02.corp.local" && c.Stage != DeploymentStage.Preflight);
    }

    /// <summary>
    /// Reaching a port is not the same as being able to write. A host whose share answers but
    /// refuses the copy is the case validation exists to catch before 67 MB is sent to it.
    /// </summary>
    [Fact]
    public async Task A_target_that_cannot_be_written_to_is_reported_as_not_ready()
    {
        var transport = new SimulatedTransport();
        transport.ConfigureDefault(h => h.StandardOutput = TargetState(agent: "none", mode: "none"));
        transport.ConfigureHost("dc02.corp.local", h =>
            h.StagingFailure = new SimulatedFailure(
                ErrorCategory.Staging,
                @"access denied writing to \\dc02.corp.local\C$\Windows\Temp - confirm the " +
                "running account holds local administrator rights on this DC"));

        var summary = await RunAsync(transport, [Target("dc02.corp.local")]);

        var outcome = summary.Outcomes.Single();
        Assert.False(outcome.Passed);
        Assert.Equal(ErrorCategory.Staging, outcome.ErrorCategory);
        Assert.Contains("local administrator", outcome.Summary, StringComparison.Ordinal);

        // The check ran, and the session was never opened, because there was no point.
        Assert.Contains(outcome.Checks, c => c.Name == "Can stage files" && !c.Passed);
        Assert.DoesNotContain(outcome.Checks, c => c.Name == "Can run commands");

        // SEC8: cleanup still runs. A copy that failed part-way is exactly the case that can
        // leave something behind, so it is the last case where skipping cleanup is safe.
        Assert.Contains(transport.Calls, c => c.Stage == DeploymentStage.Cleanup);
    }

    /// <summary>
    /// SEC6 in miniature. A copy that arrives altered means staging cannot be trusted on this
    /// host, which is exactly what a later deployment would need it for.
    /// </summary>
    [Fact]
    public async Task A_probe_that_arrives_with_the_wrong_hash_fails_the_target()
    {
        var transport = new SimulatedTransport();
        transport.ConfigureDefault(h =>
        {
            h.StandardOutput = TargetState(agent: "none", mode: "none");

            // SimulatedTransport reports this as the staged copy's hash; the probe's real
            // content is random, so it can never match.
            h.StagedSha256 = new string('b', 64);
        });

        var summary = await RunAsync(transport, [Target("dc01.corp.local")]);

        var outcome = summary.Outcomes.Single();
        Assert.False(outcome.Passed);
        Assert.Contains(outcome.Checks, c => c.Name == "Can stage files" && !c.Passed);
    }

    // ---------------------------------------------------------------------------------------
    // Predicting what a deployment would do.
    // ---------------------------------------------------------------------------------------

    [Fact]
    public async Task A_host_with_no_agent_is_predicted_as_a_fresh_install()
    {
        var transport = new SimulatedTransport();
        transport.ConfigureDefault(h => h.StandardOutput = TargetState(agent: "none", mode: "none"));

        var summary = await RunAsync(transport, [Target("dc01.corp.local")]);

        var outcome = summary.Outcomes.Single();
        Assert.True(outcome.Passed);
        Assert.Null(outcome.InstalledAgent);
        Assert.Equal(PredictedAction.FreshInstall, outcome.Predicted);
    }

    /// <summary>
    /// The case that costs a real deployment: reachable, writable, and the installer will
    /// still refuse with 1638. Counted separately from both successes and failures, because
    /// nothing is wrong with the domain controller — the package is simply the wrong one.
    /// </summary>
    [Fact]
    public async Task A_host_with_a_newer_agent_is_flagged_before_the_install_is_refused()
    {
        var transport = new SimulatedTransport();
        transport.ConfigureDefault(h =>
            h.StandardOutput = TargetState(version: "7.6.0.10", productCode: "{1234}"));

        var summary = await RunAsync(transport, [Target("dc01.corp.local")]);

        var outcome = summary.Outcomes.Single();
        Assert.True(outcome.Passed);
        Assert.Equal(PredictedAction.DowngradeBlocked, outcome.Predicted);
        Assert.Equal(1, summary.WouldBeRefusedCount);
        Assert.Equal("7.6.0.10", outcome.InstalledAgent!.Version);
        Assert.Contains("will refuse", outcome.Summary, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("7.4.0.20", PredictedAction.Upgrade)]
    [InlineData("7.5.0.100", PredictedAction.Reinstall)]
    [InlineData("7.6.0.1", PredictedAction.DowngradeBlocked)]
    [InlineData("7.5", PredictedAction.Upgrade)]
    [InlineData("not a version", PredictedAction.Unknown)]
    public void The_prediction_follows_the_installed_version(string installed, PredictedAction expected)
    {
        var agent = new InstalledAgent("Quest Change Auditor Agent (x64)", installed, "{1234}");

        Assert.Equal(expected, TargetValidator.Predict(agent, Package));
    }

    [Theory]
    [InlineData("AGENT|none", null)]
    [InlineData("AGENT|NONE", null)]
    [InlineData("", null)]
    [InlineData("   ", null)]
    [InlineData("AGENT|Quest Change Auditor Agent (x64)|7.5.0.100|{abc}", "7.5.0.100")]
    [InlineData("AGENT|Quest Change Auditor Agent (x64)|7.5.0.100", null)]
    public void The_installed_agent_query_output_is_parsed_or_treated_as_absent(
        string output,
        string? expectedVersion)
    {
        var parsed = TargetValidator.ParseTargetState(output);

        Assert.Equal(expectedVersion, parsed.Agent?.Version);
    }

    /// <summary>
    /// A remote PowerShell session prints more than its last expression when a profile or a
    /// module has something to say, and the lines can arrive in any order.
    /// </summary>
    /// <remarks>
    /// Fields are looked up by label rather than by position for exactly this reason. Reading
    /// the third line as "the version" would have turned a warning banner into a 7.6 agent
    /// looking like a 7.7 one.
    /// </remarks>
    [Fact]
    public void Noise_around_the_query_output_is_ignored_and_labels_drive_the_parse()
    {
        var parsed = TargetValidator.ParseTargetState(
            "WARNING: something chatty\r\n" +
            "OS|26100|Windows Server 2025 Standard\r\n" +
            "AGENT|Quest Change Auditor 7.7.0 Agent|7.7.34005.0|{abc}\r\n" +
            "MODE|1\r\n");

        Assert.Equal("7.7.34005.0", parsed.Agent!.Version);
        Assert.Equal("{abc}", parsed.Agent.ProductCode);
        Assert.Equal(AgentMode.IdentityDefense, parsed.Agent.Mode);
        Assert.Equal(26100, parsed.OperatingSystem!.BuildNumber);
        Assert.Equal("Windows Server 2025 Standard", parsed.OperatingSystem.ProductName);
    }

    /// <summary>
    /// The connection mode. Anything other than 0 or 1 is reported as unrecognised rather
    /// than folded into a mode — a wrong answer here tells the operator a deployment is safe
    /// when the installer is about to refuse it.
    /// </summary>
    [Theory]
    [InlineData("MODE|0", AgentMode.ChangeAuditor)]
    [InlineData("MODE|1", AgentMode.IdentityDefense)]
    [InlineData("MODE|2", AgentMode.Unrecognised)]
    [InlineData("MODE|yes", AgentMode.Unrecognised)]
    [InlineData("MODE|none", null)]
    [InlineData("", null)]
    public void The_connection_mode_is_parsed_or_reported_as_unknown(string modeLine, AgentMode? expected)
    {
        var parsed = TargetValidator.ParseTargetState(
            "AGENT|Quest Change Auditor Agent (x64)|7.5.0.100|{abc}\r\n" + modeLine);

        Assert.Equal(expected, parsed.Agent!.Mode);
    }

    [Theory]
    [InlineData("OS|26100|Windows Server 2025 Standard", 26100, true)]
    [InlineData("OS|14393|Windows Server 2016 Standard", 14393, true)]
    [InlineData("OS|9600|Windows Server 2012 R2 Standard", 9600, false)]
    public void The_operating_system_is_parsed_and_checked_against_the_agent_minimum(
        string line,
        int expectedBuild,
        bool supported)
    {
        var os = TargetValidator.ParseTargetState(line).OperatingSystem;

        Assert.Equal(expectedBuild, os!.BuildNumber);
        Assert.Equal(supported, os.IsSupported);
    }

    [Theory]
    [InlineData("OS|not-a-number|Windows")]
    [InlineData("")]
    public void An_unreadable_operating_system_is_reported_as_unknown(string line)
    {
        Assert.Null(TargetValidator.ParseTargetState(line).OperatingSystem);
    }

    // ---------------------------------------------------------------------------------------
    // Mode. The two directions are NOT equivalent: SG=1 over a Change Auditor agent migrates
    // it to the cloud product, and the reverse does not move it back.
    // ---------------------------------------------------------------------------------------

    /// <summary>
    /// The supported direction. A migration succeeds, so it is not a refusal — but it changes
    /// which product a domain controller reports to, and the checkbox driving it defaults to
    /// on, so it can be reached by inaction rather than by decision.
    /// </summary>
    [Fact]
    public async Task A_cloud_run_against_an_on_premises_agent_is_reported_as_a_migration()
    {
        var transport = new SimulatedTransport();
        transport.ConfigureDefault(h => h.StandardOutput = TargetState(version: "7.4.0.20", mode: "0"));

        var summary = await RunAsync(transport, [Target("dc01.corp.local")], cloudMode: true);

        var outcome = summary.Outcomes.Single();

        Assert.True(outcome.Passed);
        Assert.Equal(ModeChange.MigratesToCloud, outcome.ModeChange);
        Assert.Equal(1, summary.MigrationCount);

        // A migration is a supported operation that succeeds, so it is emphatically not a
        // refusal, and must not be counted as one.
        Assert.Equal(0, summary.WouldBeRefusedCount);
        Assert.Equal(0, summary.UnsupportedModeChangeCount);

        // The migration leads, the version follows: which product the DC reports to afterwards
        // matters more than which build it lands on.
        Assert.Equal(PredictedAction.Upgrade, outcome.Predicted);
        Assert.Contains("MIGRATES", outcome.Summary, StringComparison.Ordinal);
        Assert.Contains("upgrading from 7.4.0.20", outcome.Summary, StringComparison.Ordinal);
    }

    /// <summary>
    /// The unsupported direction. Taken from the agent's developer documentation rather than
    /// from anything the MSI enforces — its <c>UPGRADE_SG_MISMATCH</c> property only decides
    /// whether the old installation name is carried forward.
    /// </summary>
    [Fact]
    public async Task An_on_premises_run_against_a_cloud_agent_is_reported_as_unsupported()
    {
        var transport = new SimulatedTransport();
        transport.ConfigureDefault(h => h.StandardOutput = TargetState(version: "7.4.0.20", mode: "1"));

        var summary = await RunAsync(transport, [Target("dc01.corp.local")], cloudMode: false);

        var outcome = summary.Outcomes.Single();
        Assert.Equal(ModeChange.NotSupported, outcome.ModeChange);
        Assert.Equal(1, summary.UnsupportedModeChangeCount);
        Assert.Equal(1, summary.WouldBeRefusedCount);
        Assert.Equal(0, summary.MigrationCount);
        Assert.Contains("does not move back", outcome.Summary, StringComparison.Ordinal);
    }

    /// <summary>
    /// A migration onto an older package is refused for the version, and then the migration
    /// does not happen either — so the headline has to say both.
    /// </summary>
    [Fact]
    public async Task A_migration_blocked_by_a_newer_installed_version_says_so()
    {
        var transport = new SimulatedTransport();
        transport.ConfigureDefault(h => h.StandardOutput = TargetState(version: "7.9.0.1", mode: "0"));

        var summary = await RunAsync(transport, [Target("dc01.corp.local")], cloudMode: true);

        var outcome = summary.Outcomes.Single();
        Assert.Equal(ModeChange.MigratesToCloud, outcome.ModeChange);
        Assert.Equal(PredictedAction.DowngradeBlocked, outcome.Predicted);
        Assert.Contains("neither the migration nor the install", outcome.Summary, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("1", true)]
    [InlineData("0", false)]
    public async Task A_matching_mode_is_not_a_mode_change(string installedMode, bool cloudMode)
    {
        var transport = new SimulatedTransport();
        transport.ConfigureDefault(h => h.StandardOutput = TargetState(version: "7.4.0.20", mode: installedMode));

        var summary = await RunAsync(transport, [Target("dc01.corp.local")], cloudMode: cloudMode);

        var outcome = summary.Outcomes.Single();
        Assert.Equal(ModeChange.None, outcome.ModeChange);
        Assert.Equal(0, summary.MigrationCount);
        Assert.Equal(0, summary.UnsupportedModeChangeCount);
        Assert.Contains("ready - upgrade", outcome.Summary, StringComparison.Ordinal);
    }

    /// <summary>
    /// A fresh install cannot be a mode change: there is no installed mode to move away from.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task A_host_with_no_agent_is_never_a_mode_change(bool cloudMode)
    {
        var transport = new SimulatedTransport();
        transport.ConfigureDefault(h => h.StandardOutput = TargetState(agent: "none", mode: "none"));

        var summary = await RunAsync(transport, [Target("dc01.corp.local")], cloudMode: cloudMode);

        Assert.Equal(ModeChange.None, summary.Outcomes.Single().ModeChange);
    }

    /// <summary>
    /// An agent whose mode cannot be read is not assumed to match, and is not guessed at
    /// either — the check says plainly that the question is open.
    /// </summary>
    [Fact]
    public async Task An_unreadable_mode_is_reported_rather_than_assumed_to_match()
    {
        var transport = new SimulatedTransport();
        transport.ConfigureDefault(h => h.StandardOutput = TargetState(version: "7.4.0.20", mode: "none"));

        var summary = await RunAsync(transport, [Target("dc01.corp.local")]);

        var outcome = summary.Outcomes.Single();
        Assert.Equal(ModeChange.None, outcome.ModeChange);
        Assert.Contains(
            outcome.Checks,
            c => c.Name == "Connection mode" && c.Detail.Contains("could not be read", StringComparison.Ordinal));
    }

    // ---------------------------------------------------------------------------------------
    // Operating system. The MSI refuses anything below Server 2016 through a launch condition.
    // ---------------------------------------------------------------------------------------

    /// <summary>
    /// Reported as a failure rather than as a refused package, because it is a property of the
    /// host: no version of this agent can be installed on a domain controller this old.
    /// </summary>
    [Fact]
    public async Task A_domain_controller_below_server_2016_is_not_ready()
    {
        var transport = new SimulatedTransport();
        transport.ConfigureDefault(h =>
            h.StandardOutput = TargetState(agent: "none", mode: "none", build: "9600",
                osName: "Windows Server 2012 R2 Standard"));

        var summary = await RunAsync(transport, [Target("dc01.corp.local")]);

        var outcome = summary.Outcomes.Single();
        Assert.False(outcome.Passed);
        Assert.Equal(1, summary.ProblemCount);
        Assert.Contains(outcome.Checks, c => c.Name == "Supported operating system" && !c.Passed);
        Assert.Contains("Windows Server 2016 or later", outcome.Summary, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_supported_domain_controller_reports_its_operating_system()
    {
        var transport = new SimulatedTransport();
        transport.ConfigureDefault(h => h.StandardOutput = TargetState(agent: "none", mode: "none"));

        var summary = await RunAsync(transport, [Target("dc01.corp.local")]);

        var outcome = summary.Outcomes.Single();
        Assert.True(outcome.Passed);
        Assert.Equal(26100, outcome.OperatingSystem!.BuildNumber);
        Assert.Contains(outcome.Checks, c => c.Name == "Supported operating system" && c.Passed);
    }

    /// <summary>
    /// An unreadable OS does not fail the target. The installer checks it too, and refusing a
    /// domain controller because one registry read came back empty would be a worse answer
    /// than letting the installer's own launch condition speak.
    /// </summary>
    [Fact]
    public async Task An_unreadable_operating_system_does_not_fail_the_target()
    {
        var transport = new SimulatedTransport();
        transport.ConfigureDefault(h =>
            h.StandardOutput = "AGENT|none\r\nMODE|none");

        var summary = await RunAsync(transport, [Target("dc01.corp.local")]);

        var outcome = summary.Outcomes.Single();
        Assert.True(outcome.Passed);
        Assert.Null(outcome.OperatingSystem);
    }

    // ---------------------------------------------------------------------------------------
    // Pacing. Validation opens real sessions to domain controllers and is throttled like
    // anything else that does.
    // ---------------------------------------------------------------------------------------

    /// <summary>R7.1: never more than five at once, whatever is asked for.</summary>
    [Fact]
    public async Task Validation_respects_the_concurrency_ceiling()
    {
        var transport = new SimulatedTransport();
        transport.ConfigureDefault(h =>
        {
            h.StandardOutput = TargetState(agent: "none", mode: "none");
            h.PreflightDelay = TimeSpan.FromMilliseconds(30);
            h.ExecutionDelay = TimeSpan.FromMilliseconds(30);
        });

        // Every target in its own large site, so the site guard cannot be what limits this.
        var targets = Enumerable.Range(1, 20)
            .Select(i => Target($"dc{i:00}.corp.local", $"Site{i:00}"))
            .ToList();

        var sites = targets.ToDictionary(t => t.SiteName!, _ => 20, StringComparer.OrdinalIgnoreCase);

        await RunAsync(transport, targets, maxParallel: 99, activeDcsPerSite: sites);

        Assert.True(
            transport.ConcurrencyObserved <= DeploymentLimits.MaxConcurrencyCeiling,
            $"{transport.ConcurrencyObserved} targets were in flight at once; the ceiling is " +
            $"{DeploymentLimits.MaxConcurrencyCeiling}.");
    }

    /// <summary>
    /// R7.2: within one site, never more than half its domain controllers. A two-DC site is
    /// checked one at a time, which is what the operator is shown on the Progress tab so a
    /// deliberately slow run does not look like a hung one.
    /// </summary>
    [Fact]
    public async Task Validation_respects_the_site_guard()
    {
        var transport = new SimulatedTransport();
        transport.ConfigureDefault(h =>
        {
            h.StandardOutput = TargetState(agent: "none", mode: "none");
            h.PreflightDelay = TimeSpan.FromMilliseconds(40);
        });

        var targets = new[]
        {
            Target("dc01.corp.local", "Branch"),
            Target("dc02.corp.local", "Branch"),
        };

        var sites = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase) { ["Branch"] = 2 };

        await RunAsync(transport, targets, maxParallel: 5, activeDcsPerSite: sites);

        Assert.Equal(1, transport.ConcurrencyObserved);
    }

    [Fact]
    public void The_pacing_description_names_the_site_that_is_the_bottleneck()
    {
        var guard = new SiteConcurrencyGuard(new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
        {
            ["Branch"] = 2,
            ["HQ"] = 40,
        });

        var text = guard.DescribePacing(
            [Target("dc01.corp.local", "Branch"), Target("dc02.corp.local", "HQ")],
            maxParallel: 5);

        Assert.Contains("At most 5", text, StringComparison.Ordinal);
        Assert.Contains("Branch at most 1", text, StringComparison.Ordinal);

        // HQ's limit of 20 never binds against a ceiling of 5, and naming it would bury the
        // one site that actually slows the run down.
        Assert.DoesNotContain("HQ", text, StringComparison.Ordinal);
    }

    /// <summary>
    /// A cancelled run lists what it did not check rather than omitting it, so the operator is
    /// not left believing the remainder passed.
    /// </summary>
    [Fact]
    public async Task Cancelled_targets_are_reported_rather_than_dropped()
    {
        var transport = new SimulatedTransport();
        transport.ConfigureDefault(h =>
        {
            h.StandardOutput = TargetState(agent: "none", mode: "none");
            h.PreflightDelay = TimeSpan.FromMilliseconds(60);
        });

        using var cancellation = new CancellationTokenSource();

        var targets = Enumerable.Range(1, 12)
            .Select(i => Target($"dc{i:00}.corp.local", $"Site{i:00}"))
            .ToList();

        var sites = targets.ToDictionary(t => t.SiteName!, _ => 20, StringComparer.OrdinalIgnoreCase);

        var directory = CreateTempDirectory();
        try
        {
            var request = ValidationRequest.Create(
                Package, targets, @"CORP\admin", directory,
                maxParallel: 2, activeDcsPerSite: sites);

            var run = new ValidationRunner(transport).ValidateAsync(request, null, cancellation.Token);
            await cancellation.CancelAsync();
            var summary = await run;

            Assert.Equal(targets.Count, summary.Outcomes.Count);
            Assert.True(summary.WasCancelled);
            Assert.Contains(
                summary.Outcomes,
                o => o.Checks.Any(c => c.Name == "Not checked"));
        }
        finally
        {
            Cleanup(directory);
        }
    }

    // ---------------------------------------------------------------------------------------
    // The record left behind.
    // ---------------------------------------------------------------------------------------

    /// <summary>
    /// The log must say plainly that nothing was installed. Someone reading this directory
    /// months later, possibly during an audit of what ran against their domain controllers,
    /// should not have to infer it from the absence of an install log.
    /// </summary>
    [Fact]
    public async Task The_log_states_that_nothing_was_installed_and_records_every_check()
    {
        var transport = new SimulatedTransport();
        transport.ConfigureDefault(h =>
            h.StandardOutput = TargetState(version: "7.4.0.20"));

        var directory = CreateTempDirectory();
        try
        {
            var summary = await RunAsync(
                transport, [Target("dc01.corp.local")], logDirectory: directory);

            var log = await File.ReadAllTextAsync(
                Path.Combine(directory, "validation.log"), CancellationToken.None);

            Assert.Contains("NOTHING WAS INSTALLED BY THIS RUN", log, StringComparison.Ordinal);
            Assert.Contains("msiexec was not", log, StringComparison.Ordinal);

            // Passes as well as failures: a log that records only failures cannot distinguish
            // "we checked and it was fine" from "we never checked".
            Assert.Contains("[pass] Reachable", log, StringComparison.Ordinal);
            Assert.Contains("[pass] Can stage files", log, StringComparison.Ordinal);
            Assert.Contains("[pass] Can run commands", log, StringComparison.Ordinal);
            Assert.Contains("7.4.0.20", log, StringComparison.Ordinal);

            var csv = await File.ReadAllTextAsync(
                Path.Combine(directory, "results.csv"), CancellationToken.None);

            Assert.Contains("predicted_action", csv, StringComparison.Ordinal);
            Assert.Contains("Upgrade", csv, StringComparison.Ordinal);
            Assert.Equal(directory, summary.LogDirectory);
        }
        finally
        {
            Cleanup(directory);
        }
    }

    /// <summary>
    /// A validation directory is distinguishable from a deployment's at a glance. An operator
    /// scanning the log root should be able to tell which folders represent something that
    /// actually installed an agent on a domain controller.
    /// </summary>
    [Fact]
    public void A_validation_log_directory_is_named_distinctly_from_a_deployment_run()
    {
        var runGuid = Guid.Parse("6df6429a-0000-0000-0000-000000000000");
        var at = new DateTimeOffset(2026, 8, 25, 14, 30, 0, TimeSpan.Zero);

        var deployment = AppConfigurationLoader.RunLogDirectory(@"C:\logs", runGuid, at);
        var validation = AppConfigurationLoader.ValidationLogDirectory(@"C:\logs", runGuid, at);

        Assert.NotEqual(deployment, validation);
        Assert.EndsWith("-validate", validation, StringComparison.Ordinal);
        Assert.StartsWith(deployment, validation, StringComparison.Ordinal);
    }

    /// <summary>
    /// NFR7: a probe directory found on a domain controller must be traceable to the run that
    /// created it, and distinguishable from one an installation happened in.
    /// </summary>
    [Fact]
    public void The_probe_directory_is_under_the_admin_only_staging_root_and_is_named_for_validation()
    {
        var runGuid = Guid.NewGuid();
        var path = StagingPaths.ForValidation(runGuid);

        Assert.StartsWith(@"C:\Windows\Temp\HybridAgentDeploy", path, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(runGuid.ToString("D"), path, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("validate-", path, StringComparison.Ordinal);
        Assert.NotEqual(StagingPaths.ForRun(runGuid), path);
    }

    // ---------------------------------------------------------------------------------------

    /// <summary>Builds the output the query script produces on a target.</summary>
    private static string TargetState(
        string agent = "Quest Change Auditor Agent (x64)",
        string version = "7.4.0.20",
        string productCode = "{abc}",
        string mode = "1",
        string build = "26100",
        string osName = "Windows Server 2025 Standard") =>
        (agent.Equals("none", StringComparison.OrdinalIgnoreCase)
            ? "AGENT|none"
            : $"AGENT|{agent}|{version}|{productCode}") +
        $"\r\nMODE|{mode}\r\nOS|{build}|{osName}";

    private static async Task<ValidationRunSummary> RunAsync(
        SimulatedTransport transport,
        IReadOnlyList<DeploymentTarget> targets,
        int maxParallel = 5,
        IReadOnlyDictionary<string, int>? activeDcsPerSite = null,
        string? logDirectory = null,
        bool cloudMode = true)
    {
        var directory = logDirectory ?? CreateTempDirectory();
        try
        {
            var request = ValidationRequest.Create(
                Package,
                targets,
                @"CORP\admin",
                directory,
                cloudMode: cloudMode,
                maxParallel: maxParallel,
                activeDcsPerSite: activeDcsPerSite ??
                    new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase) { ["HQ"] = 40 });

            return await new ValidationRunner(transport)
                .ValidateAsync(request, null, CancellationToken.None);
        }
        finally
        {
            if (logDirectory is null)
            {
                Cleanup(directory);
            }
        }
    }

    private static string CreateTempDirectory()
    {
        var directory = Path.Combine(Path.GetTempPath(), "had-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        return directory;
    }

    private static void Cleanup(string directory)
    {
        try
        {
            Directory.Delete(directory, recursive: true);
        }
        catch (IOException)
        {
        }
    }
}

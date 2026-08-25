using HybridAgentDeploy.Core.Configuration;
using HybridAgentDeploy.Core.Deployment;
using HybridAgentDeploy.Core.Models;

namespace HybridAgentDeploy.Cli.Commands;

/// <summary>
/// <c>deploy</c> and <c>test-connectivity</c> (PRD 9, 11).
/// </summary>
internal static class DeployCommand
{
    /// <summary>
    /// Deploys the agent to the selected domain controllers.
    /// </summary>
    /// <remarks>
    /// Everything consequential happens in Core. This method resolves the selection, runs the
    /// PRD 11 checklist, enforces the <c>--confirm</c> gate, and translates the outcome into a
    /// process exit code — nothing more.
    /// </remarks>
    public static async Task<int> RunAsync(
        AppServices services,
        Output output,
        DeployOptions options,
        CancellationToken cancellationToken)
    {
        var selection = await services.TargetSelector
            .ResolveAsync(options.Tags, options.Hosts, options.All, cancellationToken)
            .ConfigureAwait(false);

        ReportSelectionProblems(output, selection);

        var logDirectory = options.LogDirectory ?? AppConfigurationLoader.RunLogDirectory(
            services.Configuration.LogRootPath, Guid.NewGuid(), DateTimeOffset.UtcNow);

        // PRD 11: a single blocking checklist, every item evaluated, before anything starts.
        var preflight = await new PreflightValidator(services.MsiInspector, services.Resolver)
            .ValidateAsync(options.MsiPath, options.OrgId, selection.Targets, logDirectory, cancellationToken,
                cloudMode: !options.NoCloudMode)
            .ConfigureAwait(false);

        foreach (var check in preflight.Checks)
        {
            output.Diagnostic($"  [{(check.Passed ? "ok" : "FAIL")}] {check.Name}: {check.Detail}");
        }

        foreach (var warning in preflight.Warnings)
        {
            output.Warning(warning);
        }

        var status = await services.DomainControllers.GetStatusAsync(cancellationToken).ConfigureAwait(false);
        if (status.StalenessWarning(DateTimeOffset.UtcNow) is { } staleness)
        {
            output.Warning(staleness);
        }

        if (!preflight.Passed)
        {
            throw new CommandFailedException(
                ExitCode.InvalidArgumentsOrPreflight,
                "Pre-flight validation failed; nothing was deployed. " +
                string.Join(" ", preflight.Failures.Select(f => f.Detail)));
        }

        // PRD 9: deploy MUST refuse to run without --confirm when the resolved target count
        // exceeds 1. This is what stops a mistyped script pushing to an entire forest.
        if (selection.Targets.Count > 1 && !options.Confirm)
        {
            throw new CommandFailedException(
                ExitCode.InvalidArgumentsOrPreflight,
                $"This would deploy to {selection.Targets.Count} domain controllers " +
                $"({string.Join(", ", selection.Targets.Take(5).Select(t => t.Fqdn))}" +
                $"{(selection.Targets.Count > 5 ? ", ..." : string.Empty)}) with Org ID " +
                $"'{preflight.OrgId}'. Re-run with --confirm to proceed.");
        }

        using var credential = CredentialPrompt.Collect(options.UserName, output);

        var transport = new WinRmSmbTransport(
            new TransportOptions
            {
                Credential = credential,
                UseHttps = options.UseHttps,
            },
            services.Logger<WinRmSmbTransport>());

        var request = DeploymentRequest.Create(
            msi: preflight.Msi!,
            orgId: preflight.OrgId!,
            targets: selection.Targets,
            operatorAccount: credential?.AccountName ?? OperatorCredential.CurrentWindowsAccountName(),
            logDirectory: logDirectory,
            cloudMode: !options.NoCloudMode,
            maxParallel: options.MaxParallel,
            timeoutMinutes: options.TimeoutMinutes,
            activeDcsPerSite: selection.ActiveDcsPerSite,
            inventoryStatus: status);

        if (request.ParallelismWasClamped)
        {
            // R7.1: clamped silently rather than refused, but never invisibly.
            output.Warning(
                $"--max-parallel was clamped to the hard ceiling of " +
                $"{DeploymentLimits.MaxConcurrencyCeiling}.");
        }

        output.Diagnostic(
            $"Deploying {preflight.Msi!.ProductVersion} to {selection.Targets.Count} target(s), " +
            $"up to {request.MaxParallel} at a time. Logs: {logDirectory}");

        // Ctrl+C stops new targets from starting; in-flight ones finish or time out, and their
        // staging directories are always cleaned up (PRD 8.4).
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        using var sigint = new ConsoleCancellation(cancellation, output);

        var orchestrator = new DeploymentOrchestrator(
            transport, services.Deployments, services.Logger<DeploymentOrchestrator>());

        // Progress always goes to stderr, so stdout stays parseable even in table mode.
        var progress = new StderrProgress(output);

        var summary = await orchestrator
            .DeployAsync(request, progress, cancellation.Token)
            .ConfigureAwait(false);

        WriteSummary(output, options.Format, summary, logDirectory);

        return ExitCodeFor(summary);
    }

    /// <summary>
    /// PRD 11: runs each target's pre-flight without deploying, so an operator can check
    /// reachability before their first real run.
    /// </summary>
    public static async Task<int> TestConnectivityAsync(
        AppServices services,
        Output output,
        IReadOnlyList<string> tags,
        IReadOnlyList<string> hosts,
        bool all,
        string? userName,
        bool useHttps,
        OutputFormat format,
        CancellationToken ct)
    {
        var selection = await services.TargetSelector.ResolveAsync(tags, hosts, all, ct).ConfigureAwait(false);
        ReportSelectionProblems(output, selection);

        if (selection.Targets.Count == 0)
        {
            throw new CommandFailedException(
                ExitCode.InvalidArgumentsOrPreflight,
                "No domain controllers were selected. Choose targets by tag, by host, or with --all.");
        }

        using var credential = CredentialPrompt.Collect(userName, output);

        var transport = new WinRmSmbTransport(
            new TransportOptions { Credential = credential, UseHttps = useHttps },
            services.Logger<WinRmSmbTransport>());

        var results = new List<(DeploymentTarget Target, PreflightResult Result)>();

        // Deliberately serial. This is a diagnostic command run against Tier 0 hosts, and
        // finishing a few seconds sooner is worth less than output that arrives in a
        // predictable order.
        foreach (var target in selection.Targets)
        {
            ct.ThrowIfCancellationRequested();
            output.Diagnostic($"Checking {target.Fqdn}...");
            results.Add((target, await transport.PreflightAsync(target.Fqdn, ct).ConfigureAwait(false)));
        }

        if (format == OutputFormat.Json)
        {
            output.Json(results.Select(r => new
            {
                fqdn = r.Target.Fqdn,
                site = r.Target.SiteName,
                reachable = r.Result.Succeeded,
                error_category = r.Result.ErrorCategory?.ToString(),
                detail = r.Result.ErrorDetail,
            }).ToList());
        }
        else
        {
            var cells = results.Select(r => (IReadOnlyList<string?>)
            [
                r.Target.Fqdn,
                r.Target.SiteName,
                r.Result.Succeeded ? "reachable" : "UNREACHABLE",
                r.Result.ErrorDetail,
            ]).ToList();

            if (format == OutputFormat.Csv)
            {
                output.Csv(["FQDN", "SITE", "RESULT", "DETAIL"], cells);
            }
            else
            {
                output.Table(["FQDN", "SITE", "RESULT", "DETAIL"], cells);
            }
        }

        var unreachable = results.Count(r => !r.Result.Succeeded);
        output.Diagnostic($"{results.Count - unreachable} of {results.Count} reachable.");

        // A target that cannot be reached is a real finding a script should act on, so it is
        // reported as a failure rather than as a successful check that happened to say no.
        return unreachable == 0 ? ExitCode.Success : ExitCode.CompletedWithFailures;
    }

    private static void ReportSelectionProblems(Output output, TargetSelection selection)
    {
        foreach (var tag in selection.UnknownTags)
        {
            output.Warning(
                $"No tag named '{tag}' exists, so it selected nothing. Check the spelling with " +
                "`hybridagentdeploy list`.");
        }

        foreach (var host in selection.UnknownHosts)
        {
            output.Warning(
                $"'{host}' is not a domain controller in the inventory, so it selected nothing. " +
                "Run `enumerate` if it is new.");
        }
    }

    private static void WriteSummary(
        Output output,
        OutputFormat format,
        DeploymentRunSummary summary,
        string logDirectory)
    {
        if (format == OutputFormat.Json)
        {
            output.Json(new
            {
                run_guid = summary.RunGuid.ToString("D"),
                started_utc = Core.UtcTimestamp.Format(summary.StartedUtc),
                completed_utc = Core.UtcTimestamp.Format(summary.CompletedUtc),
                target_count = summary.Outcomes.Count,
                success_count = summary.SuccessCount,
                failure_count = summary.FailureCount,
                skipped_count = summary.SkippedCount,
                was_halted = summary.WasHalted,
                halt_reason = summary.HaltReason,
                was_cancelled = summary.WasCancelled,
                log_directory = logDirectory,
                command = summary.CommandTemplate,
                results = summary.Outcomes
                    .OrderBy(o => o.Target.Fqdn, StringComparer.OrdinalIgnoreCase)
                    .Select(o => new
                    {
                        fqdn = o.Target.Fqdn,
                        site = o.Target.SiteName,
                        outcome = o.Outcome.ToString(),
                        stage = o.Stage?.ToString(),
                        exit_code = o.ExitCode,
                        error_category = o.ErrorCategory?.ToString(),
                        error_detail = o.ErrorDetail,
                        attempt_count = o.AttemptCount,
                        duration_seconds = Math.Round(o.Duration.TotalSeconds, 1),
                        msi_log_path = o.MsiLogPath,
                        requires_attention = o.RequiresProminentWarning,
                        warnings = o.Warnings,
                    }),
            });

            return;
        }

        var cells = summary.Outcomes
            .OrderBy(o => o.Target.Fqdn, StringComparer.OrdinalIgnoreCase)
            .Select(o => (IReadOnlyList<string?>)
            [
                o.Target.Fqdn,
                o.Outcome.ToString(),
                o.ExitCode?.ToString(),
                o.ErrorCategory?.ToString(),
                Math.Round(o.Duration.TotalSeconds, 1).ToString(System.Globalization.CultureInfo.InvariantCulture),
                o.ErrorDetail,
            ])
            .ToList();

        string[] headers = ["FQDN", "OUTCOME", "EXIT", "CATEGORY", "SECONDS", "DETAIL"];

        if (format == OutputFormat.Csv)
        {
            output.Csv(headers, cells);
        }
        else
        {
            output.Table(headers, cells);
        }

        output.Diagnostic(
            $"{summary.SuccessCount} succeeded, {summary.FailureCount} failed, " +
            $"{summary.SkippedCount} not attempted. Logs: {logDirectory}");

        foreach (var loud in summary.Outcomes.Where(o => o.RequiresProminentWarning))
        {
            output.Warning(
                $"{loud.Target.Fqdn}: exit code {loud.ExitCode} — a reboot was initiated on this " +
                "domain controller.");
        }

        if (summary.WasHalted)
        {
            output.Error($"Run halted: {summary.HaltReason}");
        }
    }

    internal static int ExitCodeFor(DeploymentRunSummary summary)
    {
        // Order matters. A halted run and a cancelled run both leave targets unattempted, but
        // a script should distinguish "your credentials are wrong" from "someone pressed
        // Ctrl+C", so the breaker is reported first.
        if (summary.WasHalted)
        {
            return ExitCode.HaltedByCircuitBreaker;
        }

        if (summary.WasCancelled)
        {
            return ExitCode.Cancelled;
        }

        return summary.FailureCount > 0 ? ExitCode.CompletedWithFailures : ExitCode.Success;
    }
}

/// <summary>Everything <c>deploy</c> needs, gathered from the parsed command line.</summary>
internal sealed record DeployOptions
{
    public required string MsiPath { get; init; }
    public required string? OrgId { get; init; }
    public IReadOnlyList<string> Tags { get; init; } = [];
    public IReadOnlyList<string> Hosts { get; init; } = [];
    public bool All { get; init; }
    public bool NoCloudMode { get; init; }
    public int MaxParallel { get; init; } = DeploymentLimits.MaxConcurrencyCeiling;
    public int TimeoutMinutes { get; init; } = DeploymentLimits.DefaultTimeoutMinutes;
    public string? LogDirectory { get; init; }
    public bool Confirm { get; init; }
    public string? UserName { get; init; }
    public bool UseHttps { get; init; }
    public OutputFormat Format { get; init; } = OutputFormat.Table;
}

/// <summary>
/// Reports per-target progress to stderr as targets change stage.
/// </summary>
/// <remarks>
/// Deployments take minutes, and an operator watching a scripted run needs to see it moving.
/// stderr keeps that away from stdout, which must stay parseable (PRD 9). Only stage
/// transitions and completions are reported; every intermediate report would bury them.
/// </remarks>
internal sealed class StderrProgress(Output output) : IProgress<DeploymentProgress>
{
    private readonly Dictionary<string, DeploymentStage?> _lastStage = [];
    private readonly Lock _gate = new();

    public void Report(DeploymentProgress value)
    {
        lock (_gate)
        {
            if (value.State == TargetProgressState.Completed)
            {
                output.Diagnostic(
                    $"  [{value.CompletedCount}/{value.TotalCount}] {value.Target.Fqdn}: done " +
                    $"({value.SuccessCount} ok, {value.FailureCount} failed, " +
                    $"{value.OccupiedSlots} slot(s) busy)");
                return;
            }

            if (value.State != TargetProgressState.Running || value.Stage is null)
            {
                return;
            }

            if (_lastStage.TryGetValue(value.Target.Fqdn, out var previous) && previous == value.Stage)
            {
                return;
            }

            _lastStage[value.Target.Fqdn] = value.Stage;
            output.Diagnostic($"  {value.Target.Fqdn}: {value.Stage.ToString()!.ToLowerInvariant()}");
        }
    }
}

/// <summary>
/// Turns Ctrl+C into a cancellation request rather than an abrupt process exit.
/// </summary>
/// <remarks>
/// PRD 8.4: cancelling stops new targets from starting and lets in-flight ones finish, so an
/// msiexec running on a domain controller is never abandoned without cleanup being attempted.
/// Letting the default Ctrl+C handler kill the process would do exactly that.
/// </remarks>
internal sealed class ConsoleCancellation : IDisposable
{
    private readonly ConsoleCancelEventHandler _handler;

    public ConsoleCancellation(CancellationTokenSource source, Output output)
    {
        _handler = (_, e) =>
        {
            // Cancel the default terminate-immediately behaviour.
            e.Cancel = true;

            output.Diagnostic(
                "Cancellation requested. No further domain controllers will be started; those " +
                "already in progress will finish or time out, and their staging directories " +
                "will be cleaned up. Press Ctrl+C again to abandon them, which is not advised.");

            source.Cancel();
        };

        Console.CancelKeyPress += _handler;
    }

    public void Dispose() => Console.CancelKeyPress -= _handler;
}

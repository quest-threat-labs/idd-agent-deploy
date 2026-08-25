using System.Globalization;
using System.Text;
using HybridAgentDeploy.Core.Deployment;

namespace HybridAgentDeploy.Core.Logging;

/// <summary>
/// Writes <c>validation.log</c> and <c>results.csv</c> for one validation run.
/// </summary>
/// <remarks>
/// <para>
/// A validation gets its own directory and leaves no row in <c>deployment_run</c>. Nothing was
/// deployed, and a history that showed otherwise would misreport what ran against the
/// customer's domain controllers — the one thing this tool's records exist to get right.
/// </para>
/// <para>
/// The file is named <c>validation.log</c> rather than <c>run.log</c> so that a directory
/// opened months later identifies itself from its contents, not just from the folder name.
/// </para>
/// <para>
/// PRD 12.3 and SEC1: nothing written here may contain passwords or credential material.
/// Account names only.
/// </para>
/// </remarks>
public sealed class ValidationLogWriter : IAsyncDisposable
{
    private readonly string _logPath;
    private readonly string _resultsCsvPath;
    private readonly SemaphoreSlim _writeGate = new(1, 1);

    private ValidationLogWriter(string directory)
    {
        Directory = directory;
        _logPath = Path.Combine(directory, "validation.log");
        _resultsCsvPath = Path.Combine(directory, "results.csv");
    }

    public string Directory { get; }

    public string LogPath => _logPath;

    public string ResultsCsvPath => _resultsCsvPath;

    public static Task<ValidationLogWriter> CreateAsync(string directory, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        System.IO.Directory.CreateDirectory(directory);
        return Task.FromResult(new ValidationLogWriter(directory));
    }

    /// <summary>
    /// Writes the header: who ran it, against what, with which package, and — stated plainly —
    /// that nothing was installed.
    /// </summary>
    public async Task WriteHeaderAsync(
        ValidationRequest request,
        Guid runGuid,
        DateTimeOffset startedUtc,
        string probeDirectory,
        SiteConcurrencyGuard siteGuard,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(siteGuard);

        var builder = new StringBuilder();

        builder.AppendLine("Hybrid Audit Agent Deployment Utility - validation log");
        builder.AppendLine("=====================================================");
        builder.AppendLine();
        builder.AppendLine("NOTHING WAS INSTALLED BY THIS RUN. Validation confirms each domain");
        builder.AppendLine("controller is reachable, that the running account can write to it and");
        builder.AppendLine("start a process on it, and what agent it already has. msiexec was not");
        builder.AppendLine("run and the package was not copied to any target.");
        builder.AppendLine();
        builder.AppendLine($"Run GUID          : {runGuid:D}");
        builder.AppendLine($"Started           : {UtcTimestamp.Format(startedUtc)}");
        builder.AppendLine($"Started (local)   : {startedUtc.ToLocalTime():yyyy-MM-dd HH:mm:ss} local time");
        builder.AppendLine($"Operator account  : {request.OperatorAccount}");
        builder.AppendLine($"Tool version      : {ToolVersion}");
        builder.AppendLine();
        builder.AppendLine("Package the installed agents are compared against:");
        builder.AppendLine($"  File            : {request.Msi.FilePath}");
        builder.AppendLine($"  Product name    : {request.Msi.ProductName ?? "(not present)"}");
        builder.AppendLine($"  Product version : {request.Msi.ProductVersion}");
        builder.AppendLine($"  SHA-256         : {request.Msi.Sha256}");
        builder.AppendLine();
        builder.AppendLine($"Probe directory   : {probeDirectory}");
        builder.AppendLine(
            "                    A few kilobytes are written here on each target and removed");
        builder.AppendLine(
            "                    again. The path is recorded before it is created (NFR7) so a");
        builder.AppendLine(
            "                    run killed part-way leaves something identifiable.");
        builder.AppendLine();
        builder.AppendLine($"Max parallel      : {request.MaxParallel}");

        if (request.ParallelismWasClamped)
        {
            builder.AppendLine(
                "                    (the requested value exceeded the hard ceiling of " +
                $"{DeploymentLimits.MaxConcurrencyCeiling} and was clamped)");
        }

        builder.AppendLine($"Per-target timeout: {request.PerTargetTimeout.TotalMinutes:0} minutes");
        builder.AppendLine($"Pacing            : {siteGuard.DescribePacing(request.Targets, request.MaxParallel)}");

        if (request.InventoryStatus is { } inventory)
        {
            builder.AppendLine($"AD last enumerated: {inventory.Describe(startedUtc)}");

            if (inventory.StalenessWarning(startedUtc) is { } warning)
            {
                builder.AppendLine($"                    WARNING {warning}");
            }
        }

        builder.AppendLine();
        builder.AppendLine($"Targets ({request.Targets.Count}):");

        foreach (var target in request.Targets.OrderBy(t => t.Fqdn, StringComparer.OrdinalIgnoreCase))
        {
            var site = string.IsNullOrWhiteSpace(target.SiteName) ? "(no site recorded)" : target.SiteName;
            builder.AppendLine($"  {target.Fqdn}  [site: {site}]");
        }

        builder.AppendLine();
        builder.AppendLine("Progress");
        builder.AppendLine("--------");

        await AppendAsync(_logPath, builder.ToString(), ct).ConfigureAwait(false);
    }

    /// <summary>Appends one timestamped line. Safe to call from concurrent operations.</summary>
    public async Task WriteLineAsync(string message, CancellationToken ct) =>
        await AppendAsync(
            _logPath,
            $"{UtcTimestamp.Now()}  {message}{Environment.NewLine}",
            ct).ConfigureAwait(false);

    /// <summary>
    /// Writes one target's full checklist, passes included.
    /// </summary>
    /// <remarks>
    /// Successful checks are written out rather than summarised as "OK". The point of running
    /// this before a deployment is to be able to show, afterwards, what was confirmed and when
    /// — a log that records only failures cannot distinguish "we checked and it was fine" from
    /// "we never checked".
    /// </remarks>
    public async Task WriteOutcomeAsync(ValidationOutcome outcome, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(outcome);

        var builder = new StringBuilder();
        builder.AppendLine(
            $"{UtcTimestamp.Now()}  {outcome.Target.Fqdn}: " +
            $"{(outcome.Passed ? "READY" : "PROBLEM")} - {outcome.Summary}");

        foreach (var check in outcome.Checks)
        {
            builder.AppendLine($"        [{(check.Passed ? "pass" : "FAIL")}] {check.Name}: {check.Detail}");
        }

        await AppendAsync(_logPath, builder.ToString(), ct).ConfigureAwait(false);
    }

    public async Task WriteSummaryAsync(ValidationRunSummary summary, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(summary);

        var builder = new StringBuilder();

        builder.AppendLine();
        builder.AppendLine("Summary");
        builder.AppendLine("-------");
        builder.AppendLine($"Completed         : {UtcTimestamp.Format(summary.CompletedUtc)}");
        builder.AppendLine($"Completed (local) : {summary.CompletedUtc.ToLocalTime():yyyy-MM-dd HH:mm:ss} local time");
        builder.AppendLine($"Elapsed           : {summary.CompletedUtc - summary.StartedUtc:hh\\:mm\\:ss}");
        builder.AppendLine($"Targets           : {summary.Outcomes.Count}");
        builder.AppendLine($"Ready             : {summary.ReadyCount}");
        builder.AppendLine($"Problems          : {summary.ProblemCount}");

        if (summary.WouldBeRefusedCount > 0)
        {
            builder.AppendLine($"Package refused   : {summary.WouldBeRefusedCount}");
            builder.AppendLine(
                "                    These domain controllers are reachable and writable, but");
            builder.AppendLine(
                "                    already carry a newer agent. msiexec would return 1638.");
        }

        if (summary.WasCancelled)
        {
            builder.AppendLine();
            builder.AppendLine(
                "RUN CANCELLED BY OPERATOR. Targets already in flight finished and their probe " +
                "directories were removed; targets not yet started were not touched.");
        }

        var problems = summary.Outcomes.Where(o => !o.Passed).ToList();
        if (problems.Count > 0)
        {
            builder.AppendLine();
            builder.AppendLine("Not ready");
            foreach (var outcome in problems.OrderBy(o => o.Target.Fqdn, StringComparer.OrdinalIgnoreCase))
            {
                builder.AppendLine($"  {outcome.Target.Fqdn}: {outcome.Summary}");
            }
        }

        await AppendAsync(_logPath, builder.ToString(), ct).ConfigureAwait(false);
    }

    /// <summary>One row per target, for triaging a large forest outside this tool.</summary>
    public async Task WriteResultsCsvAsync(ValidationRunSummary summary, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(summary);

        var builder = new StringBuilder();

        builder.AppendLine(
            "fqdn,site,verdict,predicted_action,installed_version,installed_product_code," +
            "error_category,started_utc,completed_utc,duration_seconds,failed_checks,detail");

        foreach (var outcome in summary.Outcomes.OrderBy(o => o.Target.Fqdn, StringComparer.OrdinalIgnoreCase))
        {
            builder.AppendLine(string.Join(',', new[]
            {
                Csv(outcome.Target.Fqdn),
                Csv(outcome.Target.SiteName),
                Csv(outcome.Passed ? "ready" : "problem"),
                Csv(outcome.Predicted.ToString()),
                Csv(outcome.InstalledAgent?.Version),
                Csv(outcome.InstalledAgent?.ProductCode),
                Csv(outcome.ErrorCategory?.ToString()),
                Csv(UtcTimestamp.Format(outcome.StartedUtc)),
                Csv(UtcTimestamp.Format(outcome.CompletedUtc)),
                Csv(outcome.Duration.TotalSeconds.ToString("0.0", CultureInfo.InvariantCulture)),
                Csv(string.Join("; ", outcome.Failures.Select(f => f.Name))),
                Csv(outcome.Summary),
            }));
        }

        await AppendAsync(_resultsCsvPath, builder.ToString(), ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Escapes a CSV field. Check details are free text containing commas and quotes, and a
    /// broken CSV is worthless to the operator trying to triage a sixty-DC forest in Excel.
    /// </summary>
    private static string Csv(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return string.Empty;
        }

        var needsQuoting = value.AsSpan().IndexOfAny(",\"\r\n") >= 0;
        if (!needsQuoting)
        {
            return value;
        }

        return "\"" + value.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";
    }

    private async Task AppendAsync(string path, string content, CancellationToken ct)
    {
        await _writeGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await File.AppendAllTextAsync(path, content, Encoding.UTF8, ct).ConfigureAwait(false);
        }
        finally
        {
            _writeGate.Release();
        }
    }

    private static string ToolVersion =>
        typeof(ValidationLogWriter).Assembly.GetName().Version?.ToString() ?? "unknown";

    public ValueTask DisposeAsync()
    {
        _writeGate.Dispose();
        return ValueTask.CompletedTask;
    }
}

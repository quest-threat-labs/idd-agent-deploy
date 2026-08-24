using System.Globalization;
using System.Text;
using HybridAgentDeploy.Core.Deployment;

namespace HybridAgentDeploy.Core.Logging;

/// <summary>
/// Writes the per-run artefacts of PRD 12.1: <c>run.log</c> and <c>results.csv</c>.
/// </summary>
/// <remarks>
/// <para>
/// Distinct from <see cref="FileLoggerProvider"/>, which is the utility's own diagnostic log.
/// This one has prescribed content (PRD 12.2) and exists to be read by a customer
/// administrator, and possibly by a change-control board.
/// </para>
/// <para>
/// Lines are flushed as they are written rather than buffered to the end of the run. A run
/// killed mid-flight must still leave a log that says what had happened, and that is exactly
/// the run whose log matters most.
/// </para>
/// <para>
/// PRD 12.3 and SEC1: nothing written here may contain passwords, credential material, or
/// Kerberos tickets. Account names only.
/// </para>
/// </remarks>
public sealed class RunLogWriter : IAsyncDisposable
{
    private readonly string _runLogPath;
    private readonly string _resultsCsvPath;
    private readonly SemaphoreSlim _writeGate = new(1, 1);

    private RunLogWriter(string directory)
    {
        Directory = directory;
        _runLogPath = Path.Combine(directory, "run.log");
        _resultsCsvPath = Path.Combine(directory, "results.csv");
    }

    public string Directory { get; }

    public string RunLogPath => _runLogPath;

    public string ResultsCsvPath => _resultsCsvPath;

    public static Task<RunLogWriter> CreateAsync(string directory, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        System.IO.Directory.CreateDirectory(directory);
        return Task.FromResult(new RunLogWriter(directory));
    }

    /// <summary>
    /// Writes the header block. PRD 12.2 prescribes its content: timestamps in UTC and local,
    /// operator account, tool version, MSI identity and hash, Org ID, cloud-mode flag, the
    /// exact command template, the resolved target list with sites, and max-parallel in
    /// effect.
    /// </summary>
    public async Task WriteHeaderAsync(
        DeploymentRequest request,
        Guid runGuid,
        DateTimeOffset startedUtc,
        string commandTemplate,
        SiteConcurrencyGuard siteGuard,
        CancellationToken ct)
    {
        var builder = new StringBuilder();

        builder.AppendLine("Hybrid Audit Agent Deployment Utility - run log");
        builder.AppendLine("==============================================");
        builder.AppendLine();
        builder.AppendLine($"Run GUID          : {runGuid:D}");
        builder.AppendLine($"Started           : {UtcTimestamp.Format(startedUtc)}");
        builder.AppendLine($"Started (local)   : {startedUtc.ToLocalTime():yyyy-MM-dd HH:mm:ss} local time");
        builder.AppendLine($"Operator account  : {request.OperatorAccount}");
        builder.AppendLine($"Tool version      : {ToolVersion}");
        builder.AppendLine();
        builder.AppendLine($"MSI file          : {request.Msi.FileName}");
        builder.AppendLine($"MSI path          : {request.Msi.FilePath}");
        builder.AppendLine($"Product name      : {request.Msi.ProductName ?? "(not present)"}");
        builder.AppendLine($"Product version   : {request.Msi.ProductVersion}");
        builder.AppendLine($"Product code      : {request.Msi.ProductCode ?? "(not present)"}");
        builder.AppendLine($"MSI SHA-256       : {request.Msi.Sha256}");
        builder.AppendLine();
        builder.AppendLine($"Org ID            : {request.OrgId}");
        builder.AppendLine($"Cloud mode (SG=1) : {(request.CloudMode ? "on" : "off")}");
        builder.AppendLine($"Max parallel      : {request.MaxParallel}");

        if (request.ParallelismWasClamped)
        {
            // R7.1: clamping is silent to the operator but must be visible in the record.
            builder.AppendLine(
                "                    (the requested value exceeded the hard ceiling of " +
                $"{DeploymentLimits.MaxConcurrencyCeiling} and was clamped)");
        }

        builder.AppendLine($"Per-target timeout: {request.PerTargetTimeout.TotalMinutes:0} minutes");
        builder.AppendLine();
        builder.AppendLine("Command template (as executed, with the Org ID in place):");
        builder.AppendLine($"  {commandTemplate}");
        builder.AppendLine();
        builder.AppendLine($"Targets ({request.Targets.Count}):");

        foreach (var target in request.Targets.OrderBy(t => t.Fqdn, StringComparer.OrdinalIgnoreCase))
        {
            var site = string.IsNullOrWhiteSpace(target.SiteName) ? "(no site recorded)" : target.SiteName;
            builder.AppendLine($"  {target.Fqdn}  [site: {site}]");
        }

        builder.AppendLine();

        // The site guard is invisible in the UI but changes how long a run takes, so an
        // operator watching a serialised run needs to be able to find out why here rather
        // than concluding the tool has hung.
        builder.AppendLine("Site concurrency limits in effect (PRD R7.2):");

        var sites = request.Targets
            .Select(t => string.IsNullOrWhiteSpace(t.SiteName) ? SiteConcurrencyGuard.UnknownSiteKey : t.SiteName!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(s => s, StringComparer.OrdinalIgnoreCase);

        foreach (var site in sites)
        {
            var limit = siteGuard.LimitFor(site);
            builder.Append(CultureInfo.InvariantCulture, $"  {site}: at most {limit} at a time");

            if (string.Equals(site, SiteConcurrencyGuard.UnknownSiteKey, StringComparison.OrdinalIgnoreCase))
            {
                builder.Append(
                    " - these targets have no Active Directory site recorded, which is normal " +
                    "for hosts added by file import. They share one bucket and are therefore " +
                    "deployed strictly one at a time regardless of the max-parallel setting. " +
                    "Enumerate from Active Directory to give them site information and let them " +
                    "run in parallel.");
            }

            builder.AppendLine();
        }

        builder.AppendLine();
        builder.AppendLine("Progress");
        builder.AppendLine("--------");

        await AppendAsync(_runLogPath, builder.ToString(), ct).ConfigureAwait(false);
    }

    /// <summary>Appends one timestamped line. Safe to call from concurrent operations.</summary>
    public async Task WriteLineAsync(string message, CancellationToken ct) =>
        await AppendAsync(
            _runLogPath,
            $"{UtcTimestamp.Now()}  {message}{Environment.NewLine}",
            ct).ConfigureAwait(false);

    public async Task WriteSummaryAsync(DeploymentRunSummary summary, CancellationToken ct)
    {
        var builder = new StringBuilder();

        builder.AppendLine();
        builder.AppendLine("Summary");
        builder.AppendLine("-------");
        builder.AppendLine($"Completed         : {UtcTimestamp.Format(summary.CompletedUtc)}");
        builder.AppendLine($"Completed (local) : {summary.CompletedUtc.ToLocalTime():yyyy-MM-dd HH:mm:ss} local time");
        builder.AppendLine($"Elapsed           : {summary.CompletedUtc - summary.StartedUtc:hh\\:mm\\:ss}");
        builder.AppendLine($"Targets           : {summary.Outcomes.Count}");
        builder.AppendLine($"Succeeded         : {summary.SuccessCount}");
        builder.AppendLine($"Failed            : {summary.FailureCount}");
        builder.AppendLine($"Not attempted     : {summary.SkippedCount}");

        if (summary.WasHalted)
        {
            builder.AppendLine();
            builder.AppendLine("RUN HALTED BY CIRCUIT BREAKER");
            builder.AppendLine($"  {summary.HaltReason}");
        }

        if (summary.WasCancelled)
        {
            builder.AppendLine();
            builder.AppendLine(
                "RUN CANCELLED BY OPERATOR. Targets already in flight were allowed to finish " +
                "and their staging directories were cleaned up; targets not yet started were " +
                "not touched.");
        }

        var loud = summary.Outcomes.Where(o => o.RequiresProminentWarning).ToList();
        if (loud.Count > 0)
        {
            builder.AppendLine();
            builder.AppendLine("REQUIRES ATTENTION");
            foreach (var outcome in loud)
            {
                builder.AppendLine($"  {outcome.Target.Fqdn}: exit code {outcome.ExitCode} - a reboot was initiated on this domain controller.");
            }
        }

        var warnings = summary.Outcomes.SelectMany(o => o.Warnings.Select(w => (o.Target.Fqdn, w))).ToList();
        if (warnings.Count > 0)
        {
            builder.AppendLine();
            builder.AppendLine("Warnings");
            foreach (var (fqdn, warning) in warnings)
            {
                builder.AppendLine($"  {fqdn}: {warning}");
            }
        }

        await AppendAsync(_runLogPath, builder.ToString(), ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Writes <c>results.csv</c>: one row per target with outcome, exit code, timings, and
    /// error category (PRD 12.1).
    /// </summary>
    public async Task WriteResultsCsvAsync(DeploymentRunSummary summary, CancellationToken ct)
    {
        var builder = new StringBuilder();

        builder.AppendLine(
            "fqdn,site,outcome,stage,exit_code,error_category,attempt_count," +
            "started_utc,completed_utc,duration_seconds,msi_log_path,error_detail");

        foreach (var outcome in summary.Outcomes.OrderBy(o => o.Target.Fqdn, StringComparer.OrdinalIgnoreCase))
        {
            builder.AppendLine(string.Join(',', new[]
            {
                Csv(outcome.Target.Fqdn),
                Csv(outcome.Target.SiteName),
                Csv(outcome.Outcome.ToString()),
                Csv(outcome.Stage?.ToString()),
                Csv(outcome.ExitCode?.ToString(CultureInfo.InvariantCulture)),
                Csv(outcome.ErrorCategory?.ToString()),
                Csv(outcome.AttemptCount.ToString(CultureInfo.InvariantCulture)),
                Csv(UtcTimestamp.Format(outcome.StartedUtc)),
                Csv(UtcTimestamp.Format(outcome.CompletedUtc)),
                Csv(outcome.Duration.TotalSeconds.ToString("0.0", CultureInfo.InvariantCulture)),
                Csv(outcome.MsiLogPath),
                Csv(outcome.ErrorDetail),
            }));
        }

        await AppendAsync(_resultsCsvPath, builder.ToString(), ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Escapes a CSV field. The error detail is free text containing commas, quotes, and
    /// occasionally newlines, and a broken CSV is worthless to the operator trying to triage
    /// a sixty-DC run in Excel.
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
        typeof(RunLogWriter).Assembly.GetName().Version?.ToString() ?? "unknown";

    public ValueTask DisposeAsync()
    {
        _writeGate.Dispose();
        return ValueTask.CompletedTask;
    }
}

using HybridAgentDeploy.Core;
using HybridAgentDeploy.Core.Deployment;
using HybridAgentDeploy.Core.Discovery;
using HybridAgentDeploy.Core.Models;

namespace HybridAgentDeploy.Cli.Commands;

/// <summary>
/// <c>enumerate</c>, <c>import</c>, <c>list</c> and <c>inspect-msi</c> (PRD 9).
/// </summary>
internal static class InventoryCommands
{
    /// <summary>
    /// Discovers domain controllers from Active Directory and optionally tags them all.
    /// </summary>
    /// <remarks>
    /// Enumeration is the only way a DC enters the inventory, and it is additive: a controller
    /// that has disappeared is deactivated, keeping its tags and history (PRD 6.2).
    /// </remarks>
    public static async Task<int> EnumerateAsync(
        AppServices services,
        Output output,
        string? domain,
        IReadOnlyList<string> tagNames,
        string? userName,
        OutputFormat format,
        CancellationToken ct)
    {
        using var credential = CredentialPrompt.Collect(userName, output);

        output.Diagnostic(domain is null
            ? "Enumerating domain controllers across the current forest..."
            : $"Enumerating domain controllers in {domain}...");

        var enumerator = new ActiveDirectoryEnumerator(services.Logger<ActiveDirectoryEnumerator>());
        var result = await enumerator.EnumerateAsync(domain, credential, ct).ConfigureAwait(false);

        foreach (var warning in result.Warnings)
        {
            output.Warning(warning);
        }

        var ids = new List<long>(result.DomainControllers.Count);
        foreach (var dc in result.DomainControllers)
        {
            ids.Add(await services.DomainControllers.UpsertAsync(dc, ct).ConfigureAwait(false));
        }

        var deactivated = await services.DomainControllers
            .DeactivateEnumeratedExceptAsync(result.DomainControllers.Select(d => d.Fqdn), ct)
            .ConfigureAwait(false);

        foreach (var fqdn in deactivated)
        {
            output.Warning(
                $"{fqdn} was not returned by this enumeration and has been marked inactive. Its " +
                "tags and deployment history are retained.");
        }

        foreach (var tagName in tagNames)
        {
            var tag = await services.Tags.GetOrCreateAsync(tagName, null, ct).ConfigureAwait(false);
            await services.Tags.ApplyTagAsync(tag.Id, ids, ct).ConfigureAwait(false);
        }

        if (format == OutputFormat.Json)
        {
            output.Json(new
            {
                domains_searched = result.DomainsSearched,
                discovered = result.DomainControllers.Count,
                deactivated,
                tags_applied = tagNames,
                warnings = result.Warnings,
                domain_controllers = result.DomainControllers.Select(d => new
                {
                    fqdn = d.Fqdn,
                    domain = d.Domain,
                    site = d.SiteName,
                    os_version = d.OsVersion,
                    is_read_only = d.IsReadOnly,
                    is_global_catalog = d.IsGlobalCatalog,
                }),
            });
        }
        else
        {
            output.Line(
                $"Discovered {result.DomainControllers.Count} domain controller(s) across " +
                $"{result.DomainsSearched.Count} domain(s).");

            if (tagNames.Count > 0)
            {
                output.Line($"Applied tag(s): {string.Join(", ", tagNames)}");
            }
        }

        return ExitCode.Success;
    }

    /// <summary>
    /// Applies tags to domain controllers named in a text file.
    /// </summary>
    /// <remarks>
    /// Import selects and tags; it never adds hosts. See <see cref="FileImportService"/> for
    /// why that departs from the literal wording of PRD 6.2.
    /// </remarks>
    public static async Task<int> ImportAsync(
        AppServices services,
        Output output,
        string filePath,
        IReadOnlyList<string> tagNames,
        bool dryRun,
        OutputFormat format,
        CancellationToken ct)
    {
        var service = new FileImportService(
            services.DomainControllers,
            services.Tags,
            services.Resolver,
            services.Logger<FileImportService>());

        ImportReport report;
        try
        {
            report = await service.ImportFileAsync(filePath, tagNames, dryRun, ct).ConfigureAwait(false);
        }
        catch (FileNotFoundException ex)
        {
            throw new CommandFailedException(ExitCode.InvalidArgumentsOrPreflight, ex.Message);
        }
        catch (InventoryNotEnumeratedException ex)
        {
            throw new CommandFailedException(ExitCode.InvalidArgumentsOrPreflight, ex.Message);
        }

        foreach (var duplicate in report.Duplicates)
        {
            output.Warning($"line {duplicate.LineNumber}: '{duplicate.RawValue}' is a duplicate and was ignored.");
        }

        foreach (var unmatched in report.Unmatched)
        {
            output.Warning($"line {unmatched.LineNumber}: {unmatched.Reason}");
        }

        if (format == OutputFormat.Json)
        {
            output.Json(new
            {
                dry_run = report.DryRun,
                tags_applied = report.TagsApplied,
                matched = report.Matched.Select(m => new
                {
                    line = m.LineNumber,
                    entry = m.RawValue,
                    fqdn = m.ResolvedFqdn,
                }),
                unmatched = report.Unmatched.Select(u => new
                {
                    line = u.LineNumber,
                    entry = u.RawValue,
                    reason = u.Reason,
                }),
                duplicates = report.Duplicates.Select(d => new { line = d.LineNumber, entry = d.RawValue }),
            });
        }
        else
        {
            var verb = report.DryRun ? "would be tagged" : "tagged";
            output.Line($"{report.Matched.Count} domain controller(s) {verb}; {report.Unmatched.Count} unmatched.");

            if (report.DryRun)
            {
                output.Diagnostic("Dry run: nothing was written.");
            }
        }

        // Unmatched entries are reported but are not a failure: the tags that could be applied
        // were applied, and the operator is told exactly which lines to fix.
        return ExitCode.Success;
    }

    /// <summary>Lists the inventory with its last-known deployment state (PRD 6.1, 8.1).</summary>
    public static async Task<int> ListAsync(
        AppServices services,
        Output output,
        string? tagFilter,
        string? siteFilter,
        OutputFormat format,
        CancellationToken ct)
    {
        var all = await services.DomainControllers.GetAllAsync(includeInactive: false, ct).ConfigureAwait(false);
        var tagsByDc = await services.Tags.GetTagsByDcAsync(ct).ConfigureAwait(false);
        var lastDeployments = await services.DomainControllers.GetLastDeploymentsAsync(ct).ConfigureAwait(false);

        var rows = all.AsEnumerable();

        if (!string.IsNullOrWhiteSpace(tagFilter))
        {
            var tagged = (await services.Tags.GetDcIdsWithTagAsync(tagFilter, ct).ConfigureAwait(false)).ToHashSet();
            rows = rows.Where(dc => tagged.Contains(dc.Id));
        }

        if (!string.IsNullOrWhiteSpace(siteFilter))
        {
            rows = rows.Where(dc => string.Equals(dc.SiteName, siteFilter, StringComparison.OrdinalIgnoreCase));
        }

        var selected = rows.ToList();

        if (format == OutputFormat.Json)
        {
            output.Json(selected.Select(dc =>
            {
                lastDeployments.TryGetValue(dc.Id, out var last);
                return new
                {
                    fqdn = dc.Fqdn,
                    domain = dc.Domain,
                    site = dc.SiteName,
                    os_version = dc.OsVersion,
                    is_read_only = dc.IsReadOnly,
                    is_global_catalog = dc.IsGlobalCatalog,
                    tags = tagsByDc.GetValueOrDefault(dc.Id, []),
                    last_deployed_version = last?.MsiProductVersion,
                    last_deployed_utc = last?.CompletedUtc,
                    last_outcome = last?.Outcome.ToString(),
                    last_exit_code = last?.ExitCode,
                };
            }).ToList());

            return ExitCode.Success;
        }

        string[] headers = ["FQDN", "SITE", "OS", "RODC", "TAGS", "LAST VERSION", "LAST DEPLOYED (UTC)", "OUTCOME"];

        var cells = selected.Select(dc =>
        {
            lastDeployments.TryGetValue(dc.Id, out var last);
            return (IReadOnlyList<string?>)
            [
                dc.Fqdn,
                dc.SiteName,
                dc.OsVersion,
                dc.IsReadOnly ? "yes" : "no",
                string.Join(",", tagsByDc.GetValueOrDefault(dc.Id, [])),
                last?.MsiProductVersion,
                Output.Timestamp(last?.CompletedUtc),
                last?.Outcome.ToString() ?? "never deployed",
            ];
        }).ToList();

        if (format == OutputFormat.Csv)
        {
            output.Csv(headers, cells);
        }
        else
        {
            output.Table(headers, cells);
            output.Diagnostic($"{selected.Count} domain controller(s).");

            var status = await services.DomainControllers.GetStatusAsync(ct).ConfigureAwait(false);
            if (status.StalenessWarning(DateTimeOffset.UtcNow) is { } warning)
            {
                output.Warning(warning);
            }
        }

        return ExitCode.Success;
    }

    /// <summary>Reads and reports an MSI's identity (PRD 5.5, 8.3).</summary>
    public static async Task<int> InspectMsiAsync(
        Output output,
        string msiPath,
        OutputFormat format,
        CancellationToken ct)
    {
        MsiPackageInfo msi;
        try
        {
            msi = await new Core.Msi.MsiInspector().InspectAsync(msiPath, ct).ConfigureAwait(false);
        }
        catch (Core.Msi.MsiInspectionException ex)
        {
            throw new CommandFailedException(ExitCode.InvalidArgumentsOrPreflight, ex.Message);
        }

        if (format == OutputFormat.Json)
        {
            output.Json(new
            {
                file_name = msi.FileName,
                file_path = msi.FilePath,
                file_size_bytes = msi.FileSizeBytes,
                sha256 = msi.Sha256,
                product_name = msi.ProductName,
                product_version = msi.ProductVersion,
                product_code = msi.ProductCode,
                upgrade_code = msi.UpgradeCode,
                looks_like_expected_product = msi.LooksLikeExpectedProduct,
            });
        }
        else
        {
            output.Table(
                ["PROPERTY", "VALUE"],
                [
                    ["File", msi.FileName],
                    ["Size", $"{msi.FileSizeBytes:N0} bytes"],
                    ["SHA-256", msi.Sha256],
                    ["Product name", msi.ProductName ?? "(not present)"],
                    ["Product version", msi.ProductVersion],
                    ["Product code", msi.ProductCode ?? "(not present)"],
                    ["Upgrade code", msi.UpgradeCode ?? "(not present)"],
                ]);
        }

        if (!msi.LooksLikeExpectedProduct)
        {
            // PRD 5.5: warn prominently, never block on a name that may change between releases.
            output.Warning(
                $"'{msi.ProductName ?? "(no product name)"}' does not look like a Quest Change " +
                "Auditor agent package. Confirm this is the right MSI before deploying it.");
        }

        return ExitCode.Success;
    }

    /// <summary>Deployment history (PRD 8.5).</summary>
    public static async Task<int> HistoryAsync(
        AppServices services,
        Output output,
        Guid? runGuid,
        string? host,
        OutputFormat format,
        CancellationToken ct)
    {
        var runs = await services.Deployments.GetRunsAsync(200, ct).ConfigureAwait(false);

        if (runGuid is not null)
        {
            var run = runs.FirstOrDefault(r => r.RunGuid == runGuid.Value)
                ?? throw new CommandFailedException(
                    ExitCode.InvalidArgumentsOrPreflight,
                    $"No run with GUID {runGuid} is recorded in this inventory.");

            return await ShowRunAsync(services, output, run, format, ct).ConfigureAwait(false);
        }

        if (!string.IsNullOrWhiteSpace(host))
        {
            return await ShowHostAsync(services, output, host, runs, format, ct).ConfigureAwait(false);
        }

        if (format == OutputFormat.Json)
        {
            output.Json(runs.Select(RunSummaryShape).ToList());
            return ExitCode.Success;
        }

        string[] headers = ["STARTED (UTC)", "RUN GUID", "OPERATOR", "VERSION", "ORG ID", "TARGETS", "OK", "FAIL", "HALTED"];

        var cells = runs.Select(r => (IReadOnlyList<string?>)
        [
            Output.Timestamp(r.StartedUtc),
            r.RunGuid.ToString("D"),
            r.OperatorAccount,
            r.MsiProductVersion,
            r.OrgId,
            r.TargetCount.ToString(),
            r.SuccessCount.ToString(),
            r.FailureCount.ToString(),
            r.WasHalted ? "yes" : "no",
        ]).ToList();

        if (format == OutputFormat.Csv)
        {
            output.Csv(headers, cells);
        }
        else
        {
            output.Table(headers, cells);
            output.Diagnostic($"{runs.Count} run(s).");
        }

        return ExitCode.Success;
    }

    private static async Task<int> ShowRunAsync(
        AppServices services,
        Output output,
        DeploymentRunRecord run,
        OutputFormat format,
        CancellationToken ct)
    {
        var results = await services.Deployments.GetResultsForRunAsync(run.Id, ct).ConfigureAwait(false);
        var byId = (await services.DomainControllers.GetAllAsync(includeInactive: true, ct).ConfigureAwait(false))
            .ToDictionary(dc => dc.Id, dc => dc.Fqdn);

        if (format == OutputFormat.Json)
        {
            output.Json(new
            {
                run = RunSummaryShape(run),
                halt_reason = run.HaltReason,
                log_directory = run.LogDirectory,
                results = results.Select(r => new
                {
                    fqdn = byId.GetValueOrDefault(r.DcId, $"(dc id {r.DcId})"),
                    outcome = r.Outcome.ToString(),
                    stage = r.Stage?.ToString(),
                    exit_code = r.ExitCode,
                    error_category = r.ErrorCategory?.ToString(),
                    error_detail = r.ErrorDetail,
                    attempt_count = r.AttemptCount,
                    started_utc = r.StartedUtc,
                    completed_utc = r.CompletedUtc,
                    msi_log_path = r.MsiLogPath,
                }),
            });

            return ExitCode.Success;
        }

        string[] headers = ["DC", "OUTCOME", "STAGE", "EXIT", "CATEGORY", "ATTEMPTS", "LOG"];

        var cells = results.Select(r => (IReadOnlyList<string?>)
        [
            byId.GetValueOrDefault(r.DcId, $"(dc id {r.DcId})"),
            r.Outcome.ToString(),
            r.Stage?.ToString(),
            r.ExitCode?.ToString(),
            r.ErrorCategory?.ToString(),
            r.AttemptCount.ToString(),
            r.MsiLogPath,
        ]).ToList();

        if (format == OutputFormat.Csv)
        {
            output.Csv(headers, cells);
        }
        else
        {
            output.Table(headers, cells);
            output.Diagnostic($"Run {run.RunGuid:D}; logs in {run.LogDirectory}");

            if (run.WasHalted)
            {
                output.Warning($"This run was halted: {run.HaltReason}");
            }
        }

        return ExitCode.Success;
    }

    private static async Task<int> ShowHostAsync(
        AppServices services,
        Output output,
        string host,
        IReadOnlyList<DeploymentRunRecord> runs,
        OutputFormat format,
        CancellationToken ct)
    {
        var dc = await services.DomainControllers.GetByFqdnAsync(host, ct).ConfigureAwait(false)
            ?? throw new CommandFailedException(
                ExitCode.InvalidArgumentsOrPreflight,
                $"'{host}' is not a domain controller in this inventory. Run `enumerate` first, " +
                "or check the spelling.");

        var rows = new List<(DeploymentRunRecord Run, DeploymentResultRecord Result)>();
        foreach (var run in runs)
        {
            foreach (var result in await services.Deployments.GetResultsForRunAsync(run.Id, ct).ConfigureAwait(false))
            {
                if (result.DcId == dc.Id)
                {
                    rows.Add((run, result));
                }
            }
        }

        if (format == OutputFormat.Json)
        {
            output.Json(new
            {
                fqdn = dc.Fqdn,
                deployments = rows.Select(x => new
                {
                    run_guid = x.Run.RunGuid.ToString("D"),
                    started_utc = x.Result.StartedUtc,
                    completed_utc = x.Result.CompletedUtc,
                    version = x.Run.MsiProductVersion,
                    org_id = x.Run.OrgId,
                    outcome = x.Result.Outcome.ToString(),
                    exit_code = x.Result.ExitCode,
                    error_category = x.Result.ErrorCategory?.ToString(),
                    msi_log_path = x.Result.MsiLogPath,
                }),
            });

            return ExitCode.Success;
        }

        string[] headers = ["COMPLETED (UTC)", "VERSION", "ORG ID", "OUTCOME", "EXIT", "RUN GUID"];

        var cells = rows.Select(x => (IReadOnlyList<string?>)
        [
            Output.Timestamp(x.Result.CompletedUtc),
            x.Run.MsiProductVersion,
            x.Run.OrgId,
            x.Result.Outcome.ToString(),
            x.Result.ExitCode?.ToString(),
            x.Run.RunGuid.ToString("D"),
        ]).ToList();

        if (format == OutputFormat.Csv)
        {
            output.Csv(headers, cells);
        }
        else
        {
            output.Table(headers, cells);
            output.Diagnostic($"{rows.Count} deployment(s) recorded for {dc.Fqdn}.");
        }

        return ExitCode.Success;
    }

    private static object RunSummaryShape(DeploymentRunRecord r) => new
    {
        run_guid = r.RunGuid.ToString("D"),
        started_utc = r.StartedUtc,
        completed_utc = r.CompletedUtc,
        operator_account = r.OperatorAccount,
        msi_file_name = r.MsiFileName,
        msi_product_version = r.MsiProductVersion,
        msi_sha256 = r.MsiSha256,
        org_id = r.OrgId,
        cloud_mode = r.CloudMode,
        max_parallel = r.MaxParallel,
        target_count = r.TargetCount,
        success_count = r.SuccessCount,
        failure_count = r.FailureCount,
        was_halted = r.WasHalted,
    };
}

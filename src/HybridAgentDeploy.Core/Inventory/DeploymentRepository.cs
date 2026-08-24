using HybridAgentDeploy.Core.Models;
using Microsoft.Data.Sqlite;

namespace HybridAgentDeploy.Core.Inventory;

/// <summary>
/// Reads and writes deployment runs and their per-target results (PRD 6, 8.5).
/// </summary>
/// <remarks>
/// This is the durable record the tool exists to produce (PRD G2). Rows are written as the
/// run progresses rather than at the end, so a run interrupted by a process kill still
/// leaves an accurate account of what had already happened — including which staging
/// directories exist on which targets (NFR7).
/// </remarks>
public sealed class DeploymentRepository
{
    private readonly SqliteConnectionFactory _connections;

    public DeploymentRepository(SqliteConnectionFactory connections)
    {
        _connections = connections ?? throw new ArgumentNullException(nameof(connections));
    }

    public async Task<long> CreateRunAsync(DeploymentRunRecord run, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(run);

        await using var connection = await _connections.OpenAsync(ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText =
            "INSERT INTO deployment_run " +
            "  (run_guid, started_utc, completed_utc, operator_account, msi_path, msi_file_name, " +
            "   msi_sha256, msi_product_version, msi_product_name, msi_product_code, org_id, " +
            "   cloud_mode, max_parallel, target_count, success_count, failure_count, " +
            "   was_halted, halt_reason, log_directory) " +
            "VALUES ($guid, $started, $completed, $operator, $path, $file, $sha, $version, $name, " +
            "        $code, $org, $cloud, $parallel, $targets, 0, 0, 0, NULL, $logdir) " +
            "RETURNING id;";

        command.Parameters.AddWithValue("$guid", run.RunGuid.ToString("D"));
        command.Parameters.AddWithValue("$started", run.StartedUtc);
        command.Parameters.AddWithValue("$completed", (object?)run.CompletedUtc ?? DBNull.Value);
        command.Parameters.AddWithValue("$operator", run.OperatorAccount);
        command.Parameters.AddWithValue("$path", run.MsiPath);
        command.Parameters.AddWithValue("$file", run.MsiFileName);
        command.Parameters.AddWithValue("$sha", run.MsiSha256);
        command.Parameters.AddWithValue("$version", run.MsiProductVersion);
        command.Parameters.AddWithValue("$name", (object?)run.MsiProductName ?? DBNull.Value);
        command.Parameters.AddWithValue("$code", (object?)run.MsiProductCode ?? DBNull.Value);
        command.Parameters.AddWithValue("$org", run.OrgId);
        command.Parameters.AddWithValue("$cloud", run.CloudMode ? 1 : 0);

        // Defence in depth: the ceiling is enforced by the orchestrator, but a value above
        // it must never reach storage either, or the history would misreport what ran
        // (PRD R7.1).
        command.Parameters.AddWithValue(
            "$parallel",
            Deployment.DeploymentLimits.ClampParallelism(run.MaxParallel));

        command.Parameters.AddWithValue("$targets", run.TargetCount);
        command.Parameters.AddWithValue("$logdir", run.LogDirectory);

        var id = Convert.ToInt64(
            await command.ExecuteScalarAsync(ct).ConfigureAwait(false),
            System.Globalization.CultureInfo.InvariantCulture);
        run.Id = id;
        return id;
    }

    public async Task CompleteRunAsync(DeploymentRunRecord run, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(run);

        await using var connection = await _connections.OpenAsync(ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText =
            "UPDATE deployment_run SET " +
            "  completed_utc = $completed, success_count = $success, failure_count = $failure, " +
            "  was_halted = $halted, halt_reason = $reason " +
            "WHERE id = $id;";
        command.Parameters.AddWithValue("$completed", (object?)run.CompletedUtc ?? UtcTimestamp.Now());
        command.Parameters.AddWithValue("$success", run.SuccessCount);
        command.Parameters.AddWithValue("$failure", run.FailureCount);
        command.Parameters.AddWithValue("$halted", run.WasHalted ? 1 : 0);
        command.Parameters.AddWithValue("$reason", (object?)run.HaltReason ?? DBNull.Value);
        command.Parameters.AddWithValue("$id", run.Id);
        await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    public async Task<long> CreateResultAsync(DeploymentResultRecord result, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(result);

        await using var connection = await _connections.OpenAsync(ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText =
            "INSERT INTO deployment_result " +
            "  (run_id, dc_id, started_utc, completed_utc, outcome, stage, exit_code, " +
            "   attempt_count, error_category, error_detail, msi_log_path, staging_path, " +
            "   staging_cleaned) " +
            "VALUES ($run, $dc, $started, $completed, $outcome, $stage, $exit, $attempts, " +
            "        $category, $detail, $log, $staging, $cleaned) " +
            "RETURNING id;";
        BindResult(command, result);

        var id = Convert.ToInt64(
            await command.ExecuteScalarAsync(ct).ConfigureAwait(false),
            System.Globalization.CultureInfo.InvariantCulture);
        result.Id = id;
        return id;
    }

    public async Task UpdateResultAsync(DeploymentResultRecord result, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(result);

        await using var connection = await _connections.OpenAsync(ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText =
            "UPDATE deployment_result SET " +
            "  started_utc = $started, completed_utc = $completed, outcome = $outcome, " +
            "  stage = $stage, exit_code = $exit, attempt_count = $attempts, " +
            "  error_category = $category, error_detail = $detail, msi_log_path = $log, " +
            "  staging_path = $staging, staging_cleaned = $cleaned " +
            "WHERE id = $id;";
        BindResult(command, result);
        command.Parameters.AddWithValue("$id", result.Id);
        await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Records the staging directory that is about to be created on a target.
    /// </summary>
    /// <remarks>
    /// NFR7: this MUST be called before the directory is created, not after. If the process
    /// is killed between the write and the creation, the tool looks for a directory that
    /// does not exist, which is harmless. In the other order a killed process leaves a
    /// directory on a domain controller that nothing knows about.
    /// </remarks>
    public async Task RecordStagingPathAsync(long resultId, string stagingPath, CancellationToken ct)
    {
        await using var connection = await _connections.OpenAsync(ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText =
            "UPDATE deployment_result SET staging_path = $path, staging_cleaned = 0 WHERE id = $id;";
        command.Parameters.AddWithValue("$path", stagingPath);
        command.Parameters.AddWithValue("$id", resultId);
        await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    /// <summary>Marks a staging directory as confirmed removed from the target (SEC8).</summary>
    public async Task MarkStagingCleanedAsync(long resultId, CancellationToken ct)
    {
        await using var connection = await _connections.OpenAsync(ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "UPDATE deployment_result SET staging_cleaned = 1 WHERE id = $id;";
        command.Parameters.AddWithValue("$id", resultId);
        await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Staging directories this tool created and never confirmed removing (NFR7).
    /// </summary>
    /// <remarks>
    /// The recovery path for a run killed mid-flight: every orphan is identifiable, so a
    /// later invocation can clean up after an earlier one rather than leaving an MSI on a
    /// domain controller indefinitely.
    /// </remarks>
    public async Task<IReadOnlyList<OrphanedStagingDirectory>> GetOrphanedStagingDirectoriesAsync(
        CancellationToken ct)
    {
        await using var connection = await _connections.OpenAsync(ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT res.id, dc.fqdn, res.staging_path, run.run_guid, run.started_utc " +
            "FROM deployment_result res " +
            "JOIN domain_controller dc ON dc.id = res.dc_id " +
            "JOIN deployment_run run ON run.id = res.run_id " +
            "WHERE res.staging_path IS NOT NULL AND res.staging_cleaned = 0 " +
            "ORDER BY run.started_utc DESC, dc.fqdn COLLATE NOCASE;";

        var results = new List<OrphanedStagingDirectory>();
        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            results.Add(new OrphanedStagingDirectory(
                reader.GetInt64(0),
                reader.GetString(1),
                reader.GetString(2),
                Guid.Parse(reader.GetString(3)),
                reader.GetString(4)));
        }

        return results;
    }

    public async Task<IReadOnlyList<DeploymentRunRecord>> GetRunsAsync(int limit, CancellationToken ct)
    {
        await using var connection = await _connections.OpenAsync(ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT id, run_guid, started_utc, completed_utc, operator_account, msi_path, " +
            "       msi_file_name, msi_sha256, msi_product_version, msi_product_name, " +
            "       msi_product_code, org_id, cloud_mode, max_parallel, target_count, " +
            "       success_count, failure_count, was_halted, halt_reason, log_directory " +
            "FROM deployment_run ORDER BY started_utc DESC, id DESC LIMIT $limit;";
        command.Parameters.AddWithValue("$limit", limit);

        var results = new List<DeploymentRunRecord>();
        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            results.Add(new DeploymentRunRecord
            {
                Id = reader.GetInt64(0),
                RunGuid = Guid.Parse(reader.GetString(1)),
                StartedUtc = reader.GetString(2),
                CompletedUtc = reader.IsDBNull(3) ? null : reader.GetString(3),
                OperatorAccount = reader.GetString(4),
                MsiPath = reader.GetString(5),
                MsiFileName = reader.GetString(6),
                MsiSha256 = reader.GetString(7),
                MsiProductVersion = reader.GetString(8),
                MsiProductName = reader.IsDBNull(9) ? null : reader.GetString(9),
                MsiProductCode = reader.IsDBNull(10) ? null : reader.GetString(10),
                OrgId = reader.GetString(11),
                CloudMode = reader.GetInt32(12) != 0,
                MaxParallel = reader.GetInt32(13),
                TargetCount = reader.GetInt32(14),
                SuccessCount = reader.GetInt32(15),
                FailureCount = reader.GetInt32(16),
                WasHalted = reader.GetInt32(17) != 0,
                HaltReason = reader.IsDBNull(18) ? null : reader.GetString(18),
                LogDirectory = reader.GetString(19),
            });
        }

        return results;
    }

    public async Task<IReadOnlyList<DeploymentResultRecord>> GetResultsForRunAsync(
        long runId,
        CancellationToken ct)
    {
        await using var connection = await _connections.OpenAsync(ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT id, run_id, dc_id, started_utc, completed_utc, outcome, stage, exit_code, " +
            "       attempt_count, error_category, error_detail, msi_log_path, staging_path, " +
            "       staging_cleaned " +
            "FROM deployment_result WHERE run_id = $run ORDER BY id;";
        command.Parameters.AddWithValue("$run", runId);

        var results = new List<DeploymentResultRecord>();
        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            results.Add(new DeploymentResultRecord
            {
                Id = reader.GetInt64(0),
                RunId = reader.GetInt64(1),
                DcId = reader.GetInt64(2),
                StartedUtc = reader.IsDBNull(3) ? null : reader.GetString(3),
                CompletedUtc = reader.IsDBNull(4) ? null : reader.GetString(4),
                Outcome = DbEnums.ToOutcome(reader.GetString(5)),
                Stage = reader.IsDBNull(6) ? null : DbEnums.ToStage(reader.GetString(6)),
                ExitCode = reader.IsDBNull(7) ? null : reader.GetInt32(7),
                AttemptCount = reader.GetInt32(8),
                ErrorCategory = reader.IsDBNull(9) ? null : DbEnums.ToErrorCategory(reader.GetString(9)),
                ErrorDetail = reader.IsDBNull(10) ? null : reader.GetString(10),
                MsiLogPath = reader.IsDBNull(11) ? null : reader.GetString(11),
                StagingPath = reader.IsDBNull(12) ? null : reader.GetString(12),
                StagingCleaned = reader.GetInt32(13) != 0,
            });
        }

        return results;
    }

    private static void BindResult(SqliteCommand command, DeploymentResultRecord result)
    {
        command.Parameters.AddWithValue("$run", result.RunId);
        command.Parameters.AddWithValue("$dc", result.DcId);
        command.Parameters.AddWithValue("$started", (object?)result.StartedUtc ?? DBNull.Value);
        command.Parameters.AddWithValue("$completed", (object?)result.CompletedUtc ?? DBNull.Value);
        command.Parameters.AddWithValue("$outcome", DbEnums.ToDb(result.Outcome));
        command.Parameters.AddWithValue(
            "$stage",
            result.Stage is null ? DBNull.Value : DbEnums.ToDb(result.Stage.Value));
        command.Parameters.AddWithValue("$exit", (object?)result.ExitCode ?? DBNull.Value);
        command.Parameters.AddWithValue("$attempts", result.AttemptCount);
        command.Parameters.AddWithValue(
            "$category",
            result.ErrorCategory is null ? DBNull.Value : DbEnums.ToDb(result.ErrorCategory.Value));
        command.Parameters.AddWithValue("$detail", (object?)result.ErrorDetail ?? DBNull.Value);
        command.Parameters.AddWithValue("$log", (object?)result.MsiLogPath ?? DBNull.Value);
        command.Parameters.AddWithValue("$staging", (object?)result.StagingPath ?? DBNull.Value);
        command.Parameters.AddWithValue("$cleaned", result.StagingCleaned ? 1 : 0);
    }
}

/// <summary>
/// A staging directory recorded in the database that cleanup never confirmed removing
/// (NFR7).
/// </summary>
public sealed record OrphanedStagingDirectory(
    long ResultId,
    string TargetFqdn,
    string StagingPath,
    Guid RunGuid,
    string RunStartedUtc);

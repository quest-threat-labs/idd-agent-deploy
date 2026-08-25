using System.Diagnostics;
using System.Text;
using HybridAgentDeploy.Core.Models;
using HybridAgentDeploy.Gui.Presentation;

namespace HybridAgentDeploy.Gui.Views;

/// <summary>
/// Deployment history (PRD 8.5): the runs list, per-DC drill-down, and CSV export.
/// </summary>
internal sealed class HistoryTab : UserControl
{
    private readonly MainForm _main;

    private readonly DataGridView _runs = NewGrid();
    private readonly DataGridView _results = NewGrid();
    private readonly Button _export = Ui.Button("Export run to CSV...");
    private readonly Button _openLog = Ui.Button("Open selected DC's log");
    private readonly Button _refresh = Ui.Button("Refresh");

    private IReadOnlyList<DeploymentRunRecord> _runRecords = [];
    private IReadOnlyList<DeploymentResultRecord> _currentResults = [];
    private Dictionary<long, string> _fqdnByDcId = [];

    public HistoryTab(MainForm main)
    {
        _main = main;

        _runs.Columns.Add("started", "Started (UTC)");
        _runs.Columns.Add("operator", "Operator");
        _runs.Columns.Add("version", "MSI version");
        _runs.Columns.Add("orgId", "Org ID");
        _runs.Columns.Add("targets", "Targets");
        _runs.Columns.Add("ok", "Succeeded");
        _runs.Columns.Add("failed", "Failed");
        _runs.Columns.Add("halted", "Halted");

        _results.Columns.Add("fqdn", "Domain controller");
        _results.Columns.Add("outcome", "Outcome");
        _results.Columns.Add("exit", "Exit code");
        _results.Columns.Add("category", "Category");
        _results.Columns.Add("attempts", "Attempts");
        _results.Columns.Add("detail", "Detail");

        _runs.SelectionChanged += async (_, _) => await OnRunSelectedAsync();
        _results.SelectionChanged += (_, _) => _openLog.Enabled = SelectedResult()?.MsiLogPath is not null;
        _results.CellDoubleClick += (_, _) => OpenSelectedLog();

        _export.Click += (_, _) => ExportSelectedRun();
        _openLog.Click += (_, _) => OpenSelectedLog();
        _refresh.Click += (_, _) => Reload();

        _export.Enabled = false;
        _openLog.Enabled = false;

        var buttons = Ui.Bar(_refresh, _export, _openLog);

        var split = new SplitContainer
        {
            Dock = DockStyle.Fill,
            Orientation = Orientation.Horizontal,
            SplitterDistance = 260,
        };

        split.Panel1.Controls.Add(_runs);
        split.Panel2.Controls.Add(_results);

        Controls.Add(split);
        Controls.Add(buttons);

        Load += (_, _) => Reload();
    }

    /// <summary>Reloads the runs list. Called after a deployment finishes.</summary>
    public void Reload() => _ = ReloadAsync();

    private async Task ReloadAsync()
    {
        _runRecords = await _main.Deployments.GetRunsAsync(200, CancellationToken.None);

        _fqdnByDcId = (await _main.DomainControllers.GetAllAsync(includeInactive: true, CancellationToken.None))
            .ToDictionary(dc => dc.Id, dc => dc.Fqdn);

        _runs.Rows.Clear();

        foreach (var run in _runRecords)
        {
            var index = _runs.Rows.AddRow(
                InventoryTab.FormatUtc(run.StartedUtc),
                run.OperatorAccount,
                run.MsiProductVersion,
                run.OrgId,
                run.TargetCount,
                run.SuccessCount,
                run.FailureCount,
                run.WasHalted ? "yes" : string.Empty);

            _runs.Rows[index].Tag = run;

            if (run.WasHalted)
            {
                _runs.Rows[index].Cells["halted"].Style.BackColor = Color.FromArgb(253, 231, 233);
                _runs.Rows[index].Cells["halted"].Style.ForeColor = Color.FromArgb(140, 20, 30);
            }
        }

        _results.Rows.Clear();
        _export.Enabled = false;
        _openLog.Enabled = false;
    }

    private async Task OnRunSelectedAsync()
    {
        if (SelectedRun() is not { } run)
        {
            return;
        }

        _currentResults = await _main.Deployments.GetResultsForRunAsync(run.Id, CancellationToken.None);

        _results.Rows.Clear();

        foreach (var result in _currentResults)
        {
            var appearance = OutcomeStyle.For(result.Outcome);

            var index = _results.Rows.AddRow(
                _fqdnByDcId.GetValueOrDefault(result.DcId, $"(dc id {result.DcId})"),
                appearance.Text,
                result.ExitCode,
                result.ErrorCategory?.ToString(),
                result.AttemptCount,
                result.ErrorDetail);

            _results.Rows[index].Tag = result;
            _results.Rows[index].Cells["outcome"].Style.BackColor = appearance.BackColor;
            _results.Rows[index].Cells["outcome"].Style.ForeColor = appearance.ForeColor;
        }

        _export.Enabled = true;

        if (run.WasHalted && run.HaltReason is not null)
        {
            // The halt reason explains why most targets were never attempted; leaving it only
            // in the run log would make the results grid look inexplicable.
            _results.Rows.AddRow(string.Empty, string.Empty, null, string.Empty, null,
                $"RUN HALTED: {run.HaltReason}");
        }
    }

    private DeploymentRunRecord? SelectedRun() =>
        _runs.CurrentRow?.Tag as DeploymentRunRecord;

    private DeploymentResultRecord? SelectedResult() =>
        _results.CurrentRow?.Tag as DeploymentResultRecord;

    /// <summary>
    /// PRD 8.5: a link opening that DC's retrieved msiexec verbose log.
    /// </summary>
    private void OpenSelectedLog()
    {
        var path = SelectedResult()?.MsiLogPath;

        if (string.IsNullOrEmpty(path))
        {
            MessageBox.Show(
                this,
                "No installer log was retrieved for this domain controller. msiexec may have " +
                "failed before it could write one — the exit code is the better diagnostic in " +
                "that case.",
                "No log",
                MessageBoxButtons.OK,
                MessageBoxIcon.Information);
            return;
        }

        if (!File.Exists(path))
        {
            MessageBox.Show(
                this,
                $"The log was recorded at{Environment.NewLine}{path}{Environment.NewLine}{Environment.NewLine}" +
                "but is no longer there. Run logs are not managed by this utility once written.",
                "Log not found",
                MessageBoxButtons.OK,
                MessageBoxIcon.Warning);
            return;
        }

        Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
    }

    /// <summary>PRD 8.5: export a run's results to CSV.</summary>
    private void ExportSelectedRun()
    {
        if (SelectedRun() is not { } run)
        {
            return;
        }

        using var dialog = new SaveFileDialog
        {
            Title = "Export run results",
            Filter = "CSV files (*.csv)|*.csv",
            FileName = $"deployment-{run.RunGuid:D}.csv",
        };

        if (dialog.ShowDialog(this) != DialogResult.OK)
        {
            return;
        }

        var builder = new StringBuilder();
        builder.AppendLine(
            "fqdn,outcome,stage,exit_code,error_category,attempt_count,started_utc,completed_utc," +
            "msi_log_path,error_detail");

        foreach (var result in _currentResults)
        {
            builder.AppendLine(string.Join(',', new[]
            {
                Csv(_fqdnByDcId.GetValueOrDefault(result.DcId, $"(dc id {result.DcId})")),
                Csv(result.Outcome.ToString()),
                Csv(result.Stage?.ToString()),
                Csv(result.ExitCode?.ToString()),
                Csv(result.ErrorCategory?.ToString()),
                Csv(result.AttemptCount.ToString()),
                Csv(result.StartedUtc),
                Csv(result.CompletedUtc),
                Csv(result.MsiLogPath),
                Csv(result.ErrorDetail),
            }));
        }

        File.WriteAllText(dialog.FileName, builder.ToString(), Encoding.UTF8);

        MessageBox.Show(this, $"Exported to {dialog.FileName}", "Export complete",
            MessageBoxButtons.OK, MessageBoxIcon.Information);
    }

    /// <summary>Escapes a CSV field; error detail is free text full of commas and quotes.</summary>
    private static string Csv(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return string.Empty;
        }

        return value.AsSpan().IndexOfAny(",\"\r\n") >= 0
            ? "\"" + value.Replace("\"", "\"\"", StringComparison.Ordinal) + "\""
            : value;
    }

    private static DataGridView NewGrid() => new()
    {
        Dock = DockStyle.Fill,
        AllowUserToAddRows = false,
        AllowUserToDeleteRows = false,
        ReadOnly = true,
        RowHeadersVisible = false,
        AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill,
        SelectionMode = DataGridViewSelectionMode.FullRowSelect,
        MultiSelect = false,
        BackgroundColor = SystemColors.Window,
    };
}

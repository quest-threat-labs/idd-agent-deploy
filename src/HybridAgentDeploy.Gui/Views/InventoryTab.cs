using HybridAgentDeploy.Core;
using HybridAgentDeploy.Core.Discovery;
using HybridAgentDeploy.Core.Models;
using HybridAgentDeploy.Gui.Presentation;

namespace HybridAgentDeploy.Gui.Views;

/// <summary>
/// The inventory grid and its filters (PRD 8.1).
/// </summary>
internal sealed class InventoryTab : UserControl
{
    private const string AnyChoice = "(any)";

    private readonly MainForm _main;

    private readonly DataGridView _grid = new()
    {
        Dock = DockStyle.Fill,
        AllowUserToAddRows = false,
        AllowUserToDeleteRows = false,
        AllowUserToResizeRows = false,
        AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill,
        RowHeadersVisible = false,
        SelectionMode = DataGridViewSelectionMode.FullRowSelect,
        MultiSelect = true,
        EditMode = DataGridViewEditMode.EditOnEnter,
        BackgroundColor = SystemColors.Window,
    };

    private readonly TextBox _fqdnFilter = new() { Width = 200, PlaceholderText = "filter by name..." };
    private readonly ComboBox _tagFilter = new() { Width = 150, DropDownStyle = ComboBoxStyle.DropDownList };
    private readonly ComboBox _siteFilter = new() { Width = 150, DropDownStyle = ComboBoxStyle.DropDownList };
    private readonly ComboBox _outcomeFilter = new() { Width = 160, DropDownStyle = ComboBoxStyle.DropDownList };
    private readonly Label _status = new() { AutoSize = true, Padding = new Padding(8, 8, 0, 0) };

    private IReadOnlyList<InventoryRow> _visible = [];
    private bool _suppressCellEvents;

    public InventoryTab(MainForm main)
    {
        _main = main;

        BuildColumns();

        _grid.CellValueChanged += OnCellValueChanged;
        _grid.CurrentCellDirtyStateChanged += (_, _) =>
        {
            // Commit a checkbox tick immediately rather than when focus leaves the cell, so
            // the selection count updates as the operator clicks.
            if (_grid.IsCurrentCellDirty)
            {
                _grid.CommitEdit(DataGridViewDataErrorContexts.Commit);
            }
        };

        _fqdnFilter.TextChanged += (_, _) => ApplyFilters();
        _tagFilter.SelectedIndexChanged += (_, _) => ApplyFilters();
        _siteFilter.SelectedIndexChanged += (_, _) => ApplyFilters();
        _outcomeFilter.SelectedIndexChanged += (_, _) => ApplyFilters();

        _outcomeFilter.Items.AddRange(["(any)", "Success", "Reboot pending", "Failed", "Never deployed"]);
        _outcomeFilter.SelectedIndex = 0;

        Controls.Add(_grid);
        Controls.Add(BuildFilterBar());
        Controls.Add(BuildActionBar());
    }

    private Control BuildActionBar()
    {
        return Ui.Bar(
            NewButton("Enumerate from Active Directory...", OnEnumerateAsync),
            NewButton("Import from file...", OnImportAsync),
            NewButton("Manage tags", _ => { _main.ShowTagsTab(); return Task.CompletedTask; }),
            NewButton("Select all", _ => { SelectVisible(true); return Task.CompletedTask; }),
            NewButton("Select none", _ => { SelectVisible(false); return Task.CompletedTask; }),
            _status);
    }

    private Control BuildFilterBar()
    {
        return Ui.Bar(
            Ui.Label("Name:"), _fqdnFilter,
            Ui.Label("Tag:"), _tagFilter,
            Ui.Label("Site:"), _siteFilter,
            Ui.Label("Last outcome:"), _outcomeFilter);
    }

    private void BuildColumns()
    {
        _grid.Columns.Add(new DataGridViewCheckBoxColumn
        {
            Name = "selected",
            HeaderText = string.Empty,
            FillWeight = 18,
        });

        AddTextColumn("fqdn", "FQDN", 100);
        AddTextColumn("site", "Site", 55);
        AddTextColumn("os", "OS", 90);
        AddTextColumn("rodc", "RODC", 28);
        AddTextColumn("tags", "Tags", 70);
        AddTextColumn("version", "Last version", 55);
        AddTextColumn("deployed", "Last deployed (UTC)", 70);
        AddTextColumn("outcome", "Last outcome", 65);

        foreach (DataGridViewColumn column in _grid.Columns)
        {
            column.ReadOnly = column.Name != "selected";
            column.SortMode = DataGridViewColumnSortMode.NotSortable;
        }
    }

    private void AddTextColumn(string name, string header, int weight) =>
        _grid.Columns.Add(new DataGridViewTextBoxColumn
        {
            Name = name,
            HeaderText = header,
            FillWeight = weight,
        });

    /// <summary>Rebuilds the dropdown choices and re-applies the current filters.</summary>
    public void BindInventory()
    {
        var previousTag = _tagFilter.SelectedItem as string;
        var previousSite = _siteFilter.SelectedItem as string;

        RebindChoices(_tagFilter, InventoryFilter.TagChoices(_main.Inventory), previousTag);
        RebindChoices(_siteFilter, InventoryFilter.SiteChoices(_main.Inventory), previousSite);

        ApplyFilters();
    }

    private static void RebindChoices(ComboBox combo, IReadOnlyList<string> choices, string? previous)
    {
        combo.BeginUpdate();
        combo.Items.Clear();
        combo.Items.Add(AnyChoice);
        foreach (var choice in choices)
        {
            combo.Items.Add(choice);
        }

        // Keep the operator's filter across a refresh where the value still exists.
        combo.SelectedItem = previous is not null && combo.Items.Contains(previous) ? previous : AnyChoice;
        combo.EndUpdate();
    }

    private void ApplyFilters()
    {
        var criteria = new InventoryFilterCriteria
        {
            FqdnContains = _fqdnFilter.Text,
            Tag = Chosen(_tagFilter),
            Site = Chosen(_siteFilter),
            Outcome = (OutcomeFilter)Math.Max(0, _outcomeFilter.SelectedIndex),
        };

        _visible = InventoryFilter.Apply(_main.Inventory, criteria);
        Render();
    }

    private static string? Chosen(ComboBox combo) =>
        combo.SelectedItem is string value && value != AnyChoice ? value : null;

    private void Render()
    {
        _suppressCellEvents = true;
        try
        {
            _grid.Rows.Clear();

            foreach (var row in _visible)
            {
                var appearance = OutcomeStyle.For(row.LastOutcome);

                var index = _grid.Rows.AddRow(
                    _main.Selection.IsSelected(row.DcId),
                    row.Fqdn,
                    row.SiteName,
                    row.OsVersion,
                    row.IsReadOnly ? "yes" : string.Empty,
                    string.Join(", ", row.Tags),
                    row.LastDeployedVersion,
                    FormatUtc(row.LastDeployedUtc),
                    appearance.Text);

                var gridRow = _grid.Rows[index];
                gridRow.Tag = row;

                // PRD 8.1: the outcome column is colour-coded. The cell also carries the text,
                // so colour is the fast path rather than the only one.
                var outcomeCell = gridRow.Cells["outcome"];
                outcomeCell.Style.BackColor = appearance.BackColor;
                outcomeCell.Style.ForeColor = appearance.ForeColor;
            }
        }
        finally
        {
            _suppressCellEvents = false;
        }

        UpdateStatus();
    }

    private void UpdateStatus()
    {
        var hidden = _main.Selection.HiddenSelectedCount(_visible);

        _status.Text =
            $"{_visible.Count} of {_main.Inventory.Count} shown · {_main.Selection.Count} selected" +
            (hidden > 0 ? $" ({hidden} hidden by the current filter)" : string.Empty);
    }

    private void OnCellValueChanged(object? sender, DataGridViewCellEventArgs e)
    {
        if (_suppressCellEvents || e.RowIndex < 0 || _grid.Columns[e.ColumnIndex].Name != "selected")
        {
            return;
        }

        if (_grid.Rows[e.RowIndex].Tag is InventoryRow row)
        {
            _main.Selection.Set(row.DcId, _grid.Rows[e.RowIndex].Cells["selected"].Value is true);
            UpdateStatus();
            _main.OnSelectionChanged();
        }
    }

    private void SelectVisible(bool selected)
    {
        if (selected)
        {
            _main.Selection.SelectAll(_visible);
        }
        else
        {
            _main.Selection.SelectNone(_visible);
        }

        Render();
        _main.OnSelectionChanged();
    }

    private async Task OnEnumerateAsync(Button button)
    {
        using var busy = new BusyScope(this, button, "Enumerating...");

        var enumerator = new ActiveDirectoryEnumerator(_main.Logger<ActiveDirectoryEnumerator>());

        var result = await Task.Run(() =>
            enumerator.EnumerateAsync(null, null, CancellationToken.None)).ConfigureAwait(true);

        foreach (var dc in result.DomainControllers)
        {
            await _main.DomainControllers.UpsertAsync(dc, CancellationToken.None);
        }

        var deactivated = await _main.DomainControllers.DeactivateEnumeratedExceptAsync(
            result.DomainControllers.Select(d => d.Fqdn), CancellationToken.None);

        await _main.RefreshInventoryAsync();

        var message =
            $"Discovered {result.DomainControllers.Count} domain controller(s) across " +
            $"{result.DomainsSearched.Count} domain(s).";

        if (deactivated.Count > 0)
        {
            message += Environment.NewLine + Environment.NewLine +
                $"{deactivated.Count} previously known controller(s) were not returned and have " +
                "been marked inactive. Their tags and deployment history are retained:" +
                Environment.NewLine + string.Join(Environment.NewLine, deactivated.Select(d => "  " + d));
        }

        if (result.Warnings.Count > 0)
        {
            message += Environment.NewLine + Environment.NewLine + "Warnings:" +
                Environment.NewLine + string.Join(Environment.NewLine, result.Warnings.Select(w => "  " + w));
        }

        MessageBox.Show(this, message, "Enumeration complete", MessageBoxButtons.OK,
            result.Warnings.Count > 0 ? MessageBoxIcon.Warning : MessageBoxIcon.Information);
    }

    private async Task OnImportAsync(Button button)
    {
        using var dialog = new OpenFileDialog
        {
            Title = "Import a list of domain controllers to tag",
            Filter = "Text files (*.txt)|*.txt|All files (*.*)|*.*",
        };

        if (dialog.ShowDialog(this) != DialogResult.OK)
        {
            return;
        }

        var tagName = Prompt.ForText(
            this,
            "Tag to apply",
            "Every domain controller named in the file will be given this tag." +
            Environment.NewLine + Environment.NewLine +
            "Import selects and tags controllers already discovered from Active Directory; it " +
            "does not add new ones.");

        if (string.IsNullOrWhiteSpace(tagName))
        {
            return;
        }

        using var busy = new BusyScope(this, button, "Importing...");

        var service = new FileImportService(
            _main.DomainControllers, _main.Tags, _main.Resolver, _main.Logger<FileImportService>());

        ImportReport report;
        try
        {
            report = await service.ImportFileAsync(dialog.FileName, [tagName], false, CancellationToken.None);
        }
        catch (Exception ex) when (ex is InventoryNotEnumeratedException or FileNotFoundException)
        {
            MessageBox.Show(this, ex.Message, "Import failed", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        await _main.RefreshInventoryAsync();

        var message = $"{report.Matched.Count} domain controller(s) tagged '{tagName}'.";

        if (report.Unmatched.Count > 0)
        {
            message += Environment.NewLine + Environment.NewLine +
                $"{report.Unmatched.Count} entr(ies) matched nothing in the inventory:" +
                Environment.NewLine +
                string.Join(Environment.NewLine,
                    report.Unmatched.Select(u => $"  line {u.LineNumber}: {u.RawValue}"));
        }

        if (report.Duplicates.Count > 0)
        {
            message += Environment.NewLine + Environment.NewLine +
                $"{report.Duplicates.Count} duplicate line(s) were ignored.";
        }

        MessageBox.Show(this, message, "Import complete", MessageBoxButtons.OK,
            report.HasProblems ? MessageBoxIcon.Warning : MessageBoxIcon.Information);
    }

    internal static string? FormatUtc(string? storedUtc) =>
        string.IsNullOrEmpty(storedUtc)
            ? null
            : UtcTimestamp.Parse(storedUtc).ToString("yyyy-MM-dd HH:mm:ss");

    private Button NewButton(string text, Func<Button, Task> action)
    {
        var button = Ui.Button(text);
        button.Click += async (_, _) =>
        {
            try
            {
                await action(button);
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, ex.Message, "Failed", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        };

        return button;
    }
}

/// <summary>
/// Disables a button and shows a wait cursor for the duration of a long operation.
/// </summary>
/// <remarks>
/// NFR2 keeps the UI responsive by doing the work asynchronously; this stops the operator
/// clicking Enumerate four more times while the first is still running.
/// </remarks>
internal sealed class BusyScope : IDisposable
{
    private readonly Control _owner;
    private readonly Button _button;
    private readonly string _originalText;

    public BusyScope(Control owner, Button button, string busyText)
    {
        _owner = owner;
        _button = button;
        _originalText = button.Text;

        button.Enabled = false;
        button.Text = busyText;
        owner.Cursor = Cursors.WaitCursor;
    }

    public void Dispose()
    {
        _button.Enabled = true;
        _button.Text = _originalText;
        _owner.Cursor = Cursors.Default;
    }
}

/// <summary>A single-line text prompt, since WinForms has no built-in one.</summary>
internal static class Prompt
{
    public static string? ForText(IWin32Window owner, string title, string message, string initial = "")
    {
        using var form = new Form
        {
            Text = title,
            FormBorderStyle = FormBorderStyle.FixedDialog,
            StartPosition = FormStartPosition.CenterParent,
            MinimizeBox = false,
            MaximizeBox = false,
            ClientSize = new Size(460, 190),
        };

        var label = new Label { Text = message, Left = 12, Top = 12, Width = 436, Height = 90 };
        var input = new TextBox { Left = 12, Top = 108, Width = 436, Text = initial };
        var ok = new Button { Text = "OK", DialogResult = DialogResult.OK, Left = 292, Top = 142, Width = 75 };
        var cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, Left = 373, Top = 142, Width = 75 };

        form.Controls.AddRange([label, input, ok, cancel]);
        form.AcceptButton = ok;
        form.CancelButton = cancel;

        return form.ShowDialog(owner) == DialogResult.OK ? input.Text.Trim() : null;
    }
}

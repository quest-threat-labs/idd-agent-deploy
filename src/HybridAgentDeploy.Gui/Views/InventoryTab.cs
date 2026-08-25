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

    /// <summary>
    /// Starting widths for the filter dropdowns, before they are sized to the forest's own
    /// names (see <see cref="Ui.SizeToWidestItem"/>).
    /// </summary>
    /// <remarks>
    /// The domain dropdown starts wider because a domain name is an FQDN and a child domain's
    /// is longer than its parent's, so it is the one most likely to need the room.
    /// </remarks>
    private const int DomainFilterWidth = 210;

    private const int NarrowFilterWidth = 150;

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

    /// <summary>
    /// Narrows the grid to one Active Directory domain.
    /// </summary>
    /// <remarks>
    /// The width here is a starting point; it is recomputed from the forest's own domain names
    /// each time the list is rebound. See <see cref="Ui.SizeToWidestItem"/>.
    /// </remarks>
    private readonly ComboBox _domainFilter = new() { Width = DomainFilterWidth, DropDownStyle = ComboBoxStyle.DropDownList };

    private readonly ComboBox _tagFilter = new() { Width = NarrowFilterWidth, DropDownStyle = ComboBoxStyle.DropDownList };
    private readonly ComboBox _siteFilter = new() { Width = NarrowFilterWidth, DropDownStyle = ComboBoxStyle.DropDownList };
    private readonly ComboBox _outcomeFilter = new() { Width = 160, DropDownStyle = ComboBoxStyle.DropDownList };
    private readonly Label _status = new() { AutoSize = true, Padding = new Padding(8, 8, 0, 0) };

    private Button _editTags = null!;
    private IReadOnlyList<InventoryRow> _visible = [];
    private bool _suppressCellEvents;

    public InventoryTab(MainForm main)
    {
        _main = main;

        // Created before anything below can render the grid. Setting the outcome filter's
        // initial index fires SelectedIndexChanged, which reaches UpdateStatus and reads this
        // button's enabled state — creating it later in BuildActionBar left it null at that
        // moment, and the application failed to start at all.
        _editTags = NewButton("Edit tags...", OnEditTagsAsync);
        _editTags.Enabled = false;

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
        _domainFilter.SelectedIndexChanged += (_, _) => ApplyFilters();
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
            NewButton("Tag from file...", OnTagFromFileAsync),
            _editTags,
            NewButton("Select all", _ => { SelectVisible(true); return Task.CompletedTask; }),
            NewButton("Select none", _ => { SelectVisible(false); return Task.CompletedTask; }),
            _status);
    }

    /// <summary>
    /// Adds and removes tags across the ticked domain controllers (PRD 8.1).
    /// </summary>
    /// <remarks>
    /// Acts on the ticked selection — the same one the Deploy tab uses — so the application has
    /// a single answer to "which domain controllers am I working with", and a selection built
    /// across several filters is not lost by changing one.
    /// </remarks>
    private async Task OnEditTagsAsync(Button button)
    {
        var selected = _main.Inventory.Where(r => _main.Selection.IsSelected(r.DcId)).ToList();

        if (selected.Count == 0)
        {
            return;
        }

        var edits = TagEditDialog.Show(
            this,
            Presentation.TagList.Build(_main.AllTags, _main.Inventory),
            selected,
            _main.Selection.HiddenSelectedCount(_visible));

        if (edits is null)
        {
            return;
        }

        using var busy = new BusyScope(this, button, "Applying...");

        var dcIds = selected.Select(r => r.DcId).ToList();

        foreach (var name in edits.Add)
        {
            var tag = await _main.Tags.GetOrCreateAsync(name, null, CancellationToken.None);
            await _main.Tags.ApplyTagAsync(tag.Id, dcIds, CancellationToken.None);
        }

        foreach (var name in edits.Remove)
        {
            // A tag being removed necessarily exists; guard anyway rather than assume.
            if (await _main.Tags.GetByNameAsync(name, CancellationToken.None) is { } tag)
            {
                await _main.Tags.RemoveTagAsync(tag.Id, dcIds, CancellationToken.None);
            }
        }

        await _main.RefreshInventoryAsync();
    }

    private Control BuildFilterBar()
    {
        // Ordered as the grid's columns are — name, domain, then the rest — so an operator
        // reading across the filter bar and then down a column is looking at the same sequence
        // in both places.
        return Ui.Bar(
            Ui.Label("Name:"), _fqdnFilter,
            Ui.Label("Domain:"), _domainFilter,
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

        // Beside the FQDN, which already ends in it. Shown as a column of its own because in a
        // multi-domain forest the domain is the boundary an operator scopes work to, and the
        // FQDN column truncates — which is to say the one piece of it that gets cut off is the
        // domain.
        AddTextColumn("domain", "Domain", 108);

        AddTextColumn("site", "Site", 55);
        AddTextColumn("os", "OS", 62);
        AddTextColumn("rodc", "RODC", 28);
        AddTextColumn("tags", "Tags", 58);
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
        var previousDomain = _domainFilter.SelectedItem as string;
        var previousTag = _tagFilter.SelectedItem as string;
        var previousSite = _siteFilter.SelectedItem as string;

        // Every tag, not only those currently applied, so a tag created a moment ago appears
        // here too. Filtering by an empty tag shows an empty grid, which is the honest answer.
        RebindChoices(
            _tagFilter,
            [.. _main.AllTags.Select(t => t.Name).OrderBy(n => n, StringComparer.OrdinalIgnoreCase)],
            previousTag,
            NarrowFilterWidth);

        RebindChoices(
            _domainFilter, InventoryFilter.DomainChoices(_main.Inventory), previousDomain, DomainFilterWidth);

        RebindChoices(
            _siteFilter, InventoryFilter.SiteChoices(_main.Inventory), previousSite, NarrowFilterWidth);

        ApplyFilters();
    }

    /// <param name="minimumWidth">
    /// The control's declared width, passed in rather than read from the control: reading the
    /// current width would let each rebind raise the floor, so a dropdown widened for a long
    /// name could never narrow again once that name left the inventory.
    /// </param>
    private static void RebindChoices(
        ComboBox combo,
        IReadOnlyList<string> choices,
        string? previous,
        int minimumWidth)
    {
        combo.BeginUpdate();
        combo.Items.Clear();
        combo.Items.Add(AnyChoice);
        foreach (var choice in choices)
        {
            combo.Items.Add(choice);
        }

        // These lists hold the customer's own domain, site, and tag names, so the right width
        // is not knowable when the control is constructed. Never narrower than it started.
        Ui.SizeToWidestItem(combo, minimumWidth);

        // Keep the operator's filter across a refresh where the value still exists.
        combo.SelectedItem = previous is not null && combo.Items.Contains(previous) ? previous : AnyChoice;
        combo.EndUpdate();
    }

    private void ApplyFilters()
    {
        var criteria = new InventoryFilterCriteria
        {
            FqdnContains = _fqdnFilter.Text,
            Domain = Chosen(_domainFilter),
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
                    row.Domain,
                    row.SiteName,
                    row.OsShortName,
                    row.IsReadOnly ? "yes" : string.Empty,
                    string.Join(", ", row.Tags),
                    row.LastDeployedVersion,
                    FormatUtc(row.LastDeployedUtc),
                    appearance.Text);

                var gridRow = _grid.Rows[index];
                gridRow.Tag = row;

                // The column shows the OS with its "Windows Server" prefix dropped; the full
                // string stays one hover away rather than being discarded.
                gridRow.Cells["os"].ToolTipText = row.OsVersion ?? string.Empty;

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

        // Editing tags needs domain controllers to edit them on.
        _editTags.Enabled = _main.Selection.Count > 0;
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

    private async Task OnTagFromFileAsync(Button button)
    {
        using var dialog = new OpenFileDialog
        {
            Title = "Choose a file listing the domain controllers to tag",
            Filter = "Text files (*.txt)|*.txt|All files (*.*)|*.*",
        };

        if (dialog.ShowDialog(this) != DialogResult.OK)
        {
            return;
        }

        // Chosen from the tags that exist rather than typed. A name that does not quite match
        // an existing tag would create a second one and split the group the operator meant to
        // build — invisibly, until a later deployment targeted half of it.
        var tagNames = TagSelectionDialog.Show(
            this,
            Presentation.TagList.Build(_main.AllTags, _main.Inventory),
            dialog.FileName);

        if (tagNames is null || tagNames.Count == 0)
        {
            return;
        }

        using var busy = new BusyScope(this, button, "Tagging...");

        var service = new FileImportService(
            _main.DomainControllers, _main.Tags, _main.Resolver, _main.Logger<FileImportService>());

        ImportReport report;
        try
        {
            report = await service.ImportFileAsync(dialog.FileName, tagNames, false, CancellationToken.None);
        }
        catch (Exception ex) when (ex is InventoryNotEnumeratedException or FileNotFoundException)
        {
            MessageBox.Show(this, ex.Message, "Could not tag from file", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        await _main.RefreshInventoryAsync();

        var message =
            $"{report.Matched.Count} domain controller(s) tagged " +
            string.Join(", ", tagNames.Select(t => $"'{t}'")) + ".";

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

        MessageBox.Show(this, message, "Tagging complete", MessageBoxButtons.OK,
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

/// <summary>
/// A single-line text prompt, since WinForms has no built-in one.
/// </summary>
/// <remarks>
/// Every element sizes itself rather than sitting at a hard-coded pixel position. The first
/// version placed 75-pixel-wide OK and Cancel buttons at absolute coordinates, which clipped
/// their labels — the same mistake as the main window's action bar, in the one place an
/// operator has to read a button to know what it does.
/// </remarks>
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
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Padding = new Padding(14),
        };

        var label = new Label
        {
            Text = message,
            AutoSize = true,
            MaximumSize = new Size(460, 0),
            Margin = new Padding(3, 3, 3, 10),
        };

        var input = new TextBox { Text = initial, Width = 460, Margin = new Padding(3, 3, 3, 12) };

        var ok = Ui.Button("OK");
        ok.DialogResult = DialogResult.OK;

        var cancel = Ui.Button("Cancel");
        cancel.DialogResult = DialogResult.Cancel;

        var buttons = new FlowLayoutPanel
        {
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            FlowDirection = FlowDirection.RightToLeft,
            Dock = DockStyle.Fill,
            WrapContents = false,
        };

        // Right-to-left flow puts Cancel rightmost with OK beside it, the Windows convention.
        buttons.Controls.Add(cancel);
        buttons.Controls.Add(ok);

        var layout = new FlowLayoutPanel
        {
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            FlowDirection = FlowDirection.TopDown,
            WrapContents = false,
            Dock = DockStyle.Fill,
        };

        layout.Controls.AddRange([label, input, buttons]);
        form.Controls.Add(layout);

        form.AcceptButton = ok;
        form.CancelButton = cancel;

        return form.ShowDialog(owner) == DialogResult.OK ? input.Text.Trim() : null;
    }
}

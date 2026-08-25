using HybridAgentDeploy.Core.Models;
using HybridAgentDeploy.Gui.Presentation;

namespace HybridAgentDeploy.Gui.Views;

/// <summary>
/// Tag management (PRD 8.2): create, rename, delete, and apply or remove tags on the current
/// inventory selection.
/// </summary>
internal sealed class TagsTab : UserControl
{
    private readonly MainForm _main;

    private readonly ListBox _tags = new() { Dock = DockStyle.Fill, IntegralHeight = false };

    /// <summary>
    /// Explains what the buttons act on.
    /// </summary>
    /// <remarks>
    /// AutoSize with a docked height rather than a fixed one: the text wraps to three lines on
    /// a narrow window, and a fixed height clipped the last line — which was the line saying
    /// that deleting a tag does not delete domain controllers, the single most reassuring
    /// sentence on the screen.
    /// </remarks>
    private readonly Label _selectionSummary = new()
    {
        Dock = DockStyle.Top,
        AutoSize = true,
        Padding = new Padding(4, 6, 4, 10),
    };

    public TagsTab(MainForm main)
    {
        _main = main;

        var buttons = Ui.Bar(
            NewButton("Create tag...", CreateAsync),
            NewButton("Rename...", RenameAsync),
            NewButton("Delete", DeleteAsync));

        Controls.Add(_tags);
        Controls.Add(_selectionSummary);
        Controls.Add(buttons);

        // The summary wraps against the control's width, which is not known until layout.
        Resize += (_, _) => UpdateSummaryWidth();
    }

    /// <summary>
    /// Rebuilds the tag list from the tag table.
    /// </summary>
    /// <remarks>
    /// Built from every tag that exists, not from the tags present on inventory rows. A tag
    /// applied to nothing is still a tag, and previously it was invisible here — so creating
    /// one looked like it had silently failed.
    /// </remarks>
    public void BindTags()
    {
        var previous = (_tags.SelectedItem as TagSummary)?.Name;

        _tags.BeginUpdate();
        _tags.Items.Clear();

        foreach (var summary in TagList.Build(_main.AllTags, _main.Inventory))
        {
            _tags.Items.Add(summary);
        }

        if (previous is not null)
        {
            var match = _tags.Items.Cast<TagSummary>()
                .FirstOrDefault(t => string.Equals(t.Name, previous, StringComparison.OrdinalIgnoreCase));
            if (match is not null)
            {
                _tags.SelectedItem = match;
            }
        }

        _tags.EndUpdate();

        _selectionSummary.Text =
            "Tags defined in this inventory. Creating, renaming and deleting them happens here; " +
            "applying them to domain controllers happens on the Inventory tab, with \"Edit " +
            "tags...\"." + Environment.NewLine +
            "Deleting a tag removes the association only — no domain controller or deployment " +
            "history is ever deleted.";

        UpdateSummaryWidth();
    }

    private void UpdateSummaryWidth()
    {
        if (Width > 40)
        {
            _selectionSummary.MaximumSize = new Size(Width - 16, 0);
        }
    }

    private async Task CreateAsync(Button _)
    {
        var name = Prompt.ForText(this, "Create tag", "Name for the new tag:");
        if (string.IsNullOrWhiteSpace(name))
        {
            return;
        }

        var existing = _main.AllTags.FirstOrDefault(
            t => string.Equals(t.Name, name, StringComparison.OrdinalIgnoreCase));

        await _main.Tags.GetOrCreateAsync(name, null, CancellationToken.None);
        await _main.RefreshInventoryAsync();

        // Select the new tag so the operator can immediately apply it, and say plainly when
        // the name already existed rather than silently doing nothing.
        SelectByName(name);

        if (existing is not null)
        {
            MessageBox.Show(
                this,
                $"A tag named '{existing.Name}' already exists and has been selected. Tag names " +
                "are matched without regard to case.",
                "Tag already exists",
                MessageBoxButtons.OK,
                MessageBoxIcon.Information);
        }
    }

    private async Task RenameAsync(Button _)
    {
        if (SelectedTag() is not { } tag)
        {
            return;
        }

        var name = Prompt.ForText(this, "Rename tag", $"New name for '{tag.Name}':", tag.Name);
        if (string.IsNullOrWhiteSpace(name) || name == tag.Name)
        {
            return;
        }

        await _main.Tags.RenameAsync(tag.Id, name, CancellationToken.None);
        await _main.RefreshInventoryAsync();
        SelectByName(name);
    }

    private async Task DeleteAsync(Button _)
    {
        if (SelectedTag() is not { } tag)
        {
            return;
        }

        // PRD 8.2 is explicit that this removes associations only, so the confirmation says so
        // — "delete" against a Tier 0 inventory should never leave an operator guessing what
        // it is about to remove.
        var confirmed = MessageBox.Show(
            this,
            $"Delete the tag '{tag.Name}'?" + Environment.NewLine + Environment.NewLine +
            "This removes the tag from every domain controller carrying it. No domain " +
            "controller and no deployment history is deleted.",
            "Delete tag",
            MessageBoxButtons.YesNo,
            MessageBoxIcon.Question) == DialogResult.Yes;

        if (!confirmed)
        {
            return;
        }

        await _main.Tags.DeleteAsync(tag.Id, CancellationToken.None);
        await _main.RefreshInventoryAsync();
    }

    private void SelectByName(string name)
    {
        var match = _tags.Items.Cast<TagSummary>()
            .FirstOrDefault(t => string.Equals(t.Name, name, StringComparison.OrdinalIgnoreCase));

        if (match is not null)
        {
            _tags.SelectedItem = match;
        }
    }

    private TagRecord? SelectedTag()
    {
        if (_tags.SelectedItem is not TagSummary summary)
        {
            MessageBox.Show(this, "Select a tag first.", "No tag selected",
                MessageBoxButtons.OK, MessageBoxIcon.Information);
            return null;
        }

        return _main.AllTags.FirstOrDefault(
            t => string.Equals(t.Name, summary.Name, StringComparison.OrdinalIgnoreCase));
    }

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

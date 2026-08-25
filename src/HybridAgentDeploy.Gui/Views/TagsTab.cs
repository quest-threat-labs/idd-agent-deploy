using HybridAgentDeploy.Core.Models;

namespace HybridAgentDeploy.Gui.Views;

/// <summary>
/// Tag management (PRD 8.2): create, rename, delete, and apply or remove tags on the current
/// inventory selection.
/// </summary>
internal sealed class TagsTab : UserControl
{
    private readonly MainForm _main;

    private readonly ListBox _tags = new() { Dock = DockStyle.Fill, IntegralHeight = false };
    private readonly Label _selectionSummary = new() { Dock = DockStyle.Top, Height = 44, Padding = new Padding(4) };

    public TagsTab(MainForm main)
    {
        _main = main;

        var buttons = Ui.Bar(
            NewButton("Create tag...", CreateAsync),
            NewButton("Rename...", RenameAsync),
            NewButton("Delete", DeleteAsync),
            NewButton("Apply to selection", ApplyToSelectionAsync),
            NewButton("Remove from selection", RemoveFromSelectionAsync));

        Controls.Add(_tags);
        Controls.Add(_selectionSummary);
        Controls.Add(buttons);
    }

    public void BindTags()
    {
        var previous = _tags.SelectedItem as TagListItem;

        _tags.BeginUpdate();
        _tags.Items.Clear();

        foreach (var tag in _main.Inventory
            .SelectMany(row => row.Tags)
            .GroupBy(name => name, StringComparer.OrdinalIgnoreCase)
            .OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase))
        {
            _tags.Items.Add(new TagListItem(tag.Key, tag.Count()));
        }

        if (previous is not null)
        {
            var match = _tags.Items.Cast<TagListItem>()
                .FirstOrDefault(t => string.Equals(t.Name, previous.Name, StringComparison.OrdinalIgnoreCase));
            if (match is not null)
            {
                _tags.SelectedItem = match;
            }
        }

        _tags.EndUpdate();

        _selectionSummary.Text =
            $"{_main.Selection.Count} domain controller(s) selected on the Inventory tab." +
            Environment.NewLine +
            "Applying or removing a tag affects that selection. Deleting a tag removes the " +
            "association only — no domain controller or deployment history is ever deleted.";
    }

    /// <summary>Tags that exist but are applied to nothing are invisible in the inventory join.</summary>
    private async Task<IReadOnlyList<TagRecord>> AllTagsAsync() =>
        await _main.Tags.GetAllAsync(CancellationToken.None);

    private async Task CreateAsync(Button _)
    {
        var name = Prompt.ForText(this, "Create tag", "Name for the new tag:");
        if (string.IsNullOrWhiteSpace(name))
        {
            return;
        }

        await _main.Tags.GetOrCreateAsync(name, null, CancellationToken.None);
        await _main.RefreshInventoryAsync();
        BindTags();
    }

    private async Task RenameAsync(Button _)
    {
        if (await SelectedTagAsync() is not { } tag)
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
        BindTags();
    }

    private async Task DeleteAsync(Button _)
    {
        if (await SelectedTagAsync() is not { } tag)
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
        BindTags();
    }

    private async Task ApplyToSelectionAsync(Button _) => await ChangeSelectionAsync(apply: true);

    private async Task RemoveFromSelectionAsync(Button _) => await ChangeSelectionAsync(apply: false);

    private async Task ChangeSelectionAsync(bool apply)
    {
        if (await SelectedTagAsync() is not { } tag)
        {
            return;
        }

        if (_main.Selection.Count == 0)
        {
            MessageBox.Show(
                this,
                "No domain controllers are selected. Tick them on the Inventory tab first.",
                "Nothing selected",
                MessageBoxButtons.OK,
                MessageBoxIcon.Information);
            return;
        }

        var ids = _main.Selection.SelectedIds.ToList();

        if (apply)
        {
            await _main.Tags.ApplyTagAsync(tag.Id, ids, CancellationToken.None);
        }
        else
        {
            await _main.Tags.RemoveTagAsync(tag.Id, ids, CancellationToken.None);
        }

        await _main.RefreshInventoryAsync();
        BindTags();
    }

    private async Task<TagRecord?> SelectedTagAsync()
    {
        if (_tags.SelectedItem is not TagListItem item)
        {
            MessageBox.Show(this, "Select a tag first.", "No tag selected",
                MessageBoxButtons.OK, MessageBoxIcon.Information);
            return null;
        }

        return (await AllTagsAsync())
            .FirstOrDefault(t => string.Equals(t.Name, item.Name, StringComparison.OrdinalIgnoreCase));
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

    private sealed record TagListItem(string Name, int Count)
    {
        public override string ToString() =>
            $"{Name}  ({Count} domain controller{(Count == 1 ? string.Empty : "s")})";
    }
}

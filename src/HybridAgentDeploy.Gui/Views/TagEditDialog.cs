using HybridAgentDeploy.Gui.Presentation;

namespace HybridAgentDeploy.Gui.Views;

/// <summary>
/// What the operator asked for: tags to apply to, and remove from, the selected controllers.
/// </summary>
public sealed record TagEdits(IReadOnlyList<string> Add, IReadOnlyList<string> Remove);

/// <summary>
/// Adds and removes tags across the domain controllers selected on the Inventory tab.
/// </summary>
/// <remarks>
/// Acts on the ticked controllers — the same selection the Deploy tab uses — so the
/// application has one answer to "which domain controllers am I working with", and a selection
/// built across several filters is not lost by changing one.
/// </remarks>
internal static class TagEditDialog
{
    /// <summary>
    /// Shows the dialog and returns the requested changes, or null if the operator cancelled or
    /// changed nothing.
    /// </summary>
    /// <param name="hiddenSelectedCount">
    /// Selected controllers the current filter is hiding. Stated plainly: the operator is about
    /// to change controllers that are not on screen.
    /// </param>
    public static TagEdits? Show(
        IWin32Window owner,
        IReadOnlyList<TagSummary> allTags,
        IReadOnlyList<InventoryRow> selected,
        int hiddenSelectedCount)
    {
        var model = new TagEditModel(allTags, selected);

        using var form = new Form
        {
            Text = "Edit tags",
            FormBorderStyle = FormBorderStyle.Sizable,
            StartPosition = FormStartPosition.CenterParent,
            MinimizeBox = false,
            MaximizeBox = false,
            ShowInTaskbar = false,
            ClientSize = new Size(560, 500),
            MinimumSize = new Size(480, 420),
            Padding = new Padding(14),
        };

        var intro = new Label
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            Padding = new Padding(0, 0, 0, 6),
            Text = BuildIntro(model.SelectedCount, hiddenSelectedCount),
        };

        var legend = new Label
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            ForeColor = Color.FromArgb(90, 90, 90),
            Padding = new Padding(0, 0, 0, 10),
            Text =
                "A filled box means every selected controller carries the tag; a blank box " +
                "means none do. A square means some do — leave it alone and those controllers " +
                "are not changed.",
        };

        var list = new CheckedListBox
        {
            Dock = DockStyle.Fill,
            CheckOnClick = true,
            IntegralHeight = false,
        };

        var summary = new Label
        {
            Dock = DockStyle.Bottom,
            AutoSize = true,
            Padding = new Padding(0, 8, 0, 8),
        };

        var newTag = Ui.Button("New tag...");
        var removeAll = Ui.Button("Remove all tags");
        var apply = Ui.Button("Apply changes");
        var cancel = Ui.Button("Cancel");

        apply.DialogResult = DialogResult.OK;
        cancel.DialogResult = DialogResult.Cancel;

        void Refresh()
        {
            apply.Enabled = model.HasChanges;
            summary.Text = model.Describe();
            summary.ForeColor = model.HasChanges
                ? Color.FromArgb(11, 79, 25)
                : Color.FromArgb(90, 90, 90);
        }

        BindList(list, model, Refresh);
        Rebuild(list, model);

        removeAll.Click += (_, _) =>
        {
            model.RemoveAll();
            Rebuild(list, model);
            Refresh();
        };

        newTag.Click += (_, _) =>
        {
            var name = Prompt.ForText(
                form, "New tag", "Name for the new tag. It is created when you apply changes.");

            if (string.IsNullOrWhiteSpace(name))
            {
                return;
            }

            var added = model.AddOrSelect(name);
            Rebuild(list, model);

            list.SelectedIndex = model.Entries
                .Select((entry, index) => (entry, index))
                .First(x => ReferenceEquals(x.entry, added)).index;

            Refresh();
        };

        var buttons = new FlowLayoutPanel
        {
            Dock = DockStyle.Bottom,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            FlowDirection = FlowDirection.RightToLeft,
            WrapContents = false,
        };

        buttons.Controls.Add(cancel);
        buttons.Controls.Add(apply);
        buttons.Controls.Add(removeAll);
        buttons.Controls.Add(newTag);

        form.Controls.Add(list);
        form.Controls.Add(summary);
        form.Controls.Add(buttons);
        form.Controls.Add(legend);
        form.Controls.Add(intro);

        form.AcceptButton = apply;
        form.CancelButton = cancel;

        // Wide enough for its own buttons, measured rather than guessed. Four self-sizing
        // buttons overflowed a hard-coded width and the leftmost was clipped to "ew tag...".
        var neededWidth = buttons.PreferredSize.Width + form.Padding.Horizontal + 24;
        form.MinimumSize = new Size(
            Math.Max(form.MinimumSize.Width, neededWidth + (form.Width - form.ClientSize.Width)),
            form.MinimumSize.Height);
        form.ClientSize = new Size(Math.Max(form.ClientSize.Width, neededWidth), form.ClientSize.Height);

        void FitLabels()
        {
            var available = form.ClientSize.Width - form.Padding.Horizontal;
            if (available > 40)
            {
                intro.MaximumSize = new Size(available, 0);
                legend.MaximumSize = new Size(available, 0);
                summary.MaximumSize = new Size(available, 0);
            }
        }

        form.Resize += (_, _) => FitLabels();
        FitLabels();
        Refresh();

        if (form.ShowDialog(owner) != DialogResult.OK || !model.HasChanges)
        {
            return null;
        }

        return new TagEdits(model.TagsToAdd, model.TagsToRemove);
    }

    private static string BuildIntro(int selectedCount, int hiddenSelectedCount)
    {
        var text = selectedCount == 1
            ? "Tags for the 1 selected domain controller."
            : $"Tags for the {selectedCount} selected domain controllers.";

        if (hiddenSelectedCount > 0)
        {
            // The count on screen and the count being changed would otherwise disagree.
            text += Environment.NewLine +
                $"{hiddenSelectedCount} of them are hidden by the current filter and will still " +
                "be changed.";
        }

        return text;
    }

    /// <summary>Repopulates the list from the model, preserving each tag's three-state value.</summary>
    private static void Rebuild(CheckedListBox list, TagEditModel model)
    {
        list.BeginUpdate();
        list.Items.Clear();

        foreach (var entry in model.Entries)
        {
            list.Items.Add(entry);
        }

        // Set states after adding, so ItemCheck does not fire against a half-built list.
        for (var i = 0; i < model.Entries.Count; i++)
        {
            list.SetItemCheckState(i, model.Entries[i].Current);
        }

        list.EndUpdate();
    }

    /// <summary>
    /// Connects the list to the model so ticking a tag records the operator's decision.
    /// </summary>
    /// <remarks>
    /// Separated from the dialog so the wiring can be tested. Driving a
    /// <see cref="CheckedListBox"/> with synthetic input is unreliable, and testing the model
    /// alone would leave the connection between them — which is what decides whether a tag is
    /// applied to a domain controller — unverified.
    /// </remarks>
    internal static void BindList(CheckedListBox list, TagEditModel model, Action onChanged)
    {
        ArgumentNullException.ThrowIfNull(list);
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(onChanged);

        list.ItemCheck += (_, e) =>
        {
            if (list.Items[e.Index] is not TagEditEntry entry)
            {
                return;
            }

            // Clicking an indeterminate box lands on a definite value, which is exactly the
            // point: the operator has now decided for the whole selection.
            model.SetState(entry.Name, e.NewValue);

            if (list.IsHandleCreated)
            {
                list.BeginInvoke(onChanged);
            }
            else
            {
                onChanged();
            }
        };
    }
}

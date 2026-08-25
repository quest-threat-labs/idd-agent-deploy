using HybridAgentDeploy.Gui.Presentation;

namespace HybridAgentDeploy.Gui.Views;

/// <summary>
/// Asks which tags to apply to the hosts in an imported file (PRD 6.2).
/// </summary>
/// <remarks>
/// Replaces a free-text box. Typing a name that did not quite match an existing tag created a
/// second one and split the group the operator was trying to build — silently, and only
/// visible later when a deployment targeted half of it. Choosing from what exists makes the
/// common case a click, and creating a new tag a deliberate act.
/// </remarks>
internal static class TagSelectionDialog
{
    /// <summary>
    /// Shows the dialog and returns the chosen tag names, or null if the operator cancelled.
    /// </summary>
    /// <param name="existingTags">Every tag currently in the inventory.</param>
    /// <param name="fileName">The file being imported, shown for context.</param>
    public static IReadOnlyList<string>? Show(
        IWin32Window owner,
        IReadOnlyList<TagSummary> existingTags,
        string fileName)
    {
        var model = new TagSelectionModel(existingTags);

        using var form = new Form
        {
            Text = "Tags to apply",
            FormBorderStyle = FormBorderStyle.Sizable,
            StartPosition = FormStartPosition.CenterParent,
            MinimizeBox = false,
            MaximizeBox = false,
            ShowInTaskbar = false,
            ClientSize = new Size(540, 460),
            MinimumSize = new Size(460, 380),
            Padding = new Padding(14),
        };

        // AutoSize, not a fixed height. A fixed height clipped the second paragraph — the one
        // telling the operator that importing does not add hosts — leaving the dialog silently
        // less informative than it reads in the source.
        var intro = new Label
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            Padding = new Padding(0, 0, 0, 10),
            Text =
                $"Every domain controller named in {Path.GetFileName(fileName)} will be given " +
                "the tags you select." + Environment.NewLine + Environment.NewLine +
                "Importing applies tags to controllers already discovered from Active " +
                "Directory; it does not add new ones.",
        };

        var list = new CheckedListBox
        {
            Dock = DockStyle.Fill,
            CheckOnClick = true,
            IntegralHeight = false,
        };

        foreach (var choice in model.Choices)
        {
            list.Items.Add(choice, model.IsSelected(choice.Name));
        }

        var summary = new Label
        {
            Dock = DockStyle.Bottom,
            AutoSize = true,
            Padding = new Padding(0, 8, 0, 8),
            ForeColor = Color.FromArgb(90, 90, 90),
        };

        var newTag = Ui.Button("New tag...");
        var ok = Ui.Button("Apply tags");
        var cancel = Ui.Button("Cancel");

        ok.DialogResult = DialogResult.OK;
        cancel.DialogResult = DialogResult.Cancel;

        void Refresh()
        {
            ok.Enabled = model.CanApply;
            summary.Text = model.BlockingReason ?? model.Describe(0);
            summary.ForeColor = model.CanApply
                ? Color.FromArgb(11, 79, 25)
                : Color.FromArgb(90, 90, 90);
        }

        BindList(list, model, Refresh);

        newTag.Click += (_, _) =>
        {
            var name = Prompt.ForText(
                form,
                "New tag",
                "Name for the new tag. It is created when the import runs.");

            if (string.IsNullOrWhiteSpace(name))
            {
                return;
            }

            var added = model.AddOrSelect(name);

            // Rebuild so a newly added tag lands in alphabetical order alongside the rest.
            list.Items.Clear();
            foreach (var choice in model.Choices)
            {
                list.Items.Add(choice, model.IsSelected(choice.Name));
            }

            list.SelectedItem = added;
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
        buttons.Controls.Add(ok);
        buttons.Controls.Add(newTag);

        form.Controls.Add(list);
        form.Controls.Add(summary);
        form.Controls.Add(buttons);
        form.Controls.Add(intro);

        form.AcceptButton = ok;
        form.CancelButton = cancel;

        // The wrapping labels need a width to wrap against, and it changes with the form.
        void FitLabels()
        {
            var available = form.ClientSize.Width - form.Padding.Horizontal;
            if (available > 40)
            {
                intro.MaximumSize = new Size(available, 0);
                summary.MaximumSize = new Size(available, 0);
            }
        }

        form.Resize += (_, _) => FitLabels();
        FitLabels();

        Refresh();

        return form.ShowDialog(owner) == DialogResult.OK ? model.Selected : null;
    }

    /// <summary>
    /// Connects a checked list to the model behind it, so ticking an item selects that tag.
    /// </summary>
    /// <remarks>
    /// Separated from the dialog so the wiring itself can be tested. Driving a
    /// <see cref="CheckedListBox"/> with synthetic mouse and keyboard messages proved
    /// unreliable, and testing only the model would have left the connection between them —
    /// the part that actually decides which tags an import applies — unverified.
    ///
    /// The model is updated synchronously. Only the visual refresh is deferred, because
    /// <c>ItemCheck</c> fires before the item's own state changes.
    /// </remarks>
    internal static void BindList(CheckedListBox list, TagSelectionModel model, Action onChanged)
    {
        ArgumentNullException.ThrowIfNull(list);
        ArgumentNullException.ThrowIfNull(model);

        list.ItemCheck += (_, e) =>
        {
            if (list.Items[e.Index] is not TagSummary tag)
            {
                return;
            }

            model.SetSelected(tag.Name, e.NewValue == CheckState.Checked);

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

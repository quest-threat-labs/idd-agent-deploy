namespace HybridAgentDeploy.Gui.Views;

/// <summary>
/// Shared control construction.
/// </summary>
/// <remarks>
/// <para>
/// Buttons size themselves to their text rather than to a pixel width fixed at authoring
/// time. A hard-coded width looks correct on the machine it was written on and clips its
/// label everywhere else — the first build of this GUI shipped buttons reading "Enumerate
/// from Active" and "Select", because the text needed more room than the number I had
/// guessed. Operators run this on jump hosts with whatever display scaling and font size the
/// customer has set, so the layout has to adapt rather than assume.
/// </para>
/// <para>
/// PRD 8 asks for legibility over polish. A truncated button label is the opposite of legible.
/// </para>
/// </remarks>
internal static class Ui
{
    /// <summary>A button that always fits its own text.</summary>
    public static Button Button(string text) => new()
    {
        Text = text,
        AutoSize = true,
        AutoSizeMode = AutoSizeMode.GrowAndShrink,
        Padding = new Padding(10, 4, 10, 4),
        Margin = new Padding(4),
        MinimumSize = new Size(0, 30),
    };

    public static Label Label(string text) => new()
    {
        Text = text,
        AutoSize = true,
        Margin = new Padding(8, 9, 2, 0),
    };

    /// <summary>
    /// A section heading, sized relative to the font its container actually uses.
    /// </summary>
    /// <remarks>
    /// Never <see cref="SystemFonts.DefaultFont"/>. That is Microsoft Sans Serif 8.25pt — a
    /// legacy default unrelated to what WinForms actually renders with, which on any current
    /// Windows is Segoe UI 9pt. Building headings from it made every section label on the
    /// deployment screen render <em>smaller</em> than the body text beneath it, and in a
    /// different typeface: the exact inverse of what a heading is for.
    /// </remarks>
    public static Label Heading(string text) => new HeadingLabel
    {
        Text = text,
        AutoSize = true,
        Margin = new Padding(0, 14, 0, 4),
    };

    /// <summary>
    /// The best available fixed-width face, for the command preview.
    /// </summary>
    /// <remarks>
    /// <c>FontFamily.GenericMonospace</c> resolves to Courier New, which is both dated and
    /// hard to read at small sizes. The operator is asked to check this text against what will
    /// run on a domain controller, so it is worth choosing a face designed for that.
    /// </remarks>
    public static Font Monospace(float sizePoints)
    {
        foreach (var candidate in new[] { "Cascadia Mono", "Consolas", "Courier New" })
        {
            try
            {
                var font = new Font(candidate, sizePoints);
                if (string.Equals(font.Name, candidate, StringComparison.OrdinalIgnoreCase))
                {
                    return font;
                }

                font.Dispose();
            }
            catch (ArgumentException)
            {
                // The family is not installed; try the next one.
            }
        }

        return new Font(FontFamily.GenericMonospace, sizePoints);
    }

    /// <summary>
    /// A label that derives its font from whatever its container is using.
    /// </summary>
    /// <remarks>
    /// Recomputed when the parent changes rather than fixed at construction, because a control
    /// built before it is added to a form has no way of knowing the ambient font yet. This
    /// also means the headings follow the operator's own font settings rather than a size
    /// hard-coded here.
    /// </remarks>
    private sealed class HeadingLabel : Label
    {
        private Font? _owned;

        protected override void OnParentChanged(EventArgs e)
        {
            base.OnParentChanged(e);

            var basis = Parent?.Font ?? SystemFonts.MessageBoxFont ?? Font;
            var heading = new Font(basis.FontFamily, basis.SizeInPoints + 1f, FontStyle.Bold);

            Font = heading;

            _owned?.Dispose();
            _owned = heading;
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _owned?.Dispose();
                _owned = null;
            }

            base.Dispose(disposing);
        }
    }

    /// <summary>A horizontal strip that sizes to its contents.</summary>
    public static FlowLayoutPanel Row(params Control[] controls)
    {
        var panel = new FlowLayoutPanel
        {
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            WrapContents = false,
            Margin = new Padding(0),
        };

        panel.Controls.AddRange(controls);
        return panel;
    }

    /// <summary>
    /// A top-docked strip of buttons that grows to fit them.
    /// </summary>
    /// <remarks>
    /// Height is left to the layout rather than fixed, so a larger system font pushes the
    /// strip taller instead of cropping the buttons inside it.
    /// </remarks>
    public static FlowLayoutPanel Bar(params Control[] controls)
    {
        var panel = new FlowLayoutPanel
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            WrapContents = true,
            Padding = new Padding(0, 2, 0, 2),
        };

        panel.Controls.AddRange(controls);
        return panel;
    }
}

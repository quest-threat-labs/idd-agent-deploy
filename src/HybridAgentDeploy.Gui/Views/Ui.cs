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

    public static Label Heading(string text) => new()
    {
        Text = text,
        AutoSize = true,
        Font = new Font(SystemFonts.DefaultFont, FontStyle.Bold),
        Margin = new Padding(0, 12, 0, 3),
    };

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

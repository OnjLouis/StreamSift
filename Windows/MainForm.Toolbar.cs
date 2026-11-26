namespace StreamSift;

internal sealed partial class MainForm
{
    private readonly ToolStrip toolbar = new()
    {
        AccessibleName = "StreamSift toolbar", Dock = DockStyle.Top, TabStop = false,
        GripStyle = ToolStripGripStyle.Hidden, ShowItemToolTips = true, Padding = new(9, 2, 4, 2)
    };
    private readonly ToolStripMenuItem showToolbarCommand;
    private ToolStripButton favouriteTool = null!;

    private void BuildToolbar()
    {
        favouriteTool = ToolButton(toggleFavouriteCommand);
        toolbar.Items.AddRange([
            ToolButton(previewCommand), ToolButton(randomCommand), ToolButton(filterCommand),
            new ToolStripSeparator(), ToolButton(showFavouritesCommand), favouriteTool
        ]);
        ApplyToolbarVisibility(preferences.ShowToolbar);
    }

    private static ToolStripButton ToolButton(ToolStripMenuItem command)
    {
        var button = new ToolStripButton
        {
            DisplayStyle = ToolStripItemDisplayStyle.Text, AutoToolTip = false, Padding = new(5, 0, 5, 0)
        };
        var shortcut = command.ShortcutKeyDisplayString;
        Sync();
        command.TextChanged += (_, _) => Sync();
        command.EnabledChanged += (_, _) => Sync();
        button.Click += (_, _) => command.PerformClick();
        return button;

        void Sync()
        {
            button.Text = command.Text?.Replace("&", "", StringComparison.Ordinal);
            button.Enabled = command.Enabled;
            button.ToolTipText = string.IsNullOrEmpty(shortcut) ? button.Text : $"{button.Text} ({shortcut})";
        }
    }

    private void ToggleToolbar() => SafeAction(() =>
    {
        var choice = preferences with { ShowToolbar = !preferences.ShowToolbar };
        if (restoreLastResults) choice.Save(Preferences.UserDirectory);
        preferences = choice;
        ApplyToolbarVisibility(choice.ShowToolbar);
    });

    private void ApplyToolbarVisibility(bool visible)
    {
        toolbar.Visible = visible;
        showToolbarCommand.Checked = visible;
    }
}

using System.ComponentModel;

namespace StreamSift;

// Native Serials' shortcut and focused-help conventions, adapted to the scanner.
internal sealed class ShortcutButton : Button
{
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    internal required string ShortcutText { get; init; }
    protected override AccessibleObject CreateAccessibilityInstance() => new ShortcutObject(this);
    private sealed class ShortcutObject(ShortcutButton owner) : ButtonBaseAccessibleObject(owner)
    {
        public override AccessibleRole Role => AccessibleRole.PushButton;
        public override string KeyboardShortcut => owner.ShortcutText;
    }
}

internal static class MenuCommand
{
    internal static ToolStripMenuItem Create(string text, Action action, Keys shortcut = Keys.None, string? display = null)
    {
        var item = new ToolStripMenuItem(text, null, (_, _) => action());
        if (shortcut != Keys.None) item.ShortcutKeys = shortcut;
        display ??= shortcut == Keys.None ? null : new KeysConverter().ConvertToString(shortcut);
        if (display != null)
        {
            item.ShowShortcutKeys = true;
            item.ShortcutKeyDisplayString = display;
        }
        return item;
    }
}

internal sealed class TextDialog : Form
{
    internal TextDialog(string title, string message, Action? openManual = null, string? primaryAction = null)
    {
        Text = title;
        ClientSize = new(510, 205);
        MinimumSize = new(410, 205);
        StartPosition = FormStartPosition.CenterParent;
        ShowInTaskbar = false;
        MinimizeBox = false;
        MaximizeBox = false;
        var body = new TextBox
        {
            AccessibleName = "Text", Text = message, ReadOnly = true,
            Multiline = true, ScrollBars = ScrollBars.Vertical, Dock = DockStyle.Fill, TabIndex = 0
        };
        var close = new Button { Text = "&Close", AutoSize = true, DialogResult = DialogResult.Cancel };
        var buttons = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, TabIndex = 1 };
        if (primaryAction != null)
            buttons.Controls.Add(new Button { Text = primaryAction, AutoSize = true, DialogResult = DialogResult.OK, TabIndex = 0 });
        if (openManual != null)
        {
            var manual = new Button { Text = "Open &Manual", AutoSize = true, TabIndex = buttons.Controls.Count };
            manual.Click += (_, _) => openManual();
            buttons.Controls.Add(manual);
        }
        close.TabIndex = buttons.Controls.Count;
        buttons.Controls.Add(close);
        var grid = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2, Padding = new(10) };
        grid.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        grid.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        grid.Controls.Add(body, 0, 0);
        grid.Controls.Add(buttons, 0, 1);
        Controls.Add(grid);
        CancelButton = close;
        Shown += (_, _) => { body.Select(0, 0); body.Focus(); };
    }
}

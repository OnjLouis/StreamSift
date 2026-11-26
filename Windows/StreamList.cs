using System.Runtime.InteropServices;

namespace StreamSift;

internal enum StreamAction { Copy = 1, Save, Foobar, OpenDefault, Preview, SelectAll, OpenBrowser, Review, Favourite }

internal sealed class StreamList : ListView
{
    internal const string PlayLabel = "Play result";
    internal const string BrowserLabel = "Open address in browser";
    private const int MinimumNameWidth = 145;
    private bool selectingAll;
    internal event Action<StreamAction>? Command;
    internal bool ShowsFileColumns { get; private set; }
    internal string? CurrentLine => (FocusedItem ?? (SelectedItems.Count > 0 ? SelectedItems[0] : null))?.Text;
    internal bool TracksSelected => SelectedItems.Count > 0 &&
        SelectedItems.Cast<ListViewItem>().All(row => row.Tag is Station { IsTrack: true });

    internal StreamList() => Columns.Add("Stream", 560);

    internal void SetFileColumns(bool show)
    {
        if (ShowsFileColumns == show) return;
        ShowsFileColumns = show;
        OwnerDraw = show;
        Columns[0].Text = show ? "Name" : "Stream";
        HeaderStyle = show ? ColumnHeaderStyle.Nonclickable : ColumnHeaderStyle.Clickable;
        if (show)
        {
            Columns.Add("Format", WidthFor("Ogg Vorbis"));
            Columns.Add("Size", WidthFor("999.9 GiB"), HorizontalAlignment.Right);
            Columns.Add("Date modified", WidthFor("30 September 2030, 23:59"));
        }
        else
        {
            while (Columns.Count > 1) Columns.RemoveAt(Columns.Count - 1);
        }
        ResizeNameColumn();

        int WidthFor(string example) => TextRenderer.MeasureText(example, Font).Width + 20;
    }

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        if (Columns.Count > 0) ResizeNameColumn();
    }

    private void ResizeNameColumn()
    {
        int metadataWidth = Columns.Cast<ColumnHeader>().Skip(1).Sum(column => column.Width);
        Columns[0].Width = Math.Max(MinimumNameWidth,
            ClientSize.Width - SystemInformation.VerticalScrollBarWidth - 4 - metadataWidth);
    }

    protected override void OnDrawColumnHeader(DrawListViewColumnHeaderEventArgs e)
    {
        e.DrawDefault = true;
        base.OnDrawColumnHeader(e);
    }

    protected override void OnDrawSubItem(DrawListViewSubItemEventArgs e)
    {
        if (e.ColumnIndex == 0 && e.Item is FileResultItem file)
        {
            // WinForms uses item.Text as the accessible row name. Keep that text complete and draw the filename here.
            bool selected = file.Selected && (Focused || !HideSelection);
            bool highlighted = selected && (Focused || SystemInformation.HighContrast);
            var background = highlighted ? SystemColors.Highlight : selected ? SystemColors.Control : BackColor;
            var foreground = highlighted ? SystemColors.HighlightText : ForeColor;
            var bounds = e.Bounds;
            bounds.Width = Math.Min(bounds.Width, e.Header?.Width ?? bounds.Width);
            var textBounds = bounds.Width > 8 ? Rectangle.Inflate(bounds, -4, 0) : bounds;
            using var fill = new SolidBrush(background);
            e.Graphics.FillRectangle(fill, bounds);
            TextRenderer.DrawText(e.Graphics, file.DisplayName, Font, textBounds, foreground,
                TextFormatFlags.SingleLine | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis |
                TextFormatFlags.NoPrefix | TextFormatFlags.PreserveGraphicsClipping);
            if (Focused && file.Focused && ShowFocusCues) ControlPaint.DrawFocusRectangle(e.Graphics, bounds, foreground, background);
        }
        else e.DrawDefault = true;
        base.OnDrawSubItem(e);
    }

    internal static bool OpensMenu(Keys key) => key is Keys.Apps or (Keys.Shift | Keys.F10);
    internal static bool PreviewsStream(Keys key) => key == Keys.Enter;
    internal static bool CopiesStream(Keys key) => key == (Keys.Control | Keys.C);
    internal static bool SelectsAll(Keys key) => key == (Keys.Control | Keys.A);
    internal static bool OpensDefaultPlayer(Keys key) => key == (Keys.Shift | Keys.Enter);
    internal static bool OpensBrowser(Keys key) => key == (Keys.Alt | Keys.Enter);

    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        if (keyData == Keys.F4 && CurrentLine != null)
        {
            Command?.Invoke(StreamAction.Review);
            return true;
        }
        if (SelectsAll(keyData) && Items.Count > 0)
        {
            Command?.Invoke(StreamAction.SelectAll);
            return true;
        }
        if (PreviewsStream(keyData) && SelectedItems.Count > 0)
        {
            Command?.Invoke(StreamAction.Preview);
            return true;
        }
        if (CopiesStream(keyData) && SelectedItems.Count > 0)
        {
            Command?.Invoke(StreamAction.Copy);
            return true;
        }
        if (OpensDefaultPlayer(keyData) && SelectedItems.Count > 0)
        {
            Command?.Invoke(StreamAction.OpenDefault);
            return true;
        }
        if (OpensBrowser(keyData) && SelectedItems.Count == 1)
        {
            Command?.Invoke(StreamAction.OpenBrowser);
            return true;
        }
        if (OpensMenu(keyData) && SelectedItems.Count > 0)
        {
            Open(null);
            return true;
        }
        return base.ProcessCmdKey(ref msg, keyData);
    }

    internal void SelectAllRows()
    {
        if (SelectedItems.Count == Items.Count) return;
        selectingAll = true;
        BeginUpdate();
        try { foreach (ListViewItem item in Items) item.Selected = true; }
        finally
        {
            selectingAll = false;
            EndUpdate();
        }
        OnSelectedIndexChanged(EventArgs.Empty);
    }

    protected override void OnSelectedIndexChanged(EventArgs e)
    {
        if (!selectingAll) base.OnSelectedIndexChanged(e);
    }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg != NativeRowMenu.ContextMessage) { base.WndProc(ref m); return; }
        int xy = unchecked((int)m.LParam.ToInt64());
        if (xy == -1) { Open(null); return; }
        var screen = new Point(unchecked((short)xy), unchecked((short)(xy >> 16)));
        if (GetItemAt(PointToClient(screen).X, PointToClient(screen).Y) is not ListViewItem item) return;
        if (!item.Selected)
        {
            SelectedIndices.Clear();
            item.Selected = true;
        }
        item.Focused = true;
        Open(screen);
    }

    protected override void OnItemActivate(EventArgs e)
    {
        base.OnItemActivate(e);
        if (SelectedItems.Count > 0) Command?.Invoke(StreamAction.Preview);
    }

    private void Open(Point? screen)
    {
        if (SelectedItems.Count == 0 || FindForm() is not Form owner) return;
        bool single = SelectedItems.Count == 1;
        var row = (FocusedItem is { Selected: true } focused ? focused : SelectedItems[0]).Bounds;
        var position = screen ?? PointToScreen(new Point(row.Left + 10, row.Bottom));
        var commands = MenuItems(single, TracksSelected);
        if (single && FavouriteCaption is { } caption)
            commands = [.. commands, ((int)StreamAction.Favourite, caption + "\tCtrl+D", true)];
        var picked = (StreamAction)NativeRowMenu.Show(owner, position, commands);
        if (picked != 0) Command?.Invoke(picked);
    }

    internal string? FavouriteCaption;

    internal static (int Id, string Caption, bool Enabled)[] MenuItems(bool single, bool tracks = false) =>
    [
        ((int)StreamAction.Preview, "&" + (tracks ? single ? "Play track" : "Play selected tracks" : PlayLabel) + "\tEnter", single || tracks),
        ((int)StreamAction.OpenBrowser, "&" + BrowserLabel + "\tAlt+Enter", single),
        ((int)StreamAction.Copy, (single ? "&Copy address" : "&Copy addresses") + "\tCtrl+C", true),
        ((int)StreamAction.Review, "Re&view text...\tF4", true),
        ((int)StreamAction.Save, "&Save playlist...\tCtrl+S", true),
        ((int)StreamAction.Foobar, "&Add to Foobar2000\tCtrl+Shift+F", true),
        ((int)StreamAction.OpenDefault, "Save and open in &default player\tShift+Enter", true)
    ];
}

internal sealed class FileResultItem : ListViewItem
{
    internal string DisplayName { get; }

    internal FileResultItem(Station station, string displayName)
    {
        DisplayName = displayName;
        Tag = station;
        if (!station.IsTrack)
        {
            Text = displayName + ", Live stream, " + station.Format +
                (station.Bitrate is int bitrate ? $", {bitrate} kbps" : "") +
                (station.Listeners is int listeners ? ", " + ResultView.ListenerLabel(listeners) : "");
            SubItems.AddRange([station.Format, "", ""]);
            return;
        }
        var size = station.FileSizeLabel ?? "Not listed";
        var modified = station.DateModifiedLabel ?? "Not listed";
        Text = $"{displayName}, {station.Format}, Size {size}, Date modified {modified}";
        SubItems.AddRange([station.Format, size, modified]);
    }
}

internal static class NativeRowMenu
{
    internal const int ContextMessage = 0x007b;
    private const int WmNull = 0;
    private const uint TpmReturnCmd = 0x0100, TpmRightButton = 0x0002, MenuGrayed = 0x0001;

    internal static int Show(Form owner, Point screen, IReadOnlyList<(int Id, string Caption, bool Enabled)> commands)
    {
        nint menu = CreatePopupMenu();
        if (menu == 0) return 0;
        try
        {
            foreach (var (id, caption, enabled) in commands)
                if (!AppendMenu(menu, enabled ? 0 : MenuGrayed, (nuint)id, caption)) return 0;
            int picked = (int)TrackPopupMenuEx(menu, TpmReturnCmd | TpmRightButton, screen.X, screen.Y, owner.Handle, 0);
            PostMessage(owner.Handle, WmNull, 0, 0);
            return picked;
        }
        finally { DestroyMenu(menu); }
    }

    [DllImport("user32.dll")] private static extern nint CreatePopupMenu();
    [DllImport("user32.dll", EntryPoint = "AppendMenuW", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AppendMenu(nint menu, uint flags, nuint id, string caption);
    [DllImport("user32.dll")] private static extern uint TrackPopupMenuEx(nint menu, uint flags, int x, int y, nint owner, nint options);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyMenu(nint menu);
    [DllImport("user32.dll", EntryPoint = "PostMessageW")] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PostMessage(nint handle, uint message, nuint wparam, nint lparam);
}

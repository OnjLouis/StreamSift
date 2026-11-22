using System.Runtime.InteropServices;

namespace StreamSift;

internal enum StreamAction { Copy = 1, Save, Foobar, OpenDefault, Preview, SelectAll, OpenBrowser }

internal sealed class StreamList : ListView
{
    internal const string PlayLabel = "Play stream";
    internal const string BrowserLabel = "Open stream in browser";
    private bool selectingAll;
    internal event Action<StreamAction>? Command;

    internal static bool OpensMenu(Keys key) => key is Keys.Apps or (Keys.Shift | Keys.F10);
    internal static bool PreviewsStream(Keys key) => key == Keys.Enter;
    internal static bool CopiesStream(Keys key) => key == (Keys.Control | Keys.C);
    internal static bool SelectsAll(Keys key) => key == (Keys.Control | Keys.A);
    internal static bool OpensDefaultPlayer(Keys key) => key == (Keys.Shift | Keys.Enter);
    internal static bool OpensBrowser(Keys key) => key == (Keys.Alt | Keys.Enter);

    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
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
        var picked = (StreamAction)NativeRowMenu.Show(owner, position,
        [
            ((int)StreamAction.Preview, PlayLabel + "\tEnter", single),
            ((int)StreamAction.OpenBrowser, BrowserLabel + "\tAlt+Enter", single),
            ((int)StreamAction.Copy, (single ? "Copy stream address" : "Copy stream addresses") + "\tCtrl+C", true),
            ((int)StreamAction.Save, "Save playlist...\tCtrl+S", true),
            ((int)StreamAction.Foobar, "Add to Foobar2000\tCtrl+Shift+F", true),
            ((int)StreamAction.OpenDefault, "Save and open in default player\tShift+Enter", true)
        ]);
        if (picked != 0) Command?.Invoke(picked);
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

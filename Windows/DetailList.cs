namespace StreamSift;

internal enum DetailKind { StreamAddress, FoundOn, Station, Format, Bitrate, Listeners, Selection }
internal enum DetailAction { Search = 1, OpenBrowser, Copy, ScanServer }

internal sealed record DetailRow(DetailKind Kind, string Label, string Value, Uri? Address = null)
{
    internal string Text => Label + ": " + Value;
    internal Uri? Server => Kind == DetailKind.StreamAddress && Address is Uri address
        ? new Uri(address.GetLeftPart(UriPartial.Authority) + "/") : null;

    internal static DetailRow[] Rows(Station? station, int selectedCount)
    {
        if (station == null) return [];
        var rows = new List<DetailRow>
        {
            new(DetailKind.StreamAddress, "Stream address", station.Url.AbsoluteUri, station.Url),
            new(DetailKind.FoundOn, "Found on", station.FoundOn.AbsoluteUri, station.FoundOn),
            new(DetailKind.Station, "Station", station.Name),
            new(DetailKind.Format, "Format", station.Format),
            new(DetailKind.Bitrate, "Bitrate", station.Bitrate is int rate ? $"{rate} kbps" : "not reported")
        };
        if (station.Listeners is int count) rows.Add(new(DetailKind.Listeners, "Listeners at scan", count.ToString()));
        if (selectedCount > 1) rows.Add(new(DetailKind.Selection, "Selection", $"Details for the first of {selectedCount} streams"));
        return rows.ToArray();
    }
}

internal sealed class DetailList : ListView
{
    private DetailRow[] rows = [];
    internal DetailRow? SelectedDetail => SelectedItems.Count == 1 ? SelectedItems[0].Tag as DetailRow : null;
    internal bool CanSearch;
    internal event Action<DetailAction, DetailRow>? Command;

    internal DetailList()
    {
        AccessibleName = "Stream details";
        View = View.Details;
        HeaderStyle = ColumnHeaderStyle.None;
        MultiSelect = false;
        FullRowSelect = true;
        HideSelection = false;
        Columns.Add("Detail", 560);
    }

    internal void ShowRows(DetailRow[] next)
    {
        if (rows.SequenceEqual(next)) return;
        var previous = SelectedDetail?.Kind;
        rows = next;
        BeginUpdate();
        try
        {
            Items.Clear();
            foreach (var row in next)
                Items.Add(new ListViewItem(row.Text) { Tag = row });
            if (next.Length == 0) return;
            var index = Array.FindIndex(next, row => row.Kind == previous);
            SelectRow(Items[Math.Max(0, index)]);
        }
        finally { EndUpdate(); }
    }

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        if (Columns.Count > 0) Columns[0].Width =
            Math.Max(140, ClientSize.Width - SystemInformation.VerticalScrollBarWidth - 4);
    }

    internal static DetailAction? ActionFor(Keys key, DetailRow row, bool canSearch) => key switch
    {
        Keys.Enter when row.Address != null && canSearch => DetailAction.Search,
        Keys.Alt | Keys.Enter when row.Address != null => DetailAction.OpenBrowser,
        Keys.Control | Keys.C => DetailAction.Copy,
        _ => null
    };

    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        if (SelectedDetail is not DetailRow row) return base.ProcessCmdKey(ref msg, keyData);
        if (StreamList.OpensMenu(keyData)) { OpenMenu(null); return true; }
        if (ActionFor(keyData, row, CanSearch) is DetailAction action)
        {
            Command?.Invoke(action, row);
            return true;
        }
        return keyData == Keys.Enter || base.ProcessCmdKey(ref msg, keyData);
    }

    protected override void OnItemActivate(EventArgs e)
    {
        base.OnItemActivate(e);
        if (CanSearch && SelectedDetail is { Address: not null } row) Command?.Invoke(DetailAction.Search, row);
    }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg != NativeRowMenu.ContextMessage) { base.WndProc(ref m); return; }
        int xy = unchecked((int)m.LParam.ToInt64());
        if (xy == -1) { OpenMenu(null); return; }
        var screen = new Point(unchecked((short)xy), unchecked((short)(xy >> 16)));
        var point = PointToClient(screen);
        if (GetItemAt(point.X, point.Y) is not ListViewItem item) return;
        SelectRow(item);
        OpenMenu(screen);
    }

    private void SelectRow(ListViewItem item)
    {
        item.Selected = true;
        item.Focused = true;
        item.EnsureVisible();
    }

    private void OpenMenu(Point? screen)
    {
        if (SelectedDetail is not DetailRow row || FindForm() is not Form owner) return;
        var commands = new List<(int, string, bool)>();
        if (row.Address != null)
        {
            commands.Add(((int)DetailAction.Search, "Search this address\tEnter", CanSearch));
            commands.Add(((int)DetailAction.OpenBrowser, "Open in browser\tAlt+Enter", true));
        }
        commands.Add(((int)DetailAction.Copy, (row.Address == null ? "Copy value" : "Copy address") + "\tCtrl+C", true));
        if (row.Server != null) commands.Add(((int)DetailAction.ScanServer, "Scan stream server", CanSearch));
        var bounds = SelectedItems[0].Bounds;
        var picked = NativeRowMenu.Show(owner, screen ?? PointToScreen(new Point(bounds.Left + 10, bounds.Bottom)), commands);
        if (picked != 0) Command?.Invoke((DetailAction)picked, row);
    }
}

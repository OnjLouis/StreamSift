namespace StreamSift;

internal sealed class ResultDialog : Form
{
    private readonly Action openManual;
    private readonly (Control Input, string Help)[] fields;
    private readonly Button primary;

    private ResultDialog(string title, string action, Action openManual,
        params (string Label, Control Input, string Help)[] rows)
    {
        this.openManual = openManual;
        fields = rows.Select(r => (r.Input, r.Help)).ToArray();
        Text = title;
        StartPosition = FormStartPosition.CenterParent;
        ShowInTaskbar = false;
        MinimizeBox = MaximizeBox = false;
        AutoScaleMode = AutoScaleMode.Font;
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
        var grid = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, AutoSize = true, Padding = new(10) };
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 255));
        for (int row = 0; row < rows.Length; row++)
        {
            var (caption, input, _) = rows[row];
            var label = new Label { Text = caption, Anchor = AnchorStyles.Left, AutoSize = true, TabIndex = 2 * row };
            input.TabIndex = label.TabIndex + 1;
            input.Dock = DockStyle.Fill;
            grid.Controls.Add(label, 0, row);
            grid.Controls.Add(input, 1, row);
        }
        primary = new Button { Text = action, AutoSize = true, DialogResult = DialogResult.OK, TabIndex = 0 };
        var cancel = new Button { Text = "&Cancel", AutoSize = true, DialogResult = DialogResult.Cancel, TabIndex = 1 };
        var buttons = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill, TabIndex = rows.Length * 2, WrapContents = false };
        buttons.Controls.AddRange([primary, cancel]);
        grid.Controls.Add(buttons, 0, rows.Length);
        grid.SetColumnSpan(buttons, 2);
        Controls.Add(grid);
        AcceptButton = primary;
        CancelButton = cancel;
        Shown += (_, _) => fields[0].Input.Focus();
    }

    private static readonly string[] OrderNames =
        ["Search order", "Station name, A to Z", "Station name, Z to A", "Bitrate, highest first", "Bitrate, lowest first",
         "Listeners, most first", "Listeners, fewest first", "Date modified, newest first", "Date modified, oldest first",
         "File size, largest first", "File size, smallest first"];

    internal static ResultDialog Filter(IReadOnlyList<Station> stations, ResultView current, Action openManual, out Func<ResultView> read)
    {
        var formats = ResultView.Formats(stations);
        var orders = ResultView.AvailableOrders(stations);
        bool tracks = stations.Any(s => s.IsTrack);
        var format = new ComboBox { AccessibleName = "Format", DropDownStyle = ComboBoxStyle.DropDownList };
        var sort = new ComboBox { AccessibleName = "Sort by", DropDownStyle = ComboBoxStyle.DropDownList };
        format.Items.Add("All formats");
        format.Items.AddRange(formats);
        sort.Items.AddRange(orders.Select(order => tracks ? order switch
        {
            ResultOrder.Search => "Listed order",
            ResultOrder.NameAscending => "Track name, A to Z",
            ResultOrder.NameDescending => "Track name, Z to A",
            _ => OrderNames[(int)order]
        } : OrderNames[(int)order]).ToArray());
        format.SelectedIndex = Array.IndexOf(formats, current.Format) + 1;
        sort.SelectedIndex = Math.Max(0, Array.IndexOf(orders, current.Order));
        read = () => new(format.SelectedIndex > 0 ? formats[format.SelectedIndex - 1] : null, orders[sort.SelectedIndex]);
        return new ResultDialog("Filter and sort", "&Apply", openManual,
            ("&Format:", format, "Show all formats or a format found in the current search."),
            ("&Sort by:", sort, tracks ? "Choose listed order, track name, date modified or file size where listed. Missing values appear last."
                : "Choose search order, station name, bitrate or listeners where reported. Unknown counts and bitrates appear last."));
    }

    internal static ResultDialog Find(string current, Action openManual, out Func<string> read)
    {
        var input = new TextBox { AccessibleName = "Find what", Text = current, MaxLength = 256 };
        read = () => input.Text.Trim();
        var dialog = new ResultDialog("Find in results", "&Find", openManual,
            ("Find &what:", input, "Find visible results by name, format, address, bitrate or listener count where available."));
        void Update() => dialog.primary.Enabled = !string.IsNullOrWhiteSpace(input.Text);
        input.TextChanged += (_, _) => Update();
        dialog.Shown += (_, _) => input.SelectAll();
        Update();
        return dialog;
    }

    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        if (keyData != Keys.F1) return base.ProcessCmdKey(ref msg, keyData);
        var field = fields.FirstOrDefault(f => f.Input.ContainsFocus);
        using var help = new TextDialog(Text + " help", field.Help ?? fields[0].Help, openManual);
        help.ShowDialog(this);
        return true;
    }
}

namespace StreamSift;

internal sealed class PreferencesDialog : Form
{
    private readonly TabControl tabs = new() { AccessibleName = "Preference categories", Dock = DockStyle.Fill, TabIndex = 0 };
    private readonly CheckBox sounds = new() { Text = "&Search sounds", AutoSize = true };
    private readonly CheckBox fallback = new() { Text = "&Automatically try other search methods", AutoSize = true };
    private readonly ComboBox player = new() { AccessibleName = "Media player", DropDownStyle = ComboBoxStyle.DropDownList,
        Anchor = AnchorStyles.Left, Width = 180 };
    private static readonly PlayerChoice[] PlayerChoices = Enum.GetValues<PlayerChoice>();
    private readonly TextBox playerPath = new() { AccessibleName = "Custom player file", Dock = DockStyle.Fill, MaxLength = 1024 };
    private readonly Button choosePlayer = new() { Text = "Choose pla&yer...", AutoSize = true };
    private readonly TextBox folder = new() { AccessibleName = "Playlist folder", Dock = DockStyle.Fill, MaxLength = 1024 };
    private readonly Button browse = new() { Text = "&Browse...", AutoSize = true };
    private readonly Button defaults = new() { Text = "Use &default folder", AutoSize = true };
    private readonly CheckBox updates = new() { Text = "Check for &updates at startup", AutoSize = true };
    private readonly Action openManual;
    internal Preferences Value { get; private set; }

    internal PreferencesDialog(Preferences current, Action openManual)
    {
        Value = current;
        this.openManual = openManual;
        Text = "StreamSift Preferences";
        ClientSize = new(630, 320);
        MinimumSize = new(480, 315);
        StartPosition = FormStartPosition.CenterParent;
        ShowInTaskbar = false;
        MinimizeBox = false;
        MaximizeBox = false;
        AutoScaleMode = AutoScaleMode.Font;
        sounds.Checked = current.SearchSounds;
        fallback.Checked = current.AutomaticFallback;
        updates.Checked = current.CheckUpdatesAtStartup;
        folder.Text = current.ExportFolder;
        player.Items.AddRange(PlayerChoices.Select(StreamPreview.DisplayName).ToArray());
        player.SelectedIndex = Array.IndexOf(PlayerChoices, current.MediaPlayer);
        playerPath.Text = current.PlayerPath ?? "";

        var general = new TableLayoutPanel { RowCount = 6, ColumnCount = 3, Dock = DockStyle.Top, AutoSize = true, Padding = new(8) };
        general.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        general.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        general.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        var playerLabel = new Label { Text = "&Media player:", AutoSize = true, Anchor = AnchorStyles.Left, TabIndex = 2 };
        var playerPathLabel = new Label { Text = "Custom player &file:", AutoSize = true, Anchor = AnchorStyles.Left, TabIndex = 4 };
        var label = new Label { Text = "&Playlist folder:", AutoSize = true, Anchor = AnchorStyles.Left, TabIndex = 7 };
        sounds.TabIndex = 0;
        fallback.TabIndex = 1;
        player.TabIndex = 3;
        playerPath.TabIndex = 5;
        choosePlayer.TabIndex = 6;
        folder.TabIndex = 8;
        browse.TabIndex = 9;
        defaults.TabIndex = 10;
        general.Controls.Add(sounds, 0, 0);
        general.SetColumnSpan(sounds, 3);
        general.Controls.Add(fallback, 0, 1);
        general.SetColumnSpan(fallback, 3);
        general.Controls.Add(playerLabel, 0, 2);
        general.Controls.Add(player, 1, 2);
        general.Controls.Add(playerPathLabel, 0, 3);
        general.Controls.Add(playerPath, 1, 3);
        general.Controls.Add(choosePlayer, 2, 3);
        general.Controls.Add(label, 0, 4);
        general.Controls.Add(folder, 1, 4);
        general.Controls.Add(browse, 2, 4);
        general.Controls.Add(defaults, 1, 5);
        var generalTab = new TabPage("General");
        generalTab.Controls.Add(general);
        var updateTab = new TabPage("Updates") { Padding = new(8) };
        updates.Dock = DockStyle.Top;
        updateTab.Controls.Add(updates);
        tabs.TabPages.AddRange([generalTab, updateTab]);

        var ok = new Button { Text = "&OK", AutoSize = true, TabIndex = 0 };
        var cancel = new Button { Text = "&Cancel", AutoSize = true, TabIndex = 1, DialogResult = DialogResult.Cancel };
        var buttons = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, TabIndex = 1, WrapContents = false };
        buttons.Controls.AddRange([ok, cancel]);
        var grid = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 2, ColumnCount = 1, Padding = new(10) };
        grid.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        grid.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        grid.Controls.Add(tabs, 0, 0);
        grid.Controls.Add(buttons, 0, 1);
        Controls.Add(grid);
        AcceptButton = ok;
        CancelButton = cancel;
        Shown += (_, _) => tabs.Focus();
        ok.Click += (_, _) => Commit();
        browse.Click += (_, _) =>
        {
            using var picker = new FolderBrowserDialog
            {
                Description = "Choose the playlist folder", UseDescriptionForTitle = true,
                SelectedPath = Directory.Exists(folder.Text) ? folder.Text : ""
            };
            if (picker.ShowDialog(this) == DialogResult.OK) folder.Text = picker.SelectedPath;
        };
        defaults.Click += (_, _) => folder.Text = Preferences.DefaultPlaylistFolder;
        choosePlayer.Click += (_, _) =>
        {
            var chosen = playerPath.Text.Trim().Trim('"');
            using var picker = new OpenFileDialog
            {
                Title = "Choose VLC, Foobar2000 or FastPlay", CheckFileExists = true,
                Filter = "Supported players (vlc.exe, foobar2000.exe, FastPlay.exe)|vlc.exe;foobar2000.exe;FastPlay.exe",
                InitialDirectory = Path.IsPathFullyQualified(chosen) && Path.GetDirectoryName(chosen) is string parent &&
                    Directory.Exists(parent) ? parent : ""
            };
            if (picker.ShowDialog(this) != DialogResult.OK) return;
            playerPath.Text = picker.FileName;
            playerPath.Focus();
        };
    }

    internal void Commit()
    {
        string? customPlayer;
        // A saved portable player may be on a drive that is not connected now; keep it unless it was changed.
        var typedPlayer = playerPath.Text.Trim().Trim('"');
        try { customPlayer = Value.PlayerPath != null && typedPlayer.Equals(Value.PlayerPath, StringComparison.OrdinalIgnoreCase)
            ? Value.PlayerPath : Preferences.NormalizePlayer(playerPath.Text); }
        catch (Exception e) when (e is ArgumentException or NotSupportedException or IOException)
        {
            Reject("Custom player", e.Message, playerPath);
            return;
        }
        try
        {
            var path = Preferences.NormalizeFolder(folder.Text);
            if (string.Equals(path, Preferences.DefaultPlaylistFolder, StringComparison.OrdinalIgnoreCase)) path = null;
            Value = new Preferences(sounds.Checked, updates.Checked, path, fallback.Checked,
                player.SelectedIndex >= 0 ? PlayerChoices[player.SelectedIndex] : PlayerChoice.Automatic, customPlayer);
            DialogResult = DialogResult.OK;
        }
        catch (Exception e) when (e is ArgumentException or NotSupportedException or IOException)
        {
            Reject("Playlist folder", e.Message, folder);
        }
    }

    private void Reject(string title, string text, TextBox field)
    {
        using var message = new TextDialog(title, text);
        message.ShowDialog(this);
        tabs.SelectedIndex = 0;
        field.SelectAll();
        field.Focus();
    }

    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        if (keyData != Keys.F1) return base.ProcessCmdKey(ref msg, keyData);
        string text = sounds.Focused ? "Play a sound when a search starts and another when it finishes." :
            fallback.Focused ? "If the chosen method finds no playable stream or cannot finish, try the other methods. This can start a temporary browser and check the public directory. Escape cancels the whole search." :
            player.Focused ? "Choose which player opens the stream when you press Enter. Automatic prefers a supported default playlist app, then looks for VLC, Foobar2000 and FastPlay. Shift+Enter still uses the Windows default." :
            playerPath.Focused || choosePlayer.Focused ? "For a player outside Program Files, such as a portable copy, choose its vlc.exe, foobar2000.exe or FastPlay.exe. Enter uses it when Media player is Automatic or set to that player. Add to Foobar2000 uses a chosen Foobar2000. For a PortableApps copy, choose vlc.exe inside its App folder, not the launcher. Leave blank to look in the usual places." :
            folder.Focused || browse.Focused || defaults.Focused ?
                "Choose where Shift+Enter saves playlists. Ctrl+S starts its Save dialog in the same folder." :
            updates.Focused || tabs.SelectedIndex == 1 ?
                "Check for a newer signed release when StreamSift opens. Updates are installed only after you choose Install update." :
                "Set up search options, playback, playlist files and update checks.";
        using var help = new TextDialog("Preferences help", text, openManual);
        help.ShowDialog(this);
        return true;
    }

    internal bool CheckControls() => tabs.TabCount == 2 && tabs.TabPages[0].Text == "General" &&
        tabs.TabPages[1].Text == "Updates" && folder.AccessibleName == "Playlist folder" &&
        fallback.Text == "&Automatically try other search methods" && fallback.Checked == Value.AutomaticFallback &&
        player.AccessibilityObject.Name == "Media player" && player.DropDownStyle == ComboBoxStyle.DropDownList &&
        player.Items.Cast<string>().SequenceEqual(["Automatic", "VLC", "Foobar2000", "FastPlay"]) &&
        player.SelectedItem as string == StreamPreview.DisplayName(Value.MediaPlayer) &&
        player.Parent?.GetNextControl(player.Parent.Controls.Cast<Control>().Single(c => c.Text == "&Media player:"), true) == player &&
        playerPath.AccessibilityObject.Name == "Custom player file" && playerPath.Text == (Value.PlayerPath ?? "") &&
        playerPath.Parent?.GetNextControl(playerPath.Parent.Controls.Cast<Control>().Single(c => c.Text == "Custom player &file:"), true) == playerPath &&
        choosePlayer.Text == "Choose pla&yer..." && !playerPath.AcceptsTab &&
        !folder.AcceptsTab && sounds.TabIndex < fallback.TabIndex && fallback.TabIndex < player.TabIndex && player.TabIndex < playerPath.TabIndex &&
        playerPath.TabIndex < choosePlayer.TabIndex && choosePlayer.TabIndex < folder.TabIndex &&
        folder.TabIndex < browse.TabIndex && browse.TabIndex < defaults.TabIndex &&
        AcceptButton is Button { Text: "&OK" } && CancelButton is Button { Text: "&Cancel" };
}

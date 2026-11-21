using System.Diagnostics;
using System.Windows.Forms.Automation;

namespace StreamSift;

internal sealed class MainForm : Form
{
    private readonly HttpClient client = PublicNetwork.CreateClient();
    private readonly TextBox url = new() { AccessibleName = "Website address", Dock = DockStyle.Fill, MaxLength = 4096, TabIndex = 0 };
    private readonly ShortcutButton find = new() { Text = "Find streams", ShortcutText = "Ctrl+Enter", AutoSize = true, TabIndex = 1 };
    private readonly ShortcutButton cancel = new() { Text = "Cancel", ShortcutText = "Esc", AutoSize = true, Enabled = false, TabIndex = 2 };
    private readonly StreamList results = new()
    {
        AccessibleName = "Streams", View = View.Details, FullRowSelect = true, HideSelection = false, MultiSelect = false,
        Dock = DockStyle.Fill, TabIndex = 1
    };
    private readonly TextBox details = new()
    {
        AccessibleName = "Stream details", ReadOnly = true, Multiline = true, ScrollBars = ScrollBars.Vertical,
        Dock = DockStyle.Fill, TabIndex = 2
    };
    private readonly TextBox status = new() { AccessibleName = "Status", ReadOnly = true, Dock = DockStyle.Fill, TabIndex = 3 };
    private readonly Dictionary<Control, string> help = new();
    private readonly ToolStripMenuItem copyCommand, saveCommand, foobarCommand, openPlayerCommand, preferencesCommand, updateCommand;
    private readonly SearchSounds sounds = new();
    private readonly System.Windows.Forms.Timer handoffs = new() { Interval = 30000 };
    private Preferences preferences = Preferences.Load(Preferences.UserDirectory);
    private CancellationTokenSource? scan;
    private CancellationTokenSource? updateCheck;
    private CancellationTokenSource? startupUpdate;
    private bool pendingUpdateNotice;
    private int requestCount;
    private bool closing;

    internal MainForm()
    {
        Text = Program.Name;
        ClientSize = new(720, 480);
        MinimumSize = new(540, 370);
        StartPosition = FormStartPosition.CenterScreen;
        AutoScaleMode = AutoScaleMode.Font;
        sounds.Enabled = preferences.SearchSounds;
        handoffs.Tick += (_, _) => CleanHandoffs();

        var menus = new MenuStrip();
        var file = new ToolStripMenuItem("&File");
        file.DropDownItems.Add(MenuCommand.Create("New search", ResetSearch, Keys.Control | Keys.N));
        saveCommand = MenuCommand.Create("Save playlist...", Export, Keys.Control | Keys.S);
        file.DropDownItems.Add(saveCommand);
        file.DropDownItems.Add(new ToolStripSeparator());
        file.DropDownItems.Add(MenuCommand.Create("E&xit", Close));
        var actions = new ToolStripMenuItem("&Actions");
        copyCommand = MenuCommand.Create("Copy stream address", Copy, display: "Ctrl+C");
        foobarCommand = MenuCommand.Create("Add to Foobar2000", AddToFoobar, Keys.Control | Keys.Shift | Keys.O);
        openPlayerCommand = MenuCommand.Create("Open in default player", OpenInDefaultPlayer, display: "Shift+Enter");
        actions.DropDownItems.AddRange([copyCommand, foobarCommand, openPlayerCommand]);
        var options = new ToolStripMenuItem("&Options");
        preferencesCommand = MenuCommand.Create("Preferences...", ShowPreferences, Keys.Control | Keys.Oemcomma, "Ctrl+,");
        options.DropDownItems.Add(preferencesCommand);
        var support = new ToolStripMenuItem("&Help");
        support.DropDownItems.Add(MenuCommand.Create("Context help", ShowHelp, display: "F1"));
        support.DropDownItems.Add(MenuCommand.Create("Open &manual", OpenManual));
        updateCommand = MenuCommand.Create("Check for updates...", () => _ = CheckForUpdatesAsync(), Keys.Shift | Keys.F1);
        support.DropDownItems.Add(updateCommand);
        support.DropDownItems.Add(MenuCommand.Create("GitHub project", () => SafeAction(() => Open("https://github.com/OnjLouis/StreamSift")), Keys.Control | Keys.F1));
        support.DropDownItems.Add(MenuCommand.Create("&Donate", () => SafeAction(() => Open("https://onj.me/donate"))));
        support.DropDownItems.Add(MenuCommand.Create("A&bout", () => ShowText("About StreamSift", "StreamSift " + Program.Version + "\r\nBy Andre Louis")));
        menus.Items.AddRange([file, actions, options, support]);
        MainMenuStrip = menus;

        var entry = new TableLayoutPanel
        {
            Dock = DockStyle.Fill, AutoSize = true, ColumnCount = 4, RowCount = 1, Margin = new(0), TabIndex = 0
        };
        entry.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        entry.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        entry.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        entry.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        var urlLabel = new Label { Text = "&Website address:", Anchor = AnchorStyles.Left, AutoSize = true, TabIndex = 0 };
        url.TabIndex = 1;
        find.TabIndex = 2;
        cancel.TabIndex = 3;
        entry.Controls.Add(urlLabel, 0, 0);
        entry.Controls.Add(url, 1, 0);
        entry.Controls.Add(find, 2, 0);
        entry.Controls.Add(cancel, 3, 0);

        results.Columns.Add("Stream", 560);
        results.SelectedIndexChanged += (_, _) => UpdateSelection();
        results.Command += command =>
        {
            switch (command)
            {
                case StreamAction.Copy: Copy(); break;
                case StreamAction.Save: Export(); break;
                case StreamAction.Foobar: AddToFoobar(); break;
                case StreamAction.OpenDefault: OpenInDefaultPlayer(); break;
            }
        };
        results.Resize += (_, _) => results.Columns[0].Width =
            Math.Max(145, results.ClientSize.Width - SystemInformation.VerticalScrollBarWidth - 4);

        var grid = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 4, Padding = new(12) };
        grid.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        grid.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        grid.RowStyles.Add(new RowStyle(SizeType.Absolute, 95));
        grid.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        grid.Controls.Add(entry, 0, 0);
        grid.Controls.Add(results, 0, 1);
        grid.Controls.Add(details, 0, 2);
        grid.Controls.Add(status, 0, 3);
        Controls.Add(grid);
        Controls.Add(menus);

        help[url] = "Enter or paste a public station website, player page or stream address. Press Enter to find streams.";
        help[find] = "Scan this address for live audio. The app checks the page, player links and playlists for a playable stream.";
        help[cancel] = "Stop the current search. You can also press Escape.";
        help[results] = "Move with the arrow keys. Ctrl+C copies the stream address. Enter opens its context menu. Shift+Enter saves a playlist and opens it in your default player.";
        help[details] = "Review the stream address and the page where it was found. Ctrl+C copies selected text.";
        help[status] = "Review the last result or operation. A search with no results does not prove that the website has no live stream.";
        find.Click += async (_, _) => await FindAsync();
        cancel.Click += (_, _) => scan?.Cancel();
        Shown += async (_, _) =>
        {
            url.Focus();
            CleanHandoffs();
            await CheckAtStartupAsync();
        };
        FormClosing += (_, e) =>
        {
            if (updateCheck != null) { updateCheck.Cancel(); e.Cancel = true; return; }
            closing = true;
            scan?.Cancel();
            startupUpdate?.Cancel();
        };
        Disposed += (_, _) => { sounds.Dispose(); client.Dispose(); handoffs.Dispose(); };
        UpdateSelection();
        SetStatus("Ready.");
    }

    private Station? Selected => results.SelectedItems.Count == 0 ? null : results.SelectedItems[0].Tag as Station;

    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        if (IsSearchKey(keyData, url.Focused)) { if (find.Enabled) find.PerformClick(); return true; }
        if (keyData == Keys.Escape && scan != null) { scan.Cancel(); return true; }
        if (keyData == Keys.F1) { ShowHelp(); return true; }
        return base.ProcessCmdKey(ref msg, keyData);
    }

    private static bool IsSearchKey(Keys key, bool inAddress) =>
        key == (Keys.Control | Keys.Enter) || (inAddress && key == Keys.Enter);

    private async Task FindAsync()
    {
        if (scan != null || updateCheck != null) return;
        if (!PublicNetwork.TryInput(url.Text, out var address))
        {
            SetStatus("Enter a public HTTP or HTTPS address.");
            status.Focus();
            return;
        }
        var operation = new CancellationTokenSource();
        scan = operation;
        bool focusCancel = find.Focused || results.Focused || details.Focused;
        cancel.Enabled = true;
        if (focusCancel) cancel.Focus();
        ClearResults();
        requestCount = 0;
        SetStatus("Searching...");
        sounds.SearchStarted();
        url.ReadOnly = true;
        find.Enabled = false;
        var progress = new Progress<int>(n => { if (scan == operation) requestCount = n; });
        var focusResults = false;
        try
        {
            var found = await Task.Run(() => new Discovery(client).FindAsync(address, operation.Token, progress));
            if (closing) return;
            results.BeginUpdate();
            try
            {
                var names = DisplayNames(found.Stations);
                for (int i = 0; i < found.Stations.Count; i++)
                {
                    results.Items.Add(ResultRow(found.Stations[i], names[i]));
                }
                if (results.Items.Count != 0)
                {
                    results.Items[0].Selected = true;
                    results.Items[0].Focused = true;
                    focusResults = true;
                }
            }
            finally { results.EndUpdate(); }
            SetStatus((found.Stations.Count switch
                { 0 => "No live streams verified.", 1 => "1 stream found.", _ => $"{found.Stations.Count} streams found." }) +
                $" Checked {found.Requests} {(found.Requests == 1 ? "address" : "addresses")}." +
                (found.Unreachable > 0 ? $" {found.Unreachable} unavailable." : "") +
                (found.LimitReached ? " Search limit reached." : ""));
            sounds.SearchCompleted();
        }
        catch (OperationCanceledException) { if (!closing) SetStatus($"Search cancelled after {requestCount} request(s)."); }
        catch (Exception e) when (e is HttpRequestException or IOException or InvalidOperationException)
        {
            if (!closing) SetStatus("Search could not finish. Try the station's player page.");
        }
        finally
        {
            scan = null;
            operation.Dispose();
            if (!closing)
            {
                // Remember the next focus inside this window even when the app is in the background.
                var focus = IsSearchControl(ActiveControl) || url.Focused || cancel.Focused || find.Focused;
                url.ReadOnly = false;
                find.Enabled = true;
                UpdateSelection();
                if (focus)
                {
                    var destination = focusResults ? (Control)results : status;
                    ActiveControl = destination;
                    if (ContainsFocus) destination.Focus();
                }
                cancel.Enabled = false;
                if (pendingUpdateNotice) SetStatus(status.Text);
            }
        }
    }

    private bool IsSearchControl(Control? control) => control == url || control == cancel || control == find;

    private async Task CheckForUpdatesAsync()
    {
        if (updateCheck != null) return;
        if (scan != null) { ShowText("Check for updates", "Finish or cancel the search before checking for updates."); return; }
        var key = AppUpdate.VerificationKey();
        if (key == null) { ShowText("Check for updates", "There is no public update channel for this build of StreamSift."); return; }
        startupUpdate?.Cancel();
        pendingUpdateNotice = false;
        using var operation = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        updateCheck = operation;
        updateCommand.Enabled = false;
        find.Enabled = false;
        bool installing = false;
        try
        {
            var policy = AppUpdate.Policy(key);
            using var client = AppUpdate.Client();
            SetStatus("Checking for updates...");
            var offer = await AppUpdate.CheckAsync(client, operation.Token);
            if (offer == null)
            {
                SetStatus("No newer update found.");
                ShowText("Check for updates", "StreamSift is up to date. Version " + Program.Version + ".");
                return;
            }
            AppUpdate.CheckOfferAddresses(offer);
            var notes = string.IsNullOrWhiteSpace(offer.Notes) ? "" :
                "\r\n\r\n" + new string(offer.Notes.Take(12000).Where(c => !char.IsControl(c) || c is '\r' or '\n' or '\t').ToArray());
            using var dialog = new TextDialog("Update available",
                $"StreamSift {offer.Version.ToString(3)} is available. Install to close and reopen StreamSift." + notes,
                primaryAction: "Install &update");
            if (dialog.ShowDialog(this) != DialogResult.OK) { SetStatus("Update declined."); return; }
            SetStatus("Downloading and verifying the update...");
            await AppUpdate.StageAndStartAsync(client, offer, policy, operation.Token);
            installing = true;
        }
        catch (OperationCanceledException) { SetStatus("Update check ended without changes."); }
        catch (Exception error) when (error is IOException or InvalidDataException or HttpRequestException or System.Security.Cryptography.CryptographicException or
            System.ComponentModel.Win32Exception or InvalidOperationException or System.Xml.XmlException or System.Runtime.Serialization.SerializationException)
        {
            SetStatus("Update could not finish.");
            ShowText("Check for updates", error.Message);
        }
        finally
        {
            updateCheck = null;
            if (installing) Close();
            else
            {
                updateCommand.Enabled = true;
                find.Enabled = true;
            }
        }
    }

    private async Task CheckAtStartupAsync()
    {
        if (!preferences.CheckUpdatesAtStartup || AppUpdate.VerificationKey() == null) return;
        using var operation = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        startupUpdate = operation;
        try
        {
            using var updates = AppUpdate.Client();
            var offer = await AppUpdate.CheckAsync(updates, operation.Token);
            if (offer != null && !operation.IsCancellationRequested && !closing && preferences.CheckUpdatesAtStartup)
            {
                AppUpdate.CheckOfferAddresses(offer);
                pendingUpdateNotice = true;
                if (scan == null) SetStatus(status.Text);
            }
        }
        catch (Exception e) when (e is OperationCanceledException or HttpRequestException or IOException or
            InvalidDataException or System.Runtime.Serialization.SerializationException or UriFormatException) { }
        finally { startupUpdate = null; }
    }

    private void ShowPreferences()
    {
        using var dialog = new PreferencesDialog(preferences, OpenManual);
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        SafeAction(() =>
        {
            dialog.Value.Save(Preferences.UserDirectory);
            preferences = dialog.Value;
            sounds.Enabled = preferences.SearchSounds;
            if (!preferences.CheckUpdatesAtStartup)
            {
                startupUpdate?.Cancel();
                pendingUpdateNotice = false;
            }
            SetStatus("Preferences saved.");
        });
    }

    private static string[] DisplayNames(IReadOnlyList<Station> stations)
    {
        var duplicates = stations.GroupBy(s => (s.Name, s.Format, s.Bitrate))
            .Where(g => g.Skip(1).Any()).Select(g => g.Key).ToHashSet();
        return stations.Select(s => duplicates.Contains((s.Name, s.Format, s.Bitrate))
            ? $"{s.Name} ({s.Url.IdnHost}{s.Url.AbsolutePath})" : s.Name).ToArray();
    }

    private static ListViewItem ResultRow(Station station, string name) =>
        new(name + ", " + station.Format + (station.Bitrate is int bitrate ? $", {bitrate} kbps" : ""))
        { Tag = station };

    private void UpdateSelection()
    {
        var selected = Selected;
        results.TabStop = results.Items.Count != 0;
        details.TabStop = selected != null;
        copyCommand.Enabled = foobarCommand.Enabled = saveCommand.Enabled = openPlayerCommand.Enabled = selected != null;
        details.Text = selected == null ? "" : string.Join("\r\n",
            selected.Name, "Format: " + selected.Format,
            selected.Bitrate is int bitrate ? $"Bitrate: {bitrate} kbps" : "Bitrate: not reported",
            "Stream address:", selected.Url.AbsoluteUri, "Found on:", selected.FoundOn.AbsoluteUri);
        details.Select(0, 0);
    }

    private void ClearResults()
    {
        results.Items.Clear();
        UpdateSelection();
    }

    private void ResetSearch()
    {
        if (scan != null) return;
        url.Clear();
        url.Focus();
        ClearResults();
        SetStatus("Ready.");
    }

    private void SetStatus(string message)
    {
        if (pendingUpdateNotice && scan == null && updateCheck == null)
        {
            const string notice = "An update is available. Press Shift+F1 to review it.";
            message = message == "Ready." ? notice : message + " " + notice;
            pendingUpdateNotice = false;
        }
        if (status.Text == message) return;
        status.Text = message;
        status.Select(0, 0);
        if (ContainsFocus && !status.Focused)
            status.AccessibilityObject.RaiseAutomationNotification(AutomationNotificationKind.Other,
                AutomationNotificationProcessing.MostRecent, message);
    }

    private void Copy()
    {
        if (Selected is not Station item) return;
        SafeAction(() => { Clipboard.SetText(item.Url.AbsoluteUri); SetStatus("Stream address copied."); });
    }

    private void Export()
    {
        if (Selected is not Station item) return;
        SafeAction(() =>
        {
            Directory.CreateDirectory(preferences.ExportFolder);
            using var dialog = new SaveFileDialog
            {
                Title = "Save stream playlist", Filter = "M3U playlist (*.m3u8)|*.m3u8",
                AddExtension = true, DefaultExt = "m3u8", InitialDirectory = preferences.ExportFolder,
                FileName = PlaylistFile.SuggestedName(item.Name)
            };
            if (dialog.ShowDialog(this) != DialogResult.OK) return;
            PlaylistFile.Write(dialog.FileName, item, replace: true);
            SetStatus("Playlist saved: " + Path.GetFileName(dialog.FileName));
        });
    }

    private void OpenInDefaultPlayer()
    {
        if (Selected is not Station item) return;
        SafeAction(() =>
        {
            var path = PlaylistFile.SaveNew(preferences.ExportFolder, item);
            SetStatus("Playlist saved: " + Path.GetFileName(path));
            try { Open(path); }
            catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException)
            {
                ShowText("Could not open the playlist", "The playlist was saved at:\r\n" + path +
                    "\r\n\r\nWindows could not open it. Check the app associated with .m3u8 files.");
            }
        });
    }

    private void AddToFoobar()
    {
        if (Selected is not Station item) return;
        var executable = new[] { Environment.SpecialFolder.ProgramFiles, Environment.SpecialFolder.ProgramFilesX86 }
            .Select(root => Path.Combine(Environment.GetFolderPath(root), "foobar2000", "foobar2000.exe"))
            .FirstOrDefault(File.Exists);
        if (executable == null)
        {
            ShowText("Foobar2000", "Foobar2000 was not found in Program Files. Copy the stream address or save the playlist to open it in your player.");
            return;
        }
        SafeAction(() =>
        {
            var path = FoobarHandoff.Stage(FoobarHandoff.Folder, item);
            try
            {
                using var process = Process.Start(FoobarHandoff.Command(executable, path));
                if (process == null) throw new IOException("Foobar2000 could not start.");
            }
            catch
            {
                try { File.Delete(path); } catch (IOException) { } catch (UnauthorizedAccessException) { }
                throw;
            }
            handoffs.Start();
            SetStatus(item.Name + " sent to Foobar2000.");
        });
    }

    private void CleanHandoffs()
    {
        try { handoffs.Enabled = FoobarHandoff.Prune(FoobarHandoff.Folder, DateTime.UtcNow) > 0; }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { handoffs.Stop(); }
    }

    private void ShowHelp()
    {
        var focused = help.Keys.FirstOrDefault(c => c.Focused);
        using var dialog = new TextDialog("Context help",
            focused == null ? help[url] : help[focused], OpenManual);
        dialog.ShowDialog(this);
        if (focused?.CanFocus == true) focused.Focus();
    }

    private void ShowText(string title, string body)
    {
        using var dialog = new TextDialog(title, body);
        dialog.ShowDialog(this);
    }

    private void OpenManual() => SafeAction(() => Open(Path.Combine(AppContext.BaseDirectory, "Manual.html")));
    private static void Open(string path) { using var process = Process.Start(new ProcessStartInfo(path) { UseShellExecute = true }); }

    private void SafeAction(Action work)
    {
        try { work(); }
        catch (Exception e) when (e is IOException or InvalidDataException or UnauthorizedAccessException or System.ComponentModel.Win32Exception or
            System.Runtime.InteropServices.ExternalException or InvalidOperationException or ArgumentException or NotSupportedException)
        {
            ShowText("Action could not finish", e.Message);
        }
    }

    internal int CheckControls()
    {
        var page = url.Parent!;
        var panel = page.Parent!;
        bool ok = page.Controls.GetChildIndex(url) < page.Controls.GetChildIndex(find) &&
            url.TabIndex < find.TabIndex && find.TabIndex < cancel.TabIndex &&
            page.TabIndex < results.TabIndex && results.TabIndex < details.TabIndex && details.TabIndex < status.TabIndex &&
            results.Parent == panel && !details.AcceptsTab && details.ReadOnly && status.ReadOnly &&
            results.AccessibilityObject.Name == "Streams" && url.AccessibilityObject.Name == "Website address" &&
            find.AccessibilityObject.KeyboardShortcut == "Ctrl+Enter" && cancel.AccessibilityObject.KeyboardShortcut == "Esc" &&
            IsSearchKey(Keys.Enter, true) && !IsSearchKey(Keys.Enter, false) &&
            IsSearchKey(Keys.Control | Keys.Enter, false) && StreamList.OpensMenu(Keys.Enter) &&
            StreamList.OpensMenu(Keys.Apps) && StreamList.OpensMenu(Keys.Shift | Keys.F10) &&
            StreamList.CopiesStream(Keys.Control | Keys.C) && !StreamList.CopiesStream(Keys.Control | Keys.Shift | Keys.C) &&
            copyCommand.ShortcutKeys == Keys.None && updateCommand.ShortcutKeys == (Keys.Shift | Keys.F1) &&
            preferencesCommand.ShortcutKeys == (Keys.Control | Keys.Oemcomma) &&
            StreamList.OpensDefaultPlayer(Keys.Shift | Keys.Enter) && !StreamList.OpensMenu(Keys.Shift | Keys.Enter);
        using var preferencesFixture = new PreferencesDialog(new Preferences(), () => { });
        ok &= preferencesFixture.CheckControls();
        foreach (var list in MainMenuStrip!.Items.OfType<ToolStripMenuItem>()
                     .Select(i => i.DropDownItems.OfType<ToolStripMenuItem>()).Append(MainMenuStrip.Items.OfType<ToolStripMenuItem>()))
        {
            var keys = list.Select(i => i.Text ?? "").Where(s => s.Contains('&')).Select(s => char.ToUpperInvariant(s[s.IndexOf('&') + 1])).ToArray();
            ok &= keys.Length == keys.Distinct().Count();
            ok &= list.Where(i => !string.IsNullOrEmpty(i.ShortcutKeyDisplayString)).All(i =>
                !(i.AccessibilityObject.Name ?? "").Contains(i.ShortcutKeyDisplayString!) && !(i.Text ?? "").Contains('&'));
        }
        var pageUrl = new Uri("https://example.com/");
        var a = new Station("Theme", new Uri(pageUrl, "fallback"), "MP3", 192, pageUrl, false);
        var b = a with { Url = new Uri(pageUrl, "themestream") };
        ok &= DisplayNames([a, b]) is ["Theme (example.com/fallback)", "Theme (example.com/themestream)"] &&
            DisplayNames([a]) is ["Theme"];
        ok &= !results.TabStop && !details.TabStop && status.TabStop;
        _ = results.Handle;
        var row = results.Items.Add(ResultRow(a, a.Name));
        ok &= results.Columns.Count == 1 && row.SubItems.Count == 1 && row.Text == "Theme, MP3, 192 kbps" &&
            ResultRow(a with { Bitrate = null }, a.Name).Text == "Theme, MP3";
        row.Selected = true;
        UpdateSelection();
        ok &= results.TabStop && details.TabStop;
        row.Selected = false;
        UpdateSelection();
        ok &= results.TabStop && !details.TabStop;
        ResetSearch();
        ok &= !results.TabStop && !details.TabStop && status.TabStop;
        return ok ? 0 : 1;
    }
}

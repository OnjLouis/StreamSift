using System.Diagnostics;
using System.Windows.Forms.Automation;

namespace StreamSift;

internal sealed class MainForm : Form
{
    private readonly HttpClient client = PublicNetwork.CreateClient();
    private readonly ComboBox url = new() { AccessibleName = "Website address or station name", Dock = DockStyle.Fill,
        DropDownStyle = ComboBoxStyle.DropDown, MaxLength = 4096, MaxDropDownItems = SearchHistory.Capacity };
    private readonly ShortcutButton find = new() { Text = "Find streams", ShortcutText = "Ctrl+Enter", AutoSize = true, TabIndex = 1 };
    private readonly ShortcutButton findBrowser = new() { Text = "Find with browser", ShortcutText = "Ctrl+Shift+Enter", AutoSize = true };
    private readonly ShortcutButton cancel = new() { Text = "Cancel", ShortcutText = "Esc", AutoSize = true, Enabled = false, TabIndex = 2 };
    private readonly StreamList results = new()
    {
        AccessibleName = "Streams", View = View.Details, FullRowSelect = true, HideSelection = false,
        Dock = DockStyle.Fill, TabIndex = 2
    };
    private readonly TextBox details = new()
    {
        AccessibleName = "Stream details", ReadOnly = true, Multiline = true, ScrollBars = ScrollBars.Vertical,
        Dock = DockStyle.Fill, TabIndex = 4
    };
    private readonly TextBox status = new() { AccessibleName = "Status", ReadOnly = true, Dock = DockStyle.Fill, TabIndex = 5 };
    private readonly Label streamsLabel = new() { Text = "&Streams:", AutoSize = true, Anchor = AnchorStyles.Left, TabIndex = 1 };
    private readonly Label detailsLabel = new() { Text = "Stream &Details:", AutoSize = true, Anchor = AnchorStyles.Left, TabIndex = 3 };
    private readonly Dictionary<Control, string> help = new();
    private readonly ToolStripMenuItem copyCommand, saveCommand, openFolderCommand, foobarCommand, openPlayerCommand, previewCommand, preferencesCommand, updateCommand;
    private readonly ToolStripMenuItem browserCommand, directoryCommand, streamsCommand, detailsCommand, refreshCommand;
    private readonly ToolStripMenuItem filterCommand, findResultCommand, nextResultCommand, previousResultCommand, openBrowserCommand;
    private readonly ToolStripMenuItem clearHistoryCommand;
    private readonly SearchSounds sounds = new();
    private readonly System.Windows.Forms.Timer handoffs = new() { Interval = 30000 };
    private Preferences preferences = Preferences.Load(Preferences.UserDirectory);
    private string[] history;
    private Station[] searchResults = [], visibleResults = [];
    private ResultView resultView;
    private string resultQuery = "";
    private (string Input, SearchMethod Method)? lastSearch;
    private CancellationTokenSource? scan;
    private CancellationTokenSource? updateCheck;
    private CancellationTokenSource? startupUpdate;
    private bool pendingUpdateNotice;
    private bool closing;
    private bool waitForScanOnClose;
    private bool closeAfterScan;

    internal MainForm(bool readHistory = true)
    {
        history = readHistory ? SearchHistory.Load(Preferences.UserDirectory) : [];
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
        openFolderCommand = MenuCommand.Create("Open playlist folder", OpenPlaylistFolder, Keys.Control | Keys.Shift | Keys.O);
        file.DropDownItems.Add(openFolderCommand);
        clearHistoryCommand = MenuCommand.Create("Clear search &history", ClearHistory);
        file.DropDownItems.Add(clearHistoryCommand);
        file.DropDownItems.Add(new ToolStripSeparator());
        file.DropDownItems.Add(MenuCommand.Create("E&xit", Close));
        var actions = new ToolStripMenuItem("&Actions");
        browserCommand = MenuCommand.Create("Find with browser", () => _ = FindAsync(SearchMethod.Browser),
            Keys.Control | Keys.Shift | Keys.Enter);
        directoryCommand = MenuCommand.Create("Look up in public directory", () => _ = FindAsync(SearchMethod.Directory),
            Keys.Control | Keys.Shift | Keys.D);
        refreshCommand = MenuCommand.Create("Refresh results", RefreshResults, Keys.F5);
        copyCommand = MenuCommand.Create("Copy stream address", Copy, display: "Ctrl+C");
        foobarCommand = MenuCommand.Create("Add to Foobar2000", AddToFoobar, Keys.Control | Keys.Shift | Keys.F);
        previewCommand = MenuCommand.Create(StreamList.PlayLabel, Preview, display: "Enter");
        openBrowserCommand = MenuCommand.Create(StreamList.BrowserLabel, OpenInBrowser, display: "Alt+Enter");
        openPlayerCommand = MenuCommand.Create("Save and open in default player", OpenInDefaultPlayer, display: "Shift+Enter");
        streamsCommand = MenuCommand.Create("Go to Streams", results.Select, Keys.Alt | Keys.S);
        detailsCommand = MenuCommand.Create("Go to Stream Details", details.Select, Keys.Alt | Keys.D);
        filterCommand = MenuCommand.Create("Filter and sort...", ShowResultFilter, Keys.Control | Keys.L);
        findResultCommand = MenuCommand.Create("Find in results...", () => ShowResultFind(), Keys.Control | Keys.F);
        nextResultCommand = MenuCommand.Create("Find next", () => FindResult(false), Keys.F3);
        previousResultCommand = MenuCommand.Create("Find previous", () => FindResult(true), Keys.Shift | Keys.F3);
        actions.DropDownItems.AddRange([browserCommand, directoryCommand, refreshCommand, new ToolStripSeparator(),
            previewCommand, openBrowserCommand, copyCommand, foobarCommand, openPlayerCommand, new ToolStripSeparator(),
            streamsCommand, detailsCommand, filterCommand, findResultCommand, nextResultCommand, previousResultCommand]);
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
            Dock = DockStyle.Fill, AutoSize = true, ColumnCount = 2, RowCount = 2, Margin = new(0), TabIndex = 0
        };
        entry.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        entry.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        entry.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        entry.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        var urlLabel = new Label { Text = "&Website address or station name:", Anchor = AnchorStyles.Left, AutoSize = true, TabIndex = 0 };
        url.TabIndex = 1;
        find.TabIndex = 0;
        findBrowser.TabIndex = 1;
        cancel.TabIndex = 2;
        var buttons = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill, Margin = new(0), TabIndex = 2 };
        buttons.Controls.AddRange([find, findBrowser, cancel]);
        entry.Controls.Add(urlLabel, 0, 0);
        entry.Controls.Add(url, 1, 0);
        entry.Controls.Add(buttons, 0, 1);
        entry.SetColumnSpan(buttons, 2);

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
                case StreamAction.Preview: Preview(); break;
                case StreamAction.SelectAll: SelectAllStreams(); break;
                case StreamAction.OpenBrowser: OpenInBrowser(); break;
            }
        };
        results.Resize += (_, _) => results.Columns[0].Width =
            Math.Max(145, results.ClientSize.Width - SystemInformation.VerticalScrollBarWidth - 4);

        var grid = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 6, Padding = new(12) };
        grid.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        grid.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        grid.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        grid.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        grid.RowStyles.Add(new RowStyle(SizeType.Absolute, 95));
        grid.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        grid.Controls.Add(entry, 0, 0);
        grid.Controls.Add(streamsLabel, 0, 1);
        grid.Controls.Add(results, 0, 2);
        grid.Controls.Add(detailsLabel, 0, 3);
        grid.Controls.Add(details, 0, 4);
        grid.Controls.Add(status, 0, 5);
        Controls.Add(grid);
        Controls.Add(menus);

        help[url] = "Enter a public website, stream address, station name or call sign. Alt+Down Arrow opens recent successful searches. Select an entry, then press Enter in the field to find streams.";
        help[find] = "Scan the address for live audio, or look up a station name in public radio directories.";
        help[findBrowser] = "Look for live audio using a temporary, muted browser. Requires Chrome or Microsoft Edge.";
        help[cancel] = "Stop the current search. You can also press Escape.";
        help[results] = "F5 repeats the current search. Ctrl+L filters and sorts, Ctrl+F finds text, and Ctrl+A selects all visible streams. Enter plays one stream; Alt+Enter opens its address in your browser.";
        help[details] = "Review the first selected stream's address and the page where it was found. Ctrl+C copies selected text. Alt+S returns to Streams.";
        help[status] = "Review the last result or operation. A search with no results does not prove that the website has no live stream.";
        find.Click += async (_, _) => await FindAsync();
        findBrowser.Click += async (_, _) => await FindAsync(SearchMethod.Browser);
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
            if (waitForScanOnClose && scan != null)
            {
                closeAfterScan = true;
                scan.Cancel();
                e.Cancel = true;
                return;
            }
            closing = true;
            scan?.Cancel();
            startupUpdate?.Cancel();
        };
        Disposed += (_, _) => { sounds.Dispose(); client.Dispose(); handoffs.Dispose(); };
        RefreshHistory();
        UpdateSelection();
        SetStatus("Ready.");
    }

    private Station? Selected => results.SelectedItems.Count == 0 ? null : results.SelectedItems[0].Tag as Station;

    private Station[] SelectedStreams() =>
        results.SelectedItems.Cast<ListViewItem>().OrderBy(row => row.Index).Select(row => (Station)row.Tag!).ToArray();

    private void SelectAllStreams()
    {
        results.SelectAllRows();
        SetStatus(results.SelectedItems.Count == 1 ? "1 stream selected." : $"{results.SelectedItems.Count} streams selected.");
    }

    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        if (IsSearchKey(keyData, url.ContainsFocus && !url.DroppedDown))
        {
            if (find.Enabled)
            {
                if (url.ContainsFocus) url.DroppedDown = false;
                find.PerformClick();
            }
            return true;
        }
        if (keyData == Keys.Escape && scan != null) { scan.Cancel(); return true; }
        if (keyData == Keys.F1) { ShowHelp(); return true; }
        return base.ProcessCmdKey(ref msg, keyData);
    }

    private static bool IsSearchKey(Keys key, bool inAddress) =>
        key == (Keys.Control | Keys.Enter) || (inAddress && key == Keys.Enter);

    private static string StartingSearch(SearchMethod method) => method switch
    {
        SearchMethod.Browser => "Searching with a temporary browser...",
        SearchMethod.Directory => "Checking public radio directories...",
        _ => "Searching the website..."
    };

    private void RefreshResults()
    {
        if (lastSearch is { } request) _ = FindAsync(request.Method, request.Input);
    }

    private async Task FindAsync(SearchMethod mode = SearchMethod.Passive, string? refreshInput = null)
    {
        if (scan != null || updateCheck != null) return;
        bool refreshing = refreshInput != null;
        var searchText = refreshInput ?? url.Text;
        var requestedMethod = mode;
        bool nameInput = SearchSequence.CanSearchStation(searchText);
        bool hasAddress = PublicNetwork.TryInput(searchText, out _) && !nameInput;
        bool stationSearch = nameInput && mode != SearchMethod.Browser;
        if (!hasAddress && !stationSearch)
        {
            SetStatus(mode == SearchMethod.Browser ? "Enter a public HTTP or HTTPS address." :
                "Enter a public HTTP or HTTPS address, station name or call sign.");
            status.Focus();
            return;
        }
        if (stationSearch) mode = SearchMethod.Directory;
        var fallback = !stationSearch && preferences.AutomaticFallback;
        if (mode == SearchMethod.Browser && !fallback && !BrowserDiscovery.IsAvailable)
        {
            SetStatus("Find with browser requires Chrome or Microsoft Edge.");
            status.Focus();
            return;
        }
        var operation = new CancellationTokenSource();
        scan = operation;
        waitForScanOnClose = fallback || mode != SearchMethod.Passive;
        bool focusCancel = url.ContainsFocus || find.Focused || findBrowser.Focused || results.Focused || details.Focused;
        cancel.Enabled = true;
        if (focusCancel) cancel.Focus();
        if (refreshing) url.Text = searchText;
        else ClearResults();
        lastSearch = (searchText, requestedMethod);
        UpdateSelection();
        SetStatus(refreshing ? "Refreshing results..." : StartingSearch(mode));
        sounds.SearchStarted();
        url.Enabled = false;
        clearHistoryCommand.Enabled = false;
        find.Enabled = findBrowser.Enabled = false;
        browserCommand.Enabled = false;
        directoryCommand.Enabled = false;
        IProgress<SearchMethod> progress = new Progress<SearchMethod>(method =>
        {
            if (scan == operation && !operation.IsCancellationRequested)
                SetStatus("Trying another method. " + StartingSearch(method));
        });
        var focusResults = refreshing && visibleResults.Length != 0;
        var historyNotSaved = false;
        try
        {
            var sequence = new SearchSequence(client, Path.Combine(Preferences.UserDirectory, "Cache"));
            var run = await Task.Run(() => stationSearch ? sequence.FindStationAsync(searchText, operation.Token) :
                sequence.FindAsync(searchText, mode, fallback, operation.Token,
                (method, number) => { if (number > 1) progress.Report(method); }));
            if (closing) return;
            operation.Token.ThrowIfCancellationRequested();
            var found = run.Result;
            var recent = SearchHistory.Remember(history, searchText, found.Stations.Count);
            if (!ReferenceEquals(recent, history))
            {
                history = recent;
                try { SearchHistory.Save(Preferences.UserDirectory, history); }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException) { historyNotSaved = true; }
                RefreshHistory();
            }
            searchResults = found.Stations.ToArray();
            var previousFormat = resultView.Format;
            if (refreshing) resultView = resultView.RetainAvailable(searchResults);
            RenderResults(preserveSelection: refreshing);
            focusResults = visibleResults.Length != 0;
            var sourceLabel = found.Stations.Count == 0 && run.Attempts > 1 ? "Automatic search: " : run.Method switch
            {
                SearchMethod.Browser => "Browser-assisted search: ",
                SearchMethod.Directory => "Public directory: ",
                _ => ""
            };
            var refreshed = refreshing ? "Refresh finished. " : "";
            SetStatus(refreshed + sourceLabel + (found.Stations.Count switch
                { 0 => "No live streams verified.", 1 => "1 stream found.", _ => $"{found.Stations.Count} streams found." }) +
                $" Checked {found.Requests} {(found.Requests == 1 ? "address" : "addresses")}." +
                (found.Unreachable > 0 ? $" {found.Unreachable} unavailable." : "") +
                (found.LimitReached ? " Search limit reached." : "") +
                (run.Attempts > 1 ? $" Tried {run.Attempts} methods." : "") +
                (run.FailedMethods > 0 ? $" Methods that could not finish: {run.FailedMethods}." : "") +
                (refreshing && resultView.Format != null ? $" Showing {visibleResults.Length} {resultView.Format} streams." : "") +
                (refreshing && previousFormat != null && resultView.Format == null && found.Stations.Count != 0
                    ? $" {previousFormat} is no longer available. Showing all formats." : "") +
                (historyNotSaved ? " Recent searches could not be saved." : ""));
            sounds.SearchCompleted();
        }
        catch (OperationCanceledException) when (operation.IsCancellationRequested)
        {
            if (!closing) SetStatus(refreshing && focusResults ? "Refresh cancelled. Previous results kept." : "Search cancelled.");
        }
        catch (Exception e) when (e is OperationCanceledException or HttpRequestException or IOException or InvalidOperationException or
            System.ComponentModel.Win32Exception or System.Text.Json.JsonException or UnauthorizedAccessException)
        {
            if (!closing) SetStatus(refreshing && focusResults ? "Refresh could not finish. Previous results kept." : mode switch
            {
                SearchMethod.Browser => "Browser-assisted search could not finish.",
                SearchMethod.Directory => "Public directory lookup could not finish.",
                _ => "Search could not finish. Try the station's player page."
            });
        }
        finally
        {
            scan = null;
            waitForScanOnClose = false;
            operation.Dispose();
            if (!closing)
            {
                // Remember the next focus inside this window even when the app is in the background.
                var focus = IsSearchControl(ActiveControl) || url.ContainsFocus || cancel.Focused || find.Focused || findBrowser.Focused;
                url.Enabled = true;
                clearHistoryCommand.Enabled = history.Length != 0;
                find.Enabled = findBrowser.Enabled = true;
                browserCommand.Enabled = true;
                directoryCommand.Enabled = true;
                UpdateSelection();
                if (focus)
                {
                    var destination = focusResults ? (Control)results : status;
                    ActiveControl = destination;
                    if (ContainsFocus) destination.Focus();
                }
                cancel.Enabled = false;
                if (pendingUpdateNotice) SetStatus(status.Text);
                if (closeAfterScan) BeginInvoke(new Action(Close));
            }
        }
    }

    private bool IsSearchControl(Control? control) => control == url || control == cancel || control == find || control == findBrowser;

    private void RefreshHistory()
    {
        string input = url.Text;
        url.BeginUpdate();
        try
        {
            url.Items.Clear();
            url.Items.AddRange(history);
            url.Text = input;
        }
        finally { url.EndUpdate(); }
        clearHistoryCommand.Enabled = scan == null && history.Length != 0;
    }

    private void ClearHistory() => SafeAction(() =>
    {
        SearchHistory.Save(Preferences.UserDirectory, []);
        history = [];
        RefreshHistory();
        SetStatus("Search history cleared.");
    });

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
        find.Enabled = findBrowser.Enabled = false;
        browserCommand.Enabled = false;
        directoryCommand.Enabled = false;
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
                find.Enabled = findBrowser.Enabled = true;
                browserCommand.Enabled = true;
                directoryCommand.Enabled = true;
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
        new(name + ", " + station.Format + (station.Bitrate is int bitrate ? $", {bitrate} kbps" : "") +
            (station.Listeners is int count ? ", " + ResultView.ListenerLabel(count) : ""))
        { Tag = station };

    private void RenderResults(bool preserveSelection)
    {
        var next = resultView.Apply(searchResults);
        if (!visibleResults.SequenceEqual(next))
        {
            var selected = preserveSelection ? SelectedStreams().Select(s => s.Url).ToHashSet() : [];
            var focused = (results.FocusedItem?.Tag as Station)?.Url;
            visibleResults = next;
            var names = DisplayNames(next);
            results.BeginUpdate();
            try
            {
                results.Items.Clear();
                for (int i = 0; i < next.Length; i++)
                {
                    var row = results.Items.Add(ResultRow(next[i], names[i]));
                    row.Selected = selected.Contains(next[i].Url);
                    if (row.Selected && next[i].Url == focused) row.Focused = true;
                }
                if (next.Length != 0 && results.SelectedItems.Count == 0) results.Items[0].Selected = true;
                if (results.SelectedItems.Count != 0)
                {
                    if (results.FocusedItem is not { Selected: true }) results.SelectedItems[0].Focused = true;
                    results.FocusedItem?.EnsureVisible();
                }
            }
            finally { results.EndUpdate(); }
        }
        UpdateSelection();
    }

    private void ShowResultFilter()
    {
        if (searchResults.Length == 0) return;
        using var dialog = ResultDialog.Filter(ResultView.Formats(searchResults), searchResults.Any(s => s.Listeners.HasValue),
            resultView, OpenManual, out var read);
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        resultView = read();
        RenderResults(preserveSelection: true);
        SetStatus($"Showing {visibleResults.Length} of {searchResults.Length} streams.");
        results.Select();
    }

    private void ShowResultFind(bool backwards = false)
    {
        if (visibleResults.Length == 0) return;
        using var dialog = ResultDialog.Find(resultQuery, OpenManual, out var read);
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        resultQuery = read();
        FindResult(backwards, fromStart: true);
    }

    private void FindResult(bool backwards, bool fromStart = false)
    {
        if (visibleResults.Length == 0) return;
        if (resultQuery.Length == 0) { ShowResultFind(backwards); return; }
        int anchor = fromStart ? -1 : results.FocusedItem?.Index ?? -1;
        int index = ResultView.Find(visibleResults, anchor, resultQuery, backwards);
        if (index < 0) { SetStatus("No match in the visible streams."); return; }
        var row = results.Items[index];
        if (results.SelectedItems.Count != 1 || !row.Selected) results.SelectedIndices.Clear();
        row.Selected = true;
        row.Focused = true;
        row.EnsureVisible();
        results.Select();
        if (index == anchor) SetStatus("Only one match in the visible streams.");
    }

    private void UpdateSelection()
    {
        var count = results.SelectedItems.Count;
        var selected = Selected;
        streamsLabel.Enabled = streamsCommand.Enabled = results.TabStop = results.Items.Count != 0;
        detailsLabel.Enabled = detailsCommand.Enabled = details.TabStop = selected != null;
        refreshCommand.Enabled = scan == null && updateCheck == null && lastSearch.HasValue;
        filterCommand.Enabled = scan == null && searchResults.Length != 0;
        findResultCommand.Enabled = nextResultCommand.Enabled = previousResultCommand.Enabled = scan == null && visibleResults.Length != 0;
        copyCommand.Enabled = foobarCommand.Enabled = saveCommand.Enabled = openPlayerCommand.Enabled = selected != null;
        previewCommand.Enabled = openBrowserCommand.Enabled = count == 1;
        copyCommand.Text = count > 1 ? "Copy stream addresses" : "Copy stream address";
        string info = selected == null ? "" : string.Join("\r\n",
            selected.Name, "Format: " + selected.Format,
            selected.Bitrate is int bitrate ? $"Bitrate: {bitrate} kbps" : "Bitrate: not reported",
            "Stream address:", selected.Url.AbsoluteUri, "Found on:", selected.FoundOn.AbsoluteUri);
        if (selected?.Listeners is int listeners) info += $"\r\nListeners at scan: {listeners}";
        if (count > 1) info = $"{count} streams selected. Details for the first stream:\r\n\r\n" + info;
        if (details.Text != info)
        {
            details.Text = info;
            details.Select(0, 0);
        }
    }

    private void ClearResults()
    {
        searchResults = visibleResults = [];
        resultView = default;
        resultQuery = "";
        results.Items.Clear();
        UpdateSelection();
    }

    private void ResetSearch()
    {
        if (scan != null) return;
        lastSearch = null;
        url.SelectedIndex = -1;
        url.Text = "";
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
        var stations = SelectedStreams();
        if (stations.Length == 0) return;
        SafeAction(() =>
        {
            Clipboard.SetText(string.Join("\r\n", stations.Select(station => station.Url.AbsoluteUri)));
            SetStatus(stations.Length == 1 ? "Stream address copied." : $"{stations.Length} stream addresses copied.");
        });
    }

    private void Export()
    {
        var stations = SelectedStreams();
        if (stations.Length == 0) return;
        SafeAction(() =>
        {
            Directory.CreateDirectory(preferences.ExportFolder);
            using var dialog = new SaveFileDialog
            {
                Title = "Save stream playlist", Filter = "M3U playlist (*.m3u8)|*.m3u8",
                AddExtension = true, DefaultExt = "m3u8", InitialDirectory = preferences.ExportFolder,
                FileName = PlaylistFile.SuggestedName(stations)
            };
            if (dialog.ShowDialog(this) != DialogResult.OK) return;
            PlaylistFile.Write(dialog.FileName, stations, replace: true);
            ReportSavedPlaylist(dialog.FileName, stations.Length);
        });
    }

    private void OpenPlaylistFolder() => SafeAction(() =>
    {
        var folder = preferences.ExportFolder;
        Directory.CreateDirectory(folder);
        Open(folder);
    });

    private void Preview()
    {
        if (results.SelectedItems.Count > 1)
        {
            SetStatus("Select one stream to play it, or press Shift+Enter to save and open the selection.");
            return;
        }
        if (Selected is not Station item) return;
        var choice = preferences.MediaPlayer;
        var player = StreamPreview.FindPlayer(choice);
        if (player == null)
        {
            ShowText(StreamList.PlayLabel, choice == PlayerChoice.Automatic
                ? "VLC, Foobar2000 and FastPlay were not found. Install one of these players, or press Shift+Enter to save and open a playlist in your default player."
                : StreamPreview.DisplayName(choice) + " was not found. Choose another media player in Preferences, or press Shift+Enter to open a saved playlist in your default player.");
            return;
        }
        SafeAction(() =>
        {
            var playlist = player.Kind == PlayerChoice.FastPlay ? FoobarHandoff.Stage(FoobarHandoff.Folder, [item]) : null;
            try
            {
                using var process = Process.Start(StreamPreview.Command(player, item, playlist));
                if (process == null) throw new IOException(player.Name + " could not start.");
            }
            catch
            {
                if (playlist != null) try { File.Delete(playlist); } catch (IOException) { } catch (UnauthorizedAccessException) { }
                throw;
            }
            if (playlist != null) handoffs.Start();
            SetStatus("Sent to " + player.Name + ".");
        });
    }

    private void OpenInDefaultPlayer()
    {
        var stations = SelectedStreams();
        if (stations.Length == 0) return;
        SafeAction(() =>
        {
            var path = PlaylistFile.SaveNew(preferences.ExportFolder, stations);
            ReportSavedPlaylist(path, stations.Length);
            try { Open(path); }
            catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException)
            {
                ShowText("Could not open the playlist", "The playlist was saved at:\r\n" + path +
                    "\r\n\r\nWindows could not open it. Check the app associated with .m3u8 files.");
            }
        });
    }

    private void OpenInBrowser()
    {
        if (results.SelectedItems.Count != 1 || Selected is not Station item) return;
        SafeAction(() =>
        {
            if (!PublicNetwork.TryUrl(item.Url.AbsoluteUri, null, out var address))
                throw new InvalidDataException("The stream does not have a public HTTP or HTTPS address.");
            Open(address.AbsoluteUri);
            SetStatus("Opened in browser.");
        });
    }

    private void ReportSavedPlaylist(string path, int count) =>
        SetStatus("Playlist saved: " + Path.GetFileName(path) + (count > 1 ? $". {count} streams." : ""));

    private void AddToFoobar()
    {
        var stations = SelectedStreams();
        if (stations.Length == 0) return;
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
            var path = FoobarHandoff.Stage(FoobarHandoff.Folder, stations);
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
            SetStatus((stations.Length == 1 ? stations[0].Name : $"{stations.Length} streams") + " sent to Foobar2000.");
        });
    }

    private void CleanHandoffs()
    {
        try { handoffs.Enabled = FoobarHandoff.Prune(FoobarHandoff.Folder, DateTime.UtcNow) > 0; }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { handoffs.Stop(); }
    }

    private void ShowHelp()
    {
        var focused = help.Keys.FirstOrDefault(c => c.ContainsFocus);
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
        if (!string.Equals(results.AccessibilityObject.KeyboardShortcut, "Alt+S", StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(details.AccessibilityObject.KeyboardShortcut, "Alt+D", StringComparison.OrdinalIgnoreCase))
        {
            Console.Error.WriteLine($"Mnemonic bindings: website '{url.AccessibilityObject.KeyboardShortcut}', streams '{results.AccessibilityObject.KeyboardShortcut}', details '{details.AccessibilityObject.KeyboardShortcut}'.");
            return 2;
        }
        var page = url.Parent!;
        var panel = page.Parent!;
        var buttons = find.Parent!;
        bool ok = buttons.Parent == page && findBrowser.Parent == buttons && cancel.Parent == buttons &&
            page.Controls.GetChildIndex(url) < page.Controls.GetChildIndex(buttons) &&
            url.TabIndex < buttons.TabIndex && find.TabIndex < findBrowser.TabIndex && findBrowser.TabIndex < cancel.TabIndex &&
            page.TabIndex < results.TabIndex && results.TabIndex < details.TabIndex && details.TabIndex < status.TabIndex &&
            results.Parent == panel && !details.AcceptsTab && details.ReadOnly && status.ReadOnly &&
            results.AccessibilityObject.Name == "Streams" && url.AccessibilityObject.Name == "Website address or station name" &&
            url.DropDownStyle == ComboBoxStyle.DropDown && url.MaxDropDownItems == SearchHistory.Capacity &&
            !clearHistoryCommand.Enabled && url.Items.Count == 0 &&
            find.AccessibilityObject.KeyboardShortcut == "Ctrl+Enter" && cancel.AccessibilityObject.KeyboardShortcut == "Esc" &&
            findBrowser.AccessibilityObject.Name == "Find with browser" && findBrowser.AccessibilityObject.KeyboardShortcut == "Ctrl+Shift+Enter" &&
            IsSearchKey(Keys.Enter, true) && !IsSearchKey(Keys.Enter, false) &&
            IsSearchKey(Keys.Control | Keys.Enter, false) && StreamList.PreviewsStream(Keys.Enter) && !StreamList.OpensMenu(Keys.Enter) &&
            StreamList.OpensMenu(Keys.Apps) && StreamList.OpensMenu(Keys.Shift | Keys.F10) &&
            StreamList.CopiesStream(Keys.Control | Keys.C) && !StreamList.CopiesStream(Keys.Control | Keys.Shift | Keys.C) &&
            StreamList.SelectsAll(Keys.Control | Keys.A) && !StreamList.SelectsAll(Keys.Alt | Keys.A) &&
            copyCommand.ShortcutKeys == Keys.None && updateCommand.ShortcutKeys == (Keys.Shift | Keys.F1) &&
            preferencesCommand.ShortcutKeys == (Keys.Control | Keys.Oemcomma) &&
            browserCommand.ShortcutKeys == (Keys.Control | Keys.Shift | Keys.Enter) &&
            directoryCommand.ShortcutKeys == (Keys.Control | Keys.Shift | Keys.D) &&
            streamsCommand.ShortcutKeys == (Keys.Alt | Keys.S) && streamsCommand.ShortcutKeyDisplayString == "Alt+S" &&
            detailsCommand.ShortcutKeys == (Keys.Alt | Keys.D) && detailsCommand.ShortcutKeyDisplayString == "Alt+D" &&
            filterCommand.ShortcutKeys == (Keys.Control | Keys.L) && findResultCommand.ShortcutKeys == (Keys.Control | Keys.F) &&
            nextResultCommand.ShortcutKeys == Keys.F3 && previousResultCommand.ShortcutKeys == (Keys.Shift | Keys.F3) &&
            refreshCommand.ShortcutKeys == Keys.F5 && !refreshCommand.Enabled &&
            StreamList.OpensBrowser(Keys.Alt | Keys.Enter) && openBrowserCommand.ShortcutKeyDisplayString == "Alt+Enter" &&
            openFolderCommand.Enabled && openFolderCommand.ShortcutKeys == (Keys.Control | Keys.Shift | Keys.O) &&
            foobarCommand.ShortcutKeys == (Keys.Control | Keys.Shift | Keys.F) &&
            StreamList.OpensDefaultPlayer(Keys.Shift | Keys.Enter) && !StreamList.OpensMenu(Keys.Shift | Keys.Enter) &&
            !StreamList.PreviewsStream(Keys.Shift | Keys.Enter) && previewCommand.Text == "Play stream" &&
            previewCommand.ShortcutKeyDisplayString == "Enter";
        using var preferencesFixture = new PreferencesDialog(new Preferences(MediaPlayer: PlayerChoice.Foobar2000), () => { });
        ok &= preferencesFixture.CheckControls();
        using var filterFixture = ResultDialog.Filter(["AAC", "MP3"], true, new ResultView("MP3"), () => { }, out var readFilter);
        var filterFields = filterFixture.Controls[0].Controls.OfType<ComboBox>().ToArray();
        ok &= filterFields.Length == 2 && filterFields[0].AccessibilityObject.Name == "Format" &&
            filterFields[0].Items.Cast<string>().SequenceEqual(["All formats", "AAC", "MP3"]) &&
            filterFields[1].AccessibilityObject.Name == "Sort by" && filterFields[1].Items.Contains("Listeners, most first") &&
            readFilter() == new ResultView("MP3") && filterFixture.AcceptButton is Button { Text: "&Apply" } &&
            filterFixture.CancelButton is Button { Text: "&Cancel" };
        using var uncounted = ResultDialog.Filter(["MP3"], false, default, () => { }, out _);
        ok &= !uncounted.Controls[0].Controls.OfType<ComboBox>().Last().Items.Contains("Listeners, most first");
        using var findFixture = ResultDialog.Find("", () => { }, out var readFind);
        var findField = findFixture.Controls[0].Controls.OfType<TextBox>().Single();
        ok &= findField.AccessibilityObject.Name == "Find what" && !((Button)findFixture.AcceptButton!).Enabled;
        findField.Text = "  Chill ";
        ok &= readFind() == "Chill" && ((Button)findFixture.AcceptButton!).Enabled;
        var shortcuts = MainMenuStrip!.Items.OfType<ToolStripMenuItem>()
            .SelectMany(item => item.DropDownItems.OfType<ToolStripMenuItem>())
            .Select(item => item.ShortcutKeys).Where(keys => keys != Keys.None).ToArray();
        ok &= shortcuts.Length == shortcuts.Distinct().Count();
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
        ok &= !results.TabStop && !details.TabStop && status.TabStop && !streamsCommand.Enabled && !detailsCommand.Enabled;
        _ = results.Handle;
        var row = results.Items.Add(ResultRow(a, a.Name));
        ok &= results.Columns.Count == 1 && row.SubItems.Count == 1 && row.Text == "Theme, MP3, 192 kbps" &&
            ResultRow(a with { Bitrate = null }, a.Name).Text == "Theme, MP3" &&
            ResultRow(a with { Listeners = 0 }, a.Name).Text == "Theme, MP3, 192 kbps, 0 listeners" &&
            ResultRow(a with { Listeners = 1 }, a.Name).Text == "Theme, MP3, 192 kbps, 1 listener";
        row.Selected = true;
        UpdateSelection();
        ok &= results.TabStop && details.TabStop && streamsCommand.Enabled && detailsCommand.Enabled;
        detailsCommand.PerformClick();
        ok &= ActiveControl == details && row.Selected;
        streamsCommand.PerformClick();
        ok &= ActiveControl == results && row.Selected;
        row.Selected = false;
        UpdateSelection();
        ok &= results.TabStop && !details.TabStop && streamsCommand.Enabled && !detailsCommand.Enabled;
        var secondRow = results.Items.Add(ResultRow(b, b.Name));
        secondRow.Selected = true;
        SelectAllStreams();
        ok &= results.SelectedItems.Count == 2 && SelectedStreams().SequenceEqual([a, b]) &&
            saveCommand.Enabled && !previewCommand.Enabled && copyCommand.Text == "Copy stream addresses" &&
            details.Text.StartsWith("2 streams selected.", StringComparison.Ordinal);
        history = ["second.example", "first.example"];
        url.Text = "current.example";
        RefreshHistory();
        ok &= clearHistoryCommand.Enabled && url.Text == "current.example" &&
            url.Items.Cast<string>().SequenceEqual(["second.example", "first.example"]);
        ResetSearch();
        ok &= url.Text == "" && url.Items.Count == 2;
        searchResults = [a, b with { Format = "AAC" }];
        RenderResults(preserveSelection: false);
        ok &= filterCommand.Enabled && findResultCommand.Enabled && results.Items.Count == 2;
        resultView = new ResultView("AAC");
        RenderResults(preserveSelection: true);
        ok &= results.Items.Count == 1 && Selected?.Format == "AAC" && openBrowserCommand.Enabled;
        lastSearch = ("https://example.org/", SearchMethod.Passive);
        searchResults = [a, b with { Format = "AAC", Listeners = 7 }];
        resultView = resultView.RetainAvailable(searchResults);
        RenderResults(preserveSelection: true);
        ok &= refreshCommand.Enabled && resultView.Format == "AAC" && Selected?.Listeners == 7;
        resultQuery = "Theme";
        ResetSearch();
        ok &= resultView == default && resultQuery.Length == 0 && !filterCommand.Enabled && !findResultCommand.Enabled && !refreshCommand.Enabled;
        ok &= !results.TabStop && !details.TabStop && status.TabStop && !streamsCommand.Enabled && !detailsCommand.Enabled;
        return ok ? 0 : 1;
    }
}

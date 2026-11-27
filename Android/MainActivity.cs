using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.Media;
using Android.OS;
using Android.Views;
using Android.Views.Accessibility;
using Android.Views.InputMethods;
using Android.Widget;
using OperationCanceledException = System.OperationCanceledException;
using Orientation = Android.Widget.Orientation;
using static Android.Views.ViewGroup;

namespace StreamSift.Mobile;

[Activity(Name = "me.onj.streamsift.MainActivity", Label = "StreamSift", MainLauncher = true,
    Exported = true, LaunchMode = LaunchMode.SingleTop, ConfigurationChanges = ConfigChanges.Orientation | ConfigChanges.ScreenSize)]
public sealed partial class MainActivity : Activity
{
    private const int SaveDocument = 601;
    private const int AccessibilityFocus = 64;
    private const int SearchHeartbeatPulseMilliseconds = 22;
    private readonly Handler main = new(Looper.MainLooper!);
    private AndroidPlayback playback = null!;
    private MobilePreferences preferences = null!;
    private EditText address = null!;
    private TextView status = null!;
    private Button cancel = null!, filter = null!;
    private ImageButton recent = null!, settings = null!, clearSearch = null!;
    private LinearLayout resultActions = null!;
    private Button previous = null!, playPause = null!, next = null!, nowPlaying = null!;
    private ListView list = null!;
    private ListView? favouritesList;
    private (Uri Url, bool IsTrack)? playingResult;
    private ResultAdapter adapter = null!;
    private Station[] found = [], rows = [];
    private ResultView view;
    private Guid resultsId;
    private Uri? focused;
    private bool fileIndex;
    private bool firstResume = true;
    private string summary = "";
    private CancellationTokenSource? search;
    private MediaPlayer? cue;

    protected override void OnCreate(Bundle? savedInstanceState)
    {
        base.OnCreate(savedInstanceState);
        playback = PlaybackService.For(this);
        preferences = playback.Preferences;
        BuildHome();
        RestoreResults();
        playback.Changed += UpdatePlayback;
        playback.RevealRelativeItem += ScrollToPlayingRow;
        playback.Failed += Announce;
        UpdatePlayback();
    }

    protected override void OnResume()
    {
        base.OnResume();
        UpdatePlaybackTools();
        if (!firstResume) return;
        firstResume = false;
        list.Post(() =>
        {
            if (focused != null) FocusResult(focused);
            playback.ResumeOnLaunch();
        });
    }

    protected override void OnPause()
    {
        updateOperation?.Cancel();
        playback.Checkpoint();
        base.OnPause();
    }

    protected override void OnDestroy()
    {
        search?.Cancel();
        transfer?.Cancel();
        updateOperation?.Cancel();
        if (pendingFile is { } file) RemoveDownload(Path.GetDirectoryName(file)!);
        if (refreshPanel != null) main.RemoveCallbacks(refreshPanel);
        refreshPanel = null;
        playback.Changed -= UpdatePlayback;
        playback.RevealRelativeItem -= ScrollToPlayingRow;
        playback.Failed -= Announce;
        cue?.Release();
        cue?.Dispose();
        base.OnDestroy();
    }

    private void BuildHome()
    {
        var root = Column();
        root.SetPadding(Dp(12), 0, Dp(12), Dp(4));
        var title = Text("StreamSift", 22);
        Heading(title);
        title.SetSingleLine(true);
        title.Ellipsize = Android.Text.TextUtils.TruncateAt.End;
        var toolbar = new LinearLayout(this) { Orientation = Orientation.Horizontal };
        toolbar.SetGravity(GravityFlags.CenterVertical);
        toolbar.AddView(title, new LinearLayout.LayoutParams(0, LayoutParams.WrapContent, 1));
        recent = ToolbarButton(Resource.Drawable.toolbar_history, "Recent Searches", ShowHistory);
        settings = ToolbarButton(Resource.Drawable.toolbar_settings, "Settings", ShowSettings);
        favouritesButton = ToolbarButton(Resource.Drawable.toolbar_favourites, "Favourites", ShowFavourites);
        foreach (var control in new[] { recent, favouritesButton, BuildTimersButton(), settings })
            toolbar.AddView(control, new LinearLayout.LayoutParams(Dp(48), Dp(48)));
        root.AddView(toolbar);

        address = new EditText(this)
        {
            Hint = "Web address or station name",
            InputType = Android.Text.InputTypes.ClassText | Android.Text.InputTypes.TextVariationUri,
            ImeOptions = ImeAction.Search,
            Text = preferences.History().FirstOrDefault()
        };
        address.SetSingleLine(true);
        address.EditorAction += (_, args) =>
        {
            if (args.ActionId != ImeAction.Search && args.Event?.KeyCode != Keycode.Enter) return;
            args.Handled = true;
            _ = Find();
        };
        cancel = Button("Cancel", () => search?.Cancel());
        cancel.ContentDescription = "Cancel Search";
        clearSearch = ToolbarButton(Resource.Drawable.search_clear, "Clear search", ClearSearchText);
        address.TextChanged += (_, _) => UpdateSearchButtons();
        UpdateSearchButtons();
        filter = Button("Filter and Sort", ShowOptions);
        random = Button("Play Random", () => playback.PlayRandom(rows, row =>
        {
            if (search == null)
                FocusPlayingRow(list, rows, row, remember: true);
        }));
        resultActions = Bar(filter, random);
        resultActions.Visibility = ViewStates.Gone;
        root.AddView(resultActions);
        status = Text("Ready to search");
        status.AccessibilityLiveRegion = AccessibilityLiveRegion.Polite;
        root.AddView(status);
        list = new ListView(this) { ContentDescription = "Results", ItemsCanFocus = true };
        list.ScrollStateChanged += (_, args) =>
        {
            if (!TouchExploration && args.ScrollState == ScrollState.Idle)
                RememberRow(list.FirstVisiblePosition);
        };
        adapter = new ResultAdapter(this, () => rows, index =>
        {
            RememberRow(index);
            playback.Select(rows, index);
        }, RememberRow);
        list.Adapter = adapter;
        root.AddView(list, new LinearLayout.LayoutParams(LayoutParams.MatchParent, 0, 1));
        var searchBar = new LinearLayout(this) { Orientation = Orientation.Horizontal };
        searchBar.SetGravity(GravityFlags.CenterVertical);
        searchBar.AddView(address, new LinearLayout.LayoutParams(0, Dp(52), 1));
        searchBar.AddView(clearSearch, new LinearLayout.LayoutParams(Dp(48), Dp(48)));
        searchBar.AddView(cancel);
        root.AddView(searchBar);
        previous = Button("Previous", playback.Previous);
        playPause = Button("Play", playback.Toggle);
        playPause.BackgroundTintList = GetColorStateList(Resource.Color.transport_fill);
        playPause.SetTextColor(GetColorStateList(Resource.Color.transport_text));
        next = Button("Next", playback.Next);
        var playerBar = Column();
        playerBar.SetBackgroundColor(new Android.Graphics.Color(GetColor(Resource.Color.player_surface)));
        playerBar.AddView(Bar(previous, playPause, next));
        nowPlaying = Button("Now Playing", ShowNowPlaying);
        nowPlaying.SetSingleLine(true);
        nowPlaying.Ellipsize = Android.Text.TextUtils.TruncateAt.End;
        playerBar.AddView(nowPlaying);
        root.AddView(playerBar);
        root.FocusableInTouchMode = true;
        var surface = new FrameLayout(this);
        surface.SetFitsSystemWindows(true);
        surface.AddView(root, new FrameLayout.LayoutParams(LayoutParams.MatchParent, LayoutParams.MatchParent));
        SetContentView(surface);
        root.RequestFocus();
        Window?.SetSoftInputMode(SoftInput.AdjustResize);
    }

    private void UpdateSearchButtons()
    {
        bool searching = search != null;
        clearSearch.Visibility = !searching && address.Text?.Length > 0 ? ViewStates.Visible : ViewStates.Gone;
        cancel.Visibility = searching ? ViewStates.Visible : ViewStates.Gone;
    }

    private void ClearSearchText()
    {
        if (search != null) return;
        address.RequestFocus();
        address.Text = "";
        address.Post(() =>
        {
            if (!address.HasFocus || !address.HasWindowFocus) return;
            if (TouchExploration)
                address.PerformAccessibilityAction((Android.Views.Accessibility.Action)AccessibilityFocus, null);
            (GetSystemService(InputMethodService) as InputMethodManager)?.ShowSoftInput(address, ShowFlags.Implicit);
        });
    }

    private void RestoreResults()
    {
        if (playback.Store.ReadResults() is not { } saved || !MobilePreferences.CanRemember(saved.Input)) return;
        address.Text = saved.Input;
        resultsId = saved.Id;
        found = saved.Stations;
        fileIndex = saved.FileIndex;
        var place = playback.Place;
        view = place?.ResultsId == saved.Id ? place.View.RetainAvailable(found, fileIndex) : ResultView.ForSearch(fileIndex);
        focused = place?.ResultsId == saved.Id ? place.Focus : null;
        summary = ResultSummary();
        ApplyResults();
        playback.RestoreQueue(rows);
    }

    private async Task SearchFor(string input)
    {
        if (search != null) return;
        address.Text = input;
        await Find();
    }

    private async Task Find()
    {
        if (search != null) return;
        string input = address.Text?.Trim() ?? "";
        bool byName = SearchSequence.CanSearchStation(input);
        bool byUrl = PublicNetwork.TryInput(input, out var url) && url.Scheme == Uri.UriSchemeHttps;
        if (!byName && !byUrl)
        {
            status.Text = "Enter a public HTTPS address, station name or call sign.";
            return;
        }

        (GetSystemService(InputMethodService) as InputMethodManager)?.HideSoftInputFromWindow(address.WindowToken, HideSoftInputFlags.None);
        address.ClearFocus();
        using var operation = new CancellationTokenSource();
        search = operation;
        recent.Enabled = filter.Enabled = random.Enabled = false;
        UpdateSearchButtons();
        status.Text = "Searching…";
        Feedback(Resource.Raw.search);
        using var heartbeat = CancellationTokenSource.CreateLinkedTokenSource(operation.Token);
        var heartbeatToken = heartbeat.Token;
        var pulses = SearchHeartbeat.RunAsync(() => main.Post(() =>
        {
            if (!heartbeatToken.IsCancellationRequested && HasWindowFocus && preferences.Haptics)
                Pulse(SearchHeartbeatPulseMilliseconds);
        }), heartbeatToken);
        try
        {
            using var client = PublicNetwork.CreateClient(readStreamTitles: true);
            var result = byName
                ? (await new SearchSequence(client, Path.Combine(CacheDir!.AbsolutePath, "stations"))
                    .FindStationAsync(input, operation.Token, preferHttpsPlayback: true)).Result
                : await new Discovery(client).FindAsync(url, operation.Token);
            operation.Token.ThrowIfCancellationRequested();
            found = result.Stations.Where(row => !row.IsVideo && row.Url.Scheme == Uri.UriSchemeHttps).ToArray();
            fileIndex = result.EntryWasFileIndex;
            view = ResultView.ForSearch(fileIndex, result.PreferListedOrder);
            focused = null;
            summary = found.Length == 0
                ? result.Stations.Any(row => !row.IsVideo) ? "The audio results only use HTTP. StreamSift needs HTTPS on Android."
                    : result.ListingNotice ?? "No playable HTTPS streams or files found."
                : ResultSummary();
            summary += $" Checked {result.Requests} {(result.Requests == 1 ? "address" : "addresses")}.";
            if (result.LimitReached) summary += " Search limit reached.";
            if (found.Length > 0) preferences.Remember(input);
            var saved = MobilePreferences.CanRemember(input) ? SavedResults.Capture(input, found, fileIndex) : null;
            resultsId = saved != null && playback.Store.WriteResults(saved) ? saved.Id : Guid.Empty;
            if (saved == null) playback.Store.ClearResults();
            ApplyResults();
            focused = rows.FirstOrDefault(SessionStore.CanSave)?.Url;
            SaveBrowse();
            FocusResult(rows.FirstOrDefault()?.Url);
            Feedback(rows.Length > 0 ? Resource.Raw.done : Resource.Raw.error);
        }
        catch (OperationCanceledException)
        {
            status.Text = operation.IsCancellationRequested ? "Search cancelled." : "The search timed out.";
        }
        catch (Exception error) when (error is IOException or HttpRequestException or ArgumentException)
        {
            status.Text = "The search could not finish. Check the address and try again.";
        }
        finally
        {
            heartbeat.Cancel();
            await pulses;
            search = null;
            recent.Enabled = filter.Enabled = true;
            random.Enabled = rows.Length > 0;
            UpdateSearchButtons();
        }
    }

    private string ResultSummary()
    {
        int count = found.Length;
        if (fileIndex || found.All(row => row.IsTrack))
            return $"{count} {(count == 1 ? "track" : "tracks")} {(fileIndex ? "listed" : "found")}.";
        return $"{count} {(count == 1 ? "result" : "results")} found.";
    }

    private void ApplyResults()
    {
        rows = view.Apply(found);
        random.Enabled = rows.Length > 0;
        adapter.NotifyDataSetChanged();
        resultActions.Visibility = found.Length == 0 ? ViewStates.Gone : ViewStates.Visible;
        status.Text = rows.Length == found.Length ? summary : $"{rows.Length} of {found.Length} results shown.";
    }

    private void SaveBrowse() => playback.SaveBrowse(resultsId, view, focused);

    private void RememberRow(int index)
    {
        if (search != null || resultsId == Guid.Empty || index < 0 || index >= rows.Length) return;
        var row = rows[index];
        if (!SessionStore.CanSave(row) || row.Url == focused) return;
        focused = row.Url;
        SaveBrowse();
    }

    private void FocusResult(Uri? url)
    {
        if (url == null) return;
        int index = Array.FindIndex(rows, row => row.Url == url);
        if (index < 0) return;
        FocusListRow(list, index, url, () => index < rows.Length && rows[index].Url == url);
    }

    private void ScrollToPlayingRow(Station playing)
    {
        if (search != null || !list.HasWindowFocus) return;
        int index = Array.FindIndex(rows, row => row.Url == playing.Url && row.IsTrack == playing.IsTrack);
        if (index < 0) return;
        var child = list.GetChildAt(index - list.FirstVisiblePosition);
        if (child == null)
            list.SmoothScrollToPosition(index);
        else if (child.Top < list.PaddingTop)
            list.SmoothScrollBy(child.Top - list.PaddingTop, 150);
        else if (child.Bottom > list.Height - list.PaddingBottom)
            list.SmoothScrollBy(child.Bottom - list.Height + list.PaddingBottom, 150);
    }

    private void FocusPlayingRow(ListView target, Station[] entries, Station row, bool remember = false)
    {
        bool Current() => target.HasWindowFocus && playingResult == (row.Url, row.IsTrack) &&
            (remember ? ReferenceEquals(entries, rows) && search == null : target == favouritesList);
        if (!Current()) return;
        int index = Array.FindIndex(entries, item => item.Url == row.Url && item.IsTrack == row.IsTrack);
        if (index < 0) return;
        if (remember) RememberRow(index);
        FocusListRow(target, index, row.Url, Current, focusKeyboard: true);
    }

    private void FocusListRow(ListView target, int index, Uri url, Func<bool> current, bool focusKeyboard = false)
    {
        target.SetSelectionFromTop(index, Dp(12));
        if (!TouchExploration && (!focusKeyboard || target.IsInTouchMode)) return;
        int tries = 0;
        void FocusVisibleRow()
        {
            if (IsDestroyed || !current()) return;
            if (target.GetChildAt(index - target.FirstVisiblePosition) is ResultRow row && row.Matches(url))
            {
                bool focusedRow = TouchExploration
                    ? row.PerformAccessibilityAction((Android.Views.Accessibility.Action)AccessibilityFocus, null)
                    : row.RequestFocus();
                if (focusedRow) return;
            }
            if (++tries < 8) target.PostDelayed(FocusVisibleRow, 50);
        }
        target.Post(FocusVisibleRow);
    }

    private bool TouchExploration => (GetSystemService(AccessibilityService) as AccessibilityManager)?.IsTouchExplorationEnabled == true;

    private void UpdatePlayback()
    {
        UpdatePlaybackTools();
        previous.Enabled = playback.CanPrevious;
        next.Enabled = playback.CanNext;
        playPause.Enabled = playback.Current != null;
        playPause.Text = playback.Active ? "Pause" : "Play";
        string? title = playback.DisplayTitle;
        nowPlaying.Text = title == null ? "Now Playing" : $"Now Playing · {title}";
        nowPlaying.ContentDescription = title == null ? "Now Playing, nothing playing"
            : playback.State == TransportState.Playing ? $"Now Playing, {title}"
            : $"Now Playing, {title}, {playback.State}";
        UpdatePanel();
        var playing = playback.State == TransportState.Playing ? playback.Current : null;
        var key = playing == null ? ((Uri Url, bool IsTrack)?)null : (playing.Url, playing.IsTrack);
        if (key == playingResult) return;
        playingResult = key;
        RefreshPlayingRows(list);
        if (favouritesList != null) RefreshPlayingRows(favouritesList);
    }

    private static void RefreshPlayingRows(ListView target)
    {
        for (int i = 0; i < target.ChildCount; i++)
            if (target.GetChildAt(i) is ResultRow row) row.RefreshPlaying();
    }

    private void Feedback(int sound)
    {
        if (preferences.Sounds) PlayCue(sound);
        if (preferences.Haptics) Pulse();
    }

    private void PlayCue(int sound)
    {
        cue?.Release();
        cue?.Dispose();
        cue = MediaPlayer.Create(this, sound);
        cue?.SetVolume(.55f, .55f);
        cue?.Start();
    }

    private void Pulse(int duration = 45)
    {
        var motor = OperatingSystem.IsAndroidVersionAtLeast(31)
            ? (GetSystemService(VibratorManagerService) as VibratorManager)?.DefaultVibrator
            : GetSystemService(VibratorService) as Vibrator;
        motor?.Vibrate(VibrationEffect.CreateOneShot(duration, VibrationEffect.DefaultAmplitude)!);
    }

    private void Announce(string message)
    {
        status.Text = message;
        Toast.MakeText(this, message, ToastLength.Short)?.Show();
    }

    private int Dp(float v) => (int)(v * (Resources!.DisplayMetrics!.Density) + 0.5f);

    private ImageButton ToolbarButton(int icon, string label, System.Action press)
    {
        var button = new ImageButton(this)
        {
            ContentDescription = label,
            TooltipText = label,
            Focusable = true
        };
        button.SetImageResource(icon);
        using var background = new Android.Util.TypedValue();
        if (Theme!.ResolveAttribute(Android.Resource.Attribute.SelectableItemBackground, background, true))
            button.SetBackgroundResource(background.ResourceId);
        button.Click += (_, _) => press();
        return button;
    }

    private Button Button(string label, System.Action press)
    {
        var button = new Button(this) { Text = label };
        button.SetAllCaps(false);
        button.SetMinHeight(Dp(48));
        button.Click += (_, _) => press();
        return button;
    }

    private TextView Text(string label, float size = 16) => new(this) { Text = label, TextSize = size };

    private LinearLayout Column() => new(this) { Orientation = Orientation.Vertical };

    private LinearLayout Bar(params Button[] buttons)
    {
        var bar = new LinearLayout(this) { Orientation = Orientation.Horizontal };
        foreach (var button in buttons)
            bar.AddView(button, new LinearLayout.LayoutParams(0, LayoutParams.WrapContent, 1));
        return bar;
    }

    private static void Heading(TextView text)
    {
        text.SetTypeface(null, Android.Graphics.TypefaceStyle.Bold);
        if (OperatingSystem.IsAndroidVersionAtLeast(28)) text.AccessibilityHeading = true;
    }
}

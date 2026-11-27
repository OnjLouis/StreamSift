using Android.App;
using Android.Content;
using Android.Views;
using Android.Widget;
using static Android.Views.ViewGroup;

namespace StreamSift.Mobile;

public sealed partial class MainActivity
{
    private TextView? panelStatus;
    private Button? panelPrevious, panelPlay, panelNext;
    private LinearLayout? seekControls;
    private SeekBar? position;
    private bool trackingPosition;
    private Action? refreshPanel;

    private void ShowHistory()
    {
        var entries = preferences.History();
        var dialog = new AlertDialog.Builder(this).SetTitle("Recent Searches")!
            .SetNegativeButton("Close", (_, _) => { })!;
        if (entries.Length == 0)
            dialog.SetMessage("No recent searches");
        else
        {
            dialog.SetItems(entries, (_, args) => _ = SearchFor(entries[args.Which]));
            dialog.SetNeutralButton("Clear", (_, _) => new AlertDialog.Builder(this).SetTitle("Clear Recent Searches?")!
                .SetMessage("This removes saved searches from this phone.")!
                .SetNegativeButton("Cancel", (_, _) => { })!
                .SetPositiveButton("Clear", (_, _) =>
                {
                    preferences.ClearHistory();
                    playback.Store.ClearResults();
                    resultsId = Guid.Empty;
                    focused = null;
                    SaveBrowse();
                })!.Show());
        }
        dialog.Show();
    }

    private void ShowOptions()
    {
        var content = PanelContent();
        var formats = ResultView.Formats(found).Prepend("All formats").ToArray();
        var orders = ResultView.AvailableOrders(found);
        bool hasFiles = found.Any(row => row.IsTrack);
        string[] orderNames = orders.Select(order => ResultView.OrderLabel(order, hasFiles)).ToArray();
        int formatIndex = Math.Max(0, Array.IndexOf(formats, view.Format));
        int orderIndex = Math.Max(0, Array.IndexOf(orders, view.Order));
        var format = Button($"Format, {formats[formatIndex]}", () => { });
        var sort = Button($"Sort by, {orderNames[orderIndex]}", () => { });
        format.Click += (_, _) => Choose("Format", formats, formatIndex, chosen =>
        {
            formatIndex = chosen;
            format.Text = $"Format, {formats[chosen]}";
        });
        sort.Click += (_, _) => Choose("Sort by", orderNames, orderIndex, chosen =>
        {
            orderIndex = chosen;
            sort.Text = $"Sort by, {orderNames[chosen]}";
        });
        content.AddView(format);
        content.AddView(sort);
        new AlertDialog.Builder(this).SetTitle("Filter and Sort")!.SetView(Scroll(content))!
            .SetNegativeButton("Cancel", (_, _) => { })!
            .SetPositiveButton("Apply", (_, _) =>
            {
                view = new ResultView(formatIndex == 0 ? null : formats[formatIndex], orders[orderIndex]);
                ApplyResults();
                if (!rows.Any(row => row.Url == focused)) focused = rows.FirstOrDefault(SessionStore.CanSave)?.Url;
                SaveBrowse();
                FocusResult(focused);
            })!.Show();
    }

    private void Choose(string title, string[] choices, int selected, Action<int> apply)
    {
        var dialog = new AlertDialog.Builder(this).SetTitle(title)!
            .SetSingleChoiceItems(choices, selected, (Android.Content.IDialogInterfaceOnClickListener?)null)!
            .SetNegativeButton("Cancel", (_, _) => { })!
            .Create()!;
        dialog.Show();
        dialog.ListView!.ItemClick += (_, args) =>
        {
            apply(args.Position);
            dialog.Dismiss();
        };
    }

    private void ShowSettings()
    {
        var content = PanelContent();
        AddHeading(content, "Search Feedback");
        content.AddView(Text("Choose how StreamSift signals search progress and results."));
        content.AddView(Switch("Search sounds", preferences.Sounds, enabled =>
        {
            preferences.Sounds = enabled;
            if (enabled) PlayCue(Resource.Raw.done);
        }));
        content.AddView(Switch("Search haptics", preferences.Haptics, enabled =>
        {
            preferences.Haptics = enabled;
            if (enabled) Pulse();
        }));
        AddHeading(content, "Playback");
        content.AddView(Switch("Resume playback on launch", preferences.ResumeOnLaunch, value => preferences.ResumeOnLaunch = value));
        content.AddView(Text("Resume picks up a file at its saved position or rejoins a live stream."));
        content.AddView(Switch("Buffer next item", preferences.BufferNext, value => preferences.BufferNext = value));
        content.AddView(Text("Keeps current audio playing while Previous, Next, or Play Random prepares another item."));
        var intervalLabel = Text("");
        intervalLabel.ImportantForAccessibility = ImportantForAccessibility.No;
        var interval = new SeekBar(this)
        {
            Max = MobilePreferences.SkipIntervals.Length - 1,
            Progress = Array.IndexOf(MobilePreferences.SkipIntervals, preferences.SkipSeconds),
            KeyProgressIncrement = 1
        };
        void LabelInterval()
        {
            string value = $"{MobilePreferences.SkipIntervals[interval.Progress]} seconds";
            intervalLabel.Text = $"Skip interval, {value}";
            SliderText(interval, "Skip interval", value);
        }
        LabelInterval();
        interval.ProgressChanged += (_, args) =>
        {
            if (args.FromUser) preferences.SkipSeconds = MobilePreferences.SkipIntervals[args.Progress];
            LabelInterval();
        };
        content.AddView(intervalLabel);
        content.AddView(interval);
        content.AddView(Text("The skip interval controls both skip buttons in Now Playing."));
        AddHeading(content, "Display");
        content.AddView(Switch("Show underscores as spaces", preferences.UnderscoresAsSpaces, enabled =>
        {
            playback.SetUnderscoresAsSpaces(enabled);
            adapter.NotifyDataSetChanged();
        }));
        AddHeading(content, "App Updates");
        content.AddView(Button("Check for Updates", () => _ = CheckForUpdateAsync()));
        AddHeading(content, "Support StreamSift");
        content.AddView(Button("Donate", () =>
        {
            try { StartActivity(new Intent(Intent.ActionView, global::Android.Net.Uri.Parse("https://onj.me/donate"))); }
            catch (ActivityNotFoundException) { Toast.MakeText(this, "No browser is available.", ToastLength.Short)?.Show(); }
        }));
        content.AddView(Button("Privacy", () => new AlertDialog.Builder(this).SetTitle("Privacy")!
            .SetMessage(PrivacyNotice.Text)!.SetPositiveButton("Back", (_, _) => { })!.Show()));
        OpenPanel("Settings", content, settings, showDone: false);
    }

    private void ShowNowPlaying()
    {
        if (panelStatus != null) return;
        var content = PanelContent();
        panelPrevious = Button("Previous", playback.Previous);
        panelPlay = Button("Play", playback.Toggle);
        panelNext = Button("Next", playback.Next);
        content.AddView(Bar(panelPrevious, panelPlay, panelNext));
        panelStatus = Text("");
        content.AddView(panelStatus);

        seekControls = Column();
        position = new SeekBar(this) { ContentDescription = "Playback position" };
        position.StartTrackingTouch += (_, _) => trackingPosition = true;
        position.StopTrackingTouch += (_, _) =>
        {
            trackingPosition = false;
            if (position != null) playback.Seek(position.Progress);
        };
        position.ProgressChanged += (_, args) =>
        {
            if (args.FromUser && !trackingPosition) playback.Seek(args.Progress);
        };
        seekControls.AddView(position);
        int seconds = preferences.SkipSeconds;
        var back = Button($"Back {seconds}s", () => playback.Skip(-seconds));
        back.ContentDescription = $"Skip back {seconds} seconds";
        var forward = Button($"Forward {seconds}s", () => playback.Skip(seconds));
        forward.ContentDescription = $"Skip forward {seconds} seconds";
        seekControls.AddView(Bar(back, forward));
        content.AddView(seekControls);

        var volume = new SeekBar(this) { Max = 100, Progress = (int)Math.Round(preferences.Volume * 100) };
        SliderText(volume, "Playback volume", $"{volume.Progress} percent");
        volume.ProgressChanged += (_, args) =>
        {
            if (args.FromUser) playback.SetVolume(args.Progress / 100f);
            SliderText(volume, "Playback volume", $"{args.Progress} percent");
        };
        var label = Text("Playback Volume");
        label.ImportantForAccessibility = ImportantForAccessibility.No;
        content.AddView(label);
        content.AddView(volume);
        content.AddView(Switch("Repeat current file", preferences.RepeatFile, value => preferences.RepeatFile = value));
        content.AddView(Switch("Auto-advance files", preferences.AutoAdvance, value => preferences.AutoAdvance = value));

        var dialog = OpenPanel("Now Playing", content, nowPlaying);
        dialog.DismissEvent += (_, _) =>
        {
            if (refreshPanel != null) main.RemoveCallbacks(refreshPanel);
            refreshPanel = null;
            panelStatus = null;
            panelPrevious = panelPlay = panelNext = null;
            seekControls = null;
            position = null;
            trackingPosition = false;
        };
        refreshPanel = () =>
        {
            UpdatePanel();
            if (refreshPanel != null) main.PostDelayed(refreshPanel, 500);
        };
        UpdatePanel();
        main.PostDelayed(refreshPanel, 500);
    }

    private void UpdatePanel()
    {
        if (panelStatus == null || panelPrevious == null || panelPlay == null || panelNext == null ||
            seekControls == null || position == null) return;
        string description = playback.DisplayTitle is { } title
            ? $"{playback.State} {title}" : "Nothing playing";
        if (panelStatus.Text != description) panelStatus.Text = description;
        panelPrevious.Enabled = playback.CanPrevious;
        panelPlay.Enabled = playback.Current != null;
        panelPlay.Text = playback.Active ? "Pause" : "Play";
        panelNext.Enabled = playback.CanNext;
        int duration = playback.Duration;
        seekControls.Visibility = duration > 0 ? ViewStates.Visible : ViewStates.Gone;
        if (duration <= 0 || trackingPosition) return;
        position.Max = duration;
        position.Progress = Math.Min(playback.Position, duration);
        SliderText(position, "Playback position", $"{TimeLabel(position.Progress)} of {TimeLabel(duration)}");
    }

    private static string TimeLabel(int milliseconds)
    {
        var time = TimeSpan.FromMilliseconds(milliseconds);
        return time.TotalHours >= 1 ? time.ToString(@"h\:mm\:ss") : time.ToString(@"m\:ss");
    }

    private static void SliderText(SeekBar slider, string label, string value)
    {
        if (OperatingSystem.IsAndroidVersionAtLeast(30))
        {
            slider.ContentDescription = label;
            if (slider.StateDescription != value) slider.StateDescription = value;
        }
        else
            slider.ContentDescription = $"{label}, {value}";
    }

    private AlertDialog OpenPanel(string title, View contents, View returnFocus, bool showDone = true, bool scroll = true)
    {
        var builder = new AlertDialog.Builder(this).SetTitle(title)!.SetView(scroll ? Scroll(contents) : contents)!;
        if (showDone) builder.SetPositiveButton("Done", (_, _) => { });
        var dialog = builder.Show()!;
        dialog.DismissEvent += (_, _) => RestorePanelFocus(returnFocus);
        return dialog;
    }

    private void RestorePanelFocus(View control) => control.Post(() =>
    {
        if (!control.HasWindowFocus) return;
        if (TouchExploration)
            control.PerformAccessibilityAction((Android.Views.Accessibility.Action)AccessibilityFocus, null);
        else
            control.RequestFocus();
    });

    private LinearLayout PanelContent()
    {
        var result = Column();
        result.SetPadding(Dp(18), Dp(6), Dp(18), Dp(10));
        return result;
    }

    private ScrollView Scroll(View child)
    {
        var scroll = new ScrollView(this);
        scroll.AddView(child, new LayoutParams(LayoutParams.MatchParent, LayoutParams.WrapContent));
        return scroll;
    }

    private void AddHeading(LinearLayout panel, string label)
    {
        var heading = Text(label, 18);
        heading.SetPadding(0, Dp(10), 0, Dp(4));
        Heading(heading);
        panel.AddView(heading);
    }

    private Switch Switch(string label, bool value, Action<bool> changed)
    {
        var toggle = new Switch(this) { Text = label, Checked = value };
        toggle.SetMinHeight(Dp(48));
        toggle.CheckedChange += (_, args) => changed(args.IsChecked);
        return toggle;
    }
}

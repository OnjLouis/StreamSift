using Android.App;
using Android.Content;
using Android.Views;
using Android.Widget;

namespace StreamSift.Mobile;

public sealed partial class MainActivity
{
    private Button random = null!;
    private ImageButton timers = null!;

    private ImageButton BuildTimersButton() =>
        timers = ToolbarButton(Resource.Drawable.toolbar_timer, "Timers", ShowTimers);

    private bool AlarmScheduled => playback.Store.ReadAlarm() is { } saved && saved.Time > DateTimeOffset.UtcNow &&
        RadioAlarmScheduler.HasPermission(this);

    private void UpdatePlaybackTools()
    {
        string description = "Timers";
        if (playback.SleepRemaining.HasValue) description += ", sleep timer running";
        if (AlarmScheduled) description += ", alarm scheduled";
        timers.ContentDescription = description;
    }

    private void ShowTimers()
    {
        string sleep = playback.SleepRemaining.HasValue ? "Sleep Timer, running" : "Sleep Timer";
        string alarm = AlarmScheduled ? "Alarm, scheduled" : "Alarm";
        Action? open = null;
        var dialog = new AlertDialog.Builder(this).SetTitle("Timers")!
            .SetItems(new[] { sleep, alarm }, (_, args) => open = args.Which == 0 ? ShowSleepTimer : ShowAlarm)!
            .SetNegativeButton("Back", (_, _) => { })!.Show()!;
        dialog.DismissEvent += (_, _) =>
        {
            if (open == null) RestorePanelFocus(timers);
            else open();
        };
    }

    private void ShowSleepTimer()
    {
        var content = PanelContent();
        var duration = new EditText(this)
        {
            Hint = "Duration in minutes",
            InputType = Android.Text.InputTypes.ClassNumber
        };
        duration.SetSingleLine(true);
        if (playback.SleepRemaining is { } remaining)
            content.AddView(Text($"About {Math.Max(1, Math.Ceiling(remaining.TotalMinutes)):0} minutes remaining."));
        content.AddView(duration);
        var builder = new AlertDialog.Builder(this).SetTitle("Sleep Timer")!.SetView(content)!
            .SetMessage("Playback fades over the final 30 seconds.")!
            .SetNegativeButton("Back", (_, _) => { })!.SetPositiveButton("Start", (_, _) => { })!;
        if (playback.SleepRemaining.HasValue)
            builder.SetNeutralButton("Cancel Timer", (_, _) => playback.CancelSleep());
        var dialog = builder.Show()!;
        dialog.DismissEvent += (_, _) => RestorePanelFocus(timers);
        var start = dialog.GetButton((int)DialogButtonType.Positive)!;
        start.TooltipText = "Starts the sleep timer for the duration you entered.";
        dialog.GetButton((int)DialogButtonType.Negative)!.TooltipText =
            "Returns to StreamSift and keeps the current timer setting.";
        if (dialog.GetButton((int)DialogButtonType.Neutral) is { } cancel)
            cancel.TooltipText = "Cancels the running sleep timer and restores playback volume.";
        start.Enabled = false;
        duration.TextChanged += (_, _) => start.Enabled = int.TryParse(duration.Text, out int minutes) && minutes > 0;
        start.Click += (_, _) =>
        {
            if (!int.TryParse(duration.Text, out int minutes) || minutes <= 0)
            {
                duration.Error = "Enter a positive number of minutes.";
                duration.RequestFocus();
                return;
            }
            playback.StartSleep(TimeSpan.FromMinutes(minutes));
            dialog.Dismiss();
            Announce($"Sleep timer set for {minutes} {(minutes == 1 ? "minute" : "minutes")}.");
        };
    }

    private void ShowAlarm()
    {
        var previous = playback.Store.ReadAlarm();
        var stations = playback.Store.ReadFavourites().Where(row => !row.IsTrack).ToArray();
        string status = previous is { } saved && saved.Time > DateTimeOffset.UtcNow && RadioAlarmScheduler.HasPermission(this)
            ? $"Scheduled for {saved.Time.LocalDateTime:f}. {preferences.DisplayName(saved.Station.Name)}."
            : "No alarm scheduled.";
        var content = PanelContent();
        content.AddView(Text(status));
        content.AddView(Text(stations.Length == 0 ? "Add a live station to Favourites to set an alarm."
            : "Choose a favourite station and a time for a one-time alarm."));
        var dialog = OpenPanel("Alarm", content, timers, showDone: false);
        if (stations.Length > 0)
            content.AddView(Button("Choose Station", () =>
            {
                dialog.Dismiss();
                new AlertDialog.Builder(this).SetTitle("Alarm Station")!
                    .SetItems(stations.Select(row => preferences.DisplayName(row.Name)).ToArray(),
                        (_, item) => ChooseAlarmTime(stations[item.Which], previous))!
                    .SetNegativeButton("Cancel", (_, _) => { })!.Show();
            }));
        if (previous != null)
            content.AddView(Button("Cancel Alarm", () =>
            {
                if (!playback.Store.ClearAlarm())
                {
                    Announce("The alarm could not be cancelled.");
                    return;
                }
                RadioAlarmScheduler.Cancel(this, previous);
                UpdatePlaybackTools();
                dialog.Dismiss();
                Announce("Alarm cancelled.");
            }));
    }

    private void ChooseAlarmTime(Station station, RadioAlarm? previous)
    {
        var time = previous?.Time.LocalDateTime ?? DateTime.Now.AddHours(1);
        new TimePickerDialog(this, (_, args) =>
            ScheduleAlarm(station, new TimeOnly(args.HourOfDay, args.Minute)), time.Hour, time.Minute,
            Android.Text.Format.DateFormat.Is24HourFormat(this)).Show();
    }

    private void ScheduleAlarm(Station station, TimeOnly time)
    {
        if (!RadioAlarmScheduler.HasPermission(this))
        {
            new AlertDialog.Builder(this).SetTitle("Allow Alarms & Reminders")!
                .SetMessage("Android requires this access to wake the phone at the time you choose. Return to StreamSift to set the alarm after allowing access.")!
                .SetNegativeButton("Cancel", (_, _) => { })!.SetPositiveButton("Open Settings", (_, _) =>
                {
                    if (OperatingSystem.IsAndroidVersionAtLeast(31))
                        StartActivity(new Intent(Android.Provider.Settings.ActionRequestScheduleExactAlarm,
                            global::Android.Net.Uri.Parse($"package:{PackageName}")));
                })!.Show();
            return;
        }

        var previous = playback.Store.ReadAlarm();
        var alarm = new RadioAlarm(Guid.NewGuid(),
            PlaybackTimers.NextAlarm(time, DateTimeOffset.Now, TimeZoneInfo.Local), station);
        if (!playback.Store.WriteAlarm(alarm))
        {
            Announce("The alarm could not be saved.");
            return;
        }
        if (!RadioAlarmScheduler.Schedule(this, alarm))
        {
            if (previous == null) playback.Store.ClearAlarm();
            else playback.Store.WriteAlarm(previous);
            Announce("Android could not schedule the alarm.");
            return;
        }
        if (previous != null) RadioAlarmScheduler.Cancel(this, previous);
        UpdatePlaybackTools();
        Announce($"Alarm set for {alarm.Time.LocalDateTime:f}.");
    }
}

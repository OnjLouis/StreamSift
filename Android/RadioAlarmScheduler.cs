using Android.App;
using Android.Content;

namespace StreamSift.Mobile;

internal static class RadioAlarmScheduler
{
    internal const string PlayAction = "me.onj.streamsift.RADIO_ALARM";
    internal const string AlarmId = "alarm-id";

    internal static bool HasPermission(Context context) => !OperatingSystem.IsAndroidVersionAtLeast(31) ||
        Manager(context).CanScheduleExactAlarms();

    internal static bool Schedule(Context context, RadioAlarm alarm)
    {
        if (!HasPermission(context)) return false;
        using var open = PendingIntent.GetActivity(context, 0, new Intent(context, typeof(MainActivity)),
            PendingIntentFlags.Immutable | PendingIntentFlags.UpdateCurrent);
        using var operation = Operation(context, alarm.Id, PendingIntentFlags.UpdateCurrent);
        if (operation == null) return false;
        try
        {
            Manager(context).SetAlarmClock(new AlarmManager.AlarmClockInfo(alarm.Time.ToUnixTimeMilliseconds(), open), operation);
            return true;
        }
        catch (Java.Lang.SecurityException) { return false; }
    }

    internal static void Cancel(Context context, RadioAlarm alarm)
    {
        using var operation = Operation(context, alarm.Id, PendingIntentFlags.NoCreate);
        if (operation == null) return;
        Manager(context).Cancel(operation);
        operation.Cancel();
    }

    internal static RadioAlarm? Due(SessionStore store, string? id)
    {
        var now = DateTimeOffset.UtcNow;
        var alarm = store.ReadAlarm();
        return alarm != null && Guid.TryParse(id, out var key) && key == alarm.Id &&
            alarm.Time <= now && now - alarm.Time < TimeSpan.FromMinutes(5) ? alarm : null;
    }

    private static PendingIntent? Operation(Context context, Guid id, PendingIntentFlags flags)
    {
        using var intent = new Intent(context, typeof(RadioAlarmReceiver)).SetAction(PlayAction)!
            .SetData(global::Android.Net.Uri.Parse($"streamsift-alarm:{id:D}"))!
            .PutExtra(AlarmId, id.ToString("D"));
        return PendingIntent.GetBroadcast(context, 0, intent, PendingIntentFlags.Immutable | flags);
    }

    private static AlarmManager Manager(Context context) => (AlarmManager)context.GetSystemService(Context.AlarmService)!;
}

[BroadcastReceiver(Enabled = true, Exported = false)]
[IntentFilter([Intent.ActionBootCompleted])]
public sealed class RadioAlarmReceiver : BroadcastReceiver
{
    public override void OnReceive(Context? context, Intent? intent)
    {
        if (context == null || intent == null) return;
        var store = PlaybackService.For(context).Store;
        if (intent.Action == Intent.ActionBootCompleted)
        {
            if (store.ReadAlarm() is { } saved && saved.Time > DateTimeOffset.UtcNow)
                RadioAlarmScheduler.Schedule(context, saved);
            return;
        }
        if (intent.Action != RadioAlarmScheduler.PlayAction) return;
        string? id = intent.GetStringExtra(RadioAlarmScheduler.AlarmId);
        if (RadioAlarmScheduler.Due(store, id) == null) return;
        using var start = new Intent(context, typeof(PlaybackService)).SetAction(RadioAlarmScheduler.PlayAction)!
            .PutExtra(RadioAlarmScheduler.AlarmId, id);
        try { context.StartForegroundService(start); }
        catch (Exception error) when (error is Java.Lang.IllegalStateException or Java.Lang.SecurityException) { }
    }
}

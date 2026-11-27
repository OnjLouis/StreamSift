using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.Media;
using Android.Media.Session;
using Android.OS;

namespace StreamSift.Mobile;

[Service(Name = "me.onj.streamsift.PlaybackService", Exported = false, ForegroundServiceType = ForegroundService.TypeMediaPlayback)]
public sealed class PlaybackService : Service
{
    private const int NotificationId = 1;
    private const string Channel = "playback";
    private const string PreviousAction = "me.onj.streamsift.PREVIOUS";
    private const string ToggleAction = "me.onj.streamsift.TOGGLE";
    private const string NextAction = "me.onj.streamsift.NEXT";
    private static AndroidPlayback? current;
    private AndroidPlayback playback = null!;
    private MediaSession session = null!;

    internal static AndroidPlayback For(Context context) => current ??= new AndroidPlayback(context.ApplicationContext!);
    internal static void Start(Context context) => context.StartForegroundService(new Intent(context, typeof(PlaybackService)));

    public override void OnCreate()
    {
        base.OnCreate();
        playback = For(this);
        session = new MediaSession(this, "StreamSift");
        session.SetCallback(new MediaCommands(playback));
        session.Active = true;
        var manager = (NotificationManager)GetSystemService(NotificationService)!;
        manager.CreateNotificationChannel(new NotificationChannel(Channel, "Playback", NotificationImportance.Low));
        playback.Changed += Publish;
    }

    public override StartCommandResult OnStartCommand(Intent? intent, StartCommandFlags flags, int startId)
    {
        switch (intent?.Action)
        {
            case RadioAlarmScheduler.PlayAction:
                if (RadioAlarmScheduler.Due(playback.Store, intent.GetStringExtra(RadioAlarmScheduler.AlarmId)) is { } alarm &&
                    playback.Store.ClearAlarm())
                {
                    // Audio focus on recent Android requires foreground playback before a background alarm can start.
                    var notice = new Notification.Builder(this, Channel).SetSmallIcon(Resource.Drawable.streamsift)!
                        .SetContentTitle("StreamSift radio alarm")!.SetContentText(alarm.Station.Name)!.Build()!;
                    StartForeground(NotificationId, notice);
                    playback.StartAlarm(alarm.Station);
                }
                break;
            case PreviousAction: playback.Previous(); break;
            case ToggleAction: playback.Toggle(); break;
            case NextAction: playback.Next(); break;
        }
        Publish();
        return StartCommandResult.NotSticky;
    }

    public override IBinder? OnBind(Intent? intent) => null;

    public override void OnTaskRemoved(Intent? rootIntent)
    {
        playback.CloseTask();
        StopForeground(StopForegroundFlags.Remove);
        StopSelf();
        base.OnTaskRemoved(rootIntent);
    }

    public override void OnDestroy()
    {
        playback.Changed -= Publish;
        session.Release();
        session.Dispose();
        base.OnDestroy();
    }

    private void Publish()
    {
        if (playback.Current is not { } item)
        {
            StopSelf();
            return;
        }
        var state = playback.State switch
        {
            TransportState.Playing => PlaybackStateCode.Playing,
            TransportState.Buffering or TransportState.Reconnecting => PlaybackStateCode.Buffering,
            TransportState.Finished => PlaybackStateCode.Stopped,
            _ => PlaybackStateCode.Paused
        };
        var actions = PlaybackState.ActionPlay | PlaybackState.ActionPause | PlaybackState.ActionPlayPause;
        if (playback.CanPrevious) actions |= PlaybackState.ActionSkipToPrevious;
        if (playback.CanNext) actions |= PlaybackState.ActionSkipToNext;
        if (playback.Duration > 0) actions |= PlaybackState.ActionSeekTo;
        session.SetPlaybackState(new PlaybackState.Builder().SetActions(actions)!
            .SetState(state, playback.Position, playback.State == TransportState.Playing ? 1 : 0)!.Build());
        string station = playback.StationTitle!;
        string title = playback.StreamTitle ?? station;
        string subtitle = playback.StreamTitle != null ? station : item.IsTrack ? "Track" : "Live stream";
        var metadata = new MediaMetadata.Builder().PutString(MediaMetadata.MetadataKeyTitle, title)!
            .PutString(MediaMetadata.MetadataKeyArtist, subtitle)!;
        if (playback.Duration > 0) metadata.PutLong(MediaMetadata.MetadataKeyDuration, playback.Duration);
        session.SetMetadata(metadata.Build());

        var open = PendingIntent.GetActivity(this, 0, new Intent(this, typeof(MainActivity)),
            PendingIntentFlags.Immutable | PendingIntentFlags.UpdateCurrent);
        var style = new Notification.MediaStyle().SetMediaSession(session.SessionToken)!;
        var notice = new Notification.Builder(this, Channel).SetSmallIcon(Resource.Drawable.streamsift)!
            .SetContentTitle(title)!.SetContentText(subtitle)!
            .SetContentIntent(open)!.SetOnlyAlertOnce(true)!.SetOngoing(playback.Active)!.SetStyle(style)!;
        int count = 0;
        if (playback.CanPrevious)
        {
            notice.AddAction(Action(PreviousAction, "Previous", Android.Resource.Drawable.IcMediaPrevious, 1));
            count++;
        }
        notice.AddAction(Action(ToggleAction, playback.Active ? "Pause" : "Play",
            playback.Active ? Android.Resource.Drawable.IcMediaPause : Android.Resource.Drawable.IcMediaPlay, 2));
        count++;
        if (playback.CanNext)
        {
            notice.AddAction(Action(NextAction, "Next", Android.Resource.Drawable.IcMediaNext, 3));
            count++;
        }
        style.SetShowActionsInCompactView(Enumerable.Range(0, count).ToArray());
        StartForeground(NotificationId, notice.Build()!);
    }

    private Notification.Action Action(string name, string title, int icon, int id)
    {
        var intent = new Intent(this, typeof(PlaybackService)).SetAction(name);
        var command = PendingIntent.GetService(this, id, intent, PendingIntentFlags.Immutable | PendingIntentFlags.UpdateCurrent);
        return new Notification.Action.Builder(Android.Graphics.Drawables.Icon.CreateWithResource(this, icon), title, command)
            .Build()!;
    }

    private sealed class MediaCommands(AndroidPlayback player) : MediaSession.Callback
    {
        public override void OnPlay() => player.Play();
        public override void OnPause() => player.Pause();
        public override void OnStop() => player.Pause();
        public override void OnSkipToPrevious() => player.Previous();
        public override void OnSkipToNext() => player.Next();
        public override void OnSeekTo(long pos) => player.Seek(pos);
    }
}

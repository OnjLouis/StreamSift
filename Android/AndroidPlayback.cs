using Android.Content;
using Android.Media;
using Android.OS;

namespace StreamSift.Mobile;

internal enum TransportState { Empty, Paused, Buffering, Reconnecting, Playing, Finished }

internal sealed partial class AndroidPlayback
{
    private const int CheckpointMilliseconds = 5000;
    private readonly Context context;
    private readonly AudioManager audio;
    private readonly AudioFocusRequestClass focus;
    private readonly Handler main = new(Looper.MainLooper!);
    private Station[] queue = [];
    private int index = -1;
    private bool favouritesQueue;
    private MediaPlayer? native;
    private PublicFileSource? nativeInput;
    private Mp2Output? layer2;
    private LiveRadioPlayer? radio;
    private readonly LiveRetry liveRetry = new();
    private const int LiveStallMilliseconds = 25000;
    private Action? reconnect;
    private Action? watchStall;
    private bool prepared;
    private bool holdingFocus;
    private bool seekOnReady;
    private int stagedPosition;
    private bool launchResume;
    private readonly Action tick;
    private SavedPlace? savedPlace;

    internal event Action? Changed;
    internal event Action<Station>? RevealRelativeItem;
    internal event Action<string>? Failed;
    internal MobilePreferences Preferences { get; }
    internal SessionStore Store { get; }
    internal SavedPlace? Place => savedPlace;
    internal Station? Current => index >= 0 ? queue[index] : null;
    internal TransportState State { get; private set; }
    internal bool Active => State is TransportState.Playing or TransportState.Buffering or TransportState.Reconnecting;
    internal string? StreamTitle { get; private set; }
    internal string? StationTitle => Current is { } row ? Preferences.DisplayName(row.Name) : null;
    internal string? DisplayTitle => StreamTitle == null ? StationTitle : $"{StationTitle}, {StreamTitle}";
    internal bool CanPrevious => index > 0 || Current?.IsTrack == true;
    internal bool CanNext => index >= 0 && index + 1 < queue.Length;
    internal int Position => Current?.IsTrack != true ? 0 : !prepared || seekOnReady ? stagedPosition : native?.CurrentPosition ?? stagedPosition;
    internal int Duration => Current?.IsTrack == true && prepared && native is { } engine ? Math.Max(0, engine.Duration) : 0;

    internal AndroidPlayback(Context app)
    {
        context = app;
        audio = (AudioManager)app.GetSystemService(Context.AudioService)!;
        var attributes = new AudioAttributes.Builder().SetUsage(AudioUsageKind.Media)!
            .SetContentType(AudioContentType.Music)!.Build()!;
        focus = new AudioFocusRequestClass.Builder(AudioFocus.Gain).SetAudioAttributes(attributes)!
            .SetOnAudioFocusChangeListener(new FocusListener(this), main)!.Build()!;
        Preferences = new MobilePreferences(app);
        Store = new SessionStore(Path.Combine(app.FilesDir!.AbsolutePath, "session"));
        savedPlace = Store.ReadPlace();
        if (savedPlace?.Media is { } media)
        {
            queue = [media];
            index = 0;
            stagedPosition = Milliseconds(savedPlace.Seconds);
            State = TransportState.Paused;
            launchResume = savedPlace.WasPlaying && Preferences.ResumeOnLaunch;
            favouritesQueue = savedPlace.FavouritesQueue;
            if (favouritesQueue) RestoreQueue(Store.ReadFavourites(), fromFavourites: true);
        }
        tick = Tick;
    }

    internal void RestoreQueue(Station[] rows, bool fromFavourites = false)
    {
        if (favouritesQueue != fromFavourites || Active || native != null || Current is not { } item) return;
        int match = Array.FindIndex(rows, row => row.Url == item.Url && row.IsTrack == item.IsTrack);
        if (match < 0) return;
        queue = rows;
        index = match;
    }

    internal void ResumeOnLaunch()
    {
        if (!launchResume) return;
        launchResume = false;
        Play();
    }

    internal void SaveBrowse(Guid id, ResultView view, Uri? focused)
    {
        savedPlace = new SavedPlace(id, view, focused, Current, Position / 1000d, Active || launchResume, favouritesQueue);
        Store.WritePlace(savedPlace);
    }

    internal void Checkpoint() => SaveBrowse(savedPlace?.ResultsId ?? Guid.Empty,
        savedPlace?.View ?? default, savedPlace?.Focus);

    internal void Select(Station[] rows, int rowIndex, bool fromFavourites = false)
    {
        var row = rows[rowIndex];
        if (Current is { } current && current.Url == row.Url && current.IsTrack == row.IsTrack)
            StartEntry(rows, rowIndex, fromFavourites, toggleSame: true);
        else
            SwitchItem(rows, rowIndex, fromFavourites);
    }

    private void StartEntry(Station[] rows, int rowIndex, bool fromFavourites, bool toggleSame = false, bool wake = false, Action? reveal = null)
    {
        CancelNext();
        favouritesQueue = fromFavourites;
        bool same = Current is { } current && current.Url == rows[rowIndex].Url &&
            current.IsTrack == rows[rowIndex].IsTrack;
        timers.CancelWake();
        if (same && toggleSame)
        {
            queue = rows;
            index = rowIndex;
            launchResume = false;
            Toggle();
            return;
        }
        ResetEngine();
        revealPlayingResult = reveal;
        queue = rows;
        index = rowIndex;
        launchResume = false;
        stagedPosition = 0;
        State = TransportState.Paused;
        if (wake) timers.ArmWake();
        UpdateFades();
        Play(serviceAlreadyStarted: wake);
    }

    internal void CloseTask()
    {
        Checkpoint();
        timers.CancelSleep();
        timers.CancelWake();
        if (fadeTick != null) main.RemoveCallbacks(fadeTick);
        fadeTick = null;
        fadeGain = 1;
        launchResume = savedPlace?.WasPlaying == true && Preferences.ResumeOnLaunch;
        stagedPosition = Position;
        ResetEngine();
        ReleaseFocus();
        main.RemoveCallbacks(tick);
        State = Current == null ? TransportState.Empty : TransportState.Paused;
    }

    internal void Toggle()
    {
        if (Active) Pause();
        else Play();
    }

    internal void Play() => Play(serviceAlreadyStarted: false);

    private void Play(bool serviceAlreadyStarted)
    {
        if (Current is not { } item || Active) return;
        launchResume = false;
        if (!holdingFocus)
        {
            holdingFocus = audio.RequestAudioFocus(focus) == AudioFocusRequest.Granted;
            if (!holdingFocus)
            {
                ReportFailure("Audio playback could not start.");
                return;
            }
        }
        if (State == TransportState.Finished)
        {
            stagedPosition = 0;
            ResetEngine();
        }
        SetState(TransportState.Buffering);
        if (!Active) return;
        try { if (!serviceAlreadyStarted) PlaybackService.Start(context); }
        catch (Java.Lang.IllegalStateException)
        {
            ReportFailure("Audio playback could not start.");
            return;
        }
        if (!item.IsTrack)
        {
            liveRetry.Reset();
            StartLive(item);
            return;
        }
        if (native != null && prepared)
        {
            if (!seekOnReady) StartNative();
            return;
        }

        try
        {
            var engine = CreateFilePlayer();
            var source = new PublicFileSource(item.Url);
            AttachFilePlayer(engine, item, source);
            engine.Prepared += (_, _) =>
            {
                if (native != engine) return;
                prepared = true;
                if (stagedPosition > 0 && Duration > stagedPosition)
                {
                    seekOnReady = true;
                    engine.SeekTo(stagedPosition, MediaPlayerSeekMode.Closest);
                }
                else
                    StartNative();
            };
            engine.SetDataSource(source);
            engine.PrepareAsync();
        }
        catch (Exception error) when (error is Java.Lang.Exception or IOException or ArgumentException)
        {
            ReportFailure($"Could not play {Preferences.DisplayName(item.Name)}.");
        }
    }

    internal void Pause()
    {
        CancelNext();
        timers.CancelWake();
        launchResume = false;
        if (!Active) return;
        stagedPosition = Position;
        if (Current?.IsTrack == true && native != null && prepared)
        {
            if (native.IsPlaying) native.Pause();
        }
        else
            ResetEngine();
        ReleaseFocus();
        SetState(TransportState.Paused);
    }

    internal void Previous()
    {
        if (nextTransition == null && Current is { IsTrack: true } row && (index == 0 || Position > 3000))
        {
            Seek(0);
            if (State == TransportState.Playing) RevealRelativeItem?.Invoke(row);
            return;
        }
        PlayRelative(-1);
    }

    internal void Next() => PlayRelative(1);

    internal void Skip(int seconds) => Seek(Math.Clamp((long)Position + seconds * 1000L, 0, int.MaxValue));

    internal void Seek(long milliseconds)
    {
        if (Current?.IsTrack != true || seekOnReady) return;
        if (native is { } engine && Duration > 0)
            engine.SeekTo(Math.Clamp(milliseconds, 0, Duration), MediaPlayerSeekMode.Closest);
        else if (milliseconds == 0)
        {
            stagedPosition = 0;
            Checkpoint();
            Changed?.Invoke();
        }
    }

    internal void SetVolume(float value)
    {
        Preferences.Volume = value;
        ApplyVolume();
    }

    internal void SetUnderscoresAsSpaces(bool enabled)
    {
        Preferences.UnderscoresAsSpaces = enabled;
        Changed?.Invoke();
    }

    internal void Mp2Started(Mp2Output output) => main.Post(() =>
    {
        if (nextTransition?.Mp2 == output) CommitNext(nextTransition);
        else if (layer2 == output && Active) LivePlaying();
    });

    internal void Mp2Failed(Mp2Output output) => main.Post(() =>
    {
        if (nextTransition?.Mp2 == output) FailNext(nextTransition);
        if (layer2 == output && Current is { } item) Reconnect(item);
    });

    internal void Mp2Title(Mp2Output output, string? title) => main.Post(() =>
    {
        if (nextTransition?.Mp2 == output) nextTransition.Title = title;
        if (layer2 == output && Active) ChangeStreamTitle(title);
    });

    internal void RadioPlaying(LiveRadioPlayer source)
    {
        if (nextTransition?.Radio == source)
        {
            CommitNext(nextTransition);
            return;
        }
        if (radio == source && Active) LivePlaying();
    }

    internal void RadioBuffering(LiveRadioPlayer source)
    {
        if (radio != source || !Active) return;
        liveRetry.Buffering();
        SetState(TransportState.Buffering);
        MonitorLivePlayback();
    }

    internal void RadioFailed(LiveRadioPlayer source) => main.Post(() =>
    {
        if (nextTransition?.Radio == source) FailNext(nextTransition);
        if (radio == source && Current is { } item) Reconnect(item);
    });

    internal void RadioTitle(LiveRadioPlayer source, string? title)
    {
        if (nextTransition?.Radio == source) nextTransition.Title = title;
        if (radio == source && Active) ChangeStreamTitle(title);
    }

    private void ChangeStreamTitle(string? title)
    {
        if (StreamTitle == title) return;
        StreamTitle = title;
        Changed?.Invoke();
    }

    private void StartLive(Station item)
    {
        try
        {
            SetState(TransportState.Buffering);
            if (!Active) return;
            if (!item.IsHls && item.Format.Equals("MP2", StringComparison.OrdinalIgnoreCase))
            {
                layer2 = new Mp2Output(item.Url, OutputVolume, this);
                layer2.Start();
            }
            else
            {
                radio = new LiveRadioPlayer(context, this, OutputVolume);
                radio.Start(item);
            }
            if (State == TransportState.Buffering) MonitorLivePlayback();
        }
        catch (Exception error) when (error is Java.Lang.Exception or IOException or ArgumentException or InvalidOperationException)
        {
            Reconnect(item);
        }
    }

    private void LivePlaying()
    {
        liveRetry.Playing();
        if (watchStall != null) main.RemoveCallbacks(watchStall);
        watchStall = null;
        SetState(TransportState.Playing);
    }

    private void MonitorLivePlayback()
    {
        if (watchStall != null) return;
        watchStall = () =>
        {
            watchStall = null;
            if (State == TransportState.Buffering && Current is { IsTrack: false } item) Reconnect(item);
        };
        main.PostDelayed(watchStall, LiveStallMilliseconds);
    }

    private void Reconnect(Station item)
    {
        if (!Active) return;
        if (liveRetry.NextDelay() is not { } delay)
        {
            ReportFailure($"Could not play {Preferences.DisplayName(item.Name)}.");
            return;
        }
        ResetEngine();
        reconnect = () =>
        {
            reconnect = null;
            StartLive(item);
        };
        SetState(TransportState.Reconnecting);
        main.PostDelayed(reconnect, (long)delay.TotalMilliseconds);
    }

    private void StartNative()
    {
        if (!Active || native is not { } engine) return;
        engine.Start();
        SetState(TransportState.Playing);
    }

    private MediaPlayer CreateFilePlayer()
    {
        var engine = new MediaPlayer();
        engine.SetAudioAttributes(focus.AudioAttributes!);
        engine.SetWakeMode(context, WakeLockFlags.Partial);
        engine.SetVolume(OutputVolume, OutputVolume);
        return engine;
    }

    private void AttachFilePlayer(MediaPlayer engine, Station item, PublicFileSource source)
    {
        native = engine;
        nativeInput = source;
        engine.SeekComplete += (_, _) =>
        {
            if (native != engine) return;
            if (seekOnReady)
            {
                seekOnReady = false;
                StartNative();
            }
            else
            {
                Checkpoint();
                Changed?.Invoke();
            }
        };
        engine.Completion += (_, _) =>
        {
            if (native == engine) Complete();
        };
        engine.Error += (_, args) =>
        {
            args.Handled = true;
            if (native == engine) ReportFailure($"Could not play {Preferences.DisplayName(item.Name)}.");
        };
    }

    private void Complete()
    {
        if (timers.SleepRemaining == TimeSpan.Zero)
        {
            UpdateFades();
            return;
        }
        if (nextTransition is { } pending)
        {
            StartEntry(pending.Entries, pending.Index, pending.FromFavourites, reveal: pending.Reveal);
            return;
        }
        if (Current?.IsTrack == true && Preferences.RepeatFile)
        {
            Seek(0);
            native?.Start();
            SetState(TransportState.Playing);
            return;
        }
        if (Current?.IsTrack == true && Preferences.AutoAdvance && CanNext && (favouritesQueue || queue[index + 1].IsTrack))
        {
            Move(1);
            return;
        }
        ReleaseFocus();
        ResetEngine();
        stagedPosition = 0;
        SetState(TransportState.Finished);
    }

    private void Move(int delta) => MoveTo(index + delta);

    private void MoveTo(int target)
    {
        timers.CancelWake();
        ResetEngine();
        index = target;
        stagedPosition = 0;
        State = TransportState.Paused;
        Play();
    }

    private void ResetEngine()
    {
        CancelNext();
        if (reconnect != null) main.RemoveCallbacks(reconnect);
        if (watchStall != null) main.RemoveCallbacks(watchStall);
        reconnect = watchStall = null;
        StreamTitle = null;
        var previousRadio = radio;
        radio = null;
        previousRadio?.Release();
        var old = native;
        native = null;
        var oldInput = nativeInput;
        nativeInput = null;
        oldInput?.Close();
        old?.Release();
        old?.Dispose();
        oldInput?.Dispose();
        layer2?.Dispose();
        layer2 = null;
        prepared = seekOnReady = false;
    }

    private void ReportFailure(string message)
    {
        timers.CancelWake();
        ResetEngine();
        ReleaseFocus();
        SetState(Current == null ? TransportState.Empty : TransportState.Paused);
        Failed?.Invoke(message);
    }

    private void ReleaseFocus()
    {
        if (!holdingFocus) return;
        holdingFocus = false;
        audio.AbandonAudioFocusRequest(focus);
    }

    private void SetState(TransportState state)
    {
        State = state;
        UpdateFades();
        main.RemoveCallbacks(tick);
        if (Active && Current?.IsTrack == true) main.PostDelayed(tick, CheckpointMilliseconds);
        Checkpoint();
        Changed?.Invoke();
        if (State == TransportState.Playing)
        {
            var reveal = revealPlayingResult;
            revealPlayingResult = null;
            reveal?.Invoke();
        }
    }

    private void Tick()
    {
        if (!Active) return;
        Checkpoint();
        main.PostDelayed(tick, CheckpointMilliseconds);
    }

    private static int Milliseconds(double seconds) => (int)Math.Clamp(seconds * 1000, 0, int.MaxValue);

    private sealed class FocusListener(AndroidPlayback owner) : Java.Lang.Object, AudioManager.IOnAudioFocusChangeListener
    {
        public void OnAudioFocusChange(AudioFocus focusChange)
        {
            if (focusChange is AudioFocus.Loss or AudioFocus.LossTransient) owner.Pause();
        }
    }
}

using Android.OS;

namespace StreamSift.Mobile;

internal sealed partial class AndroidPlayback
{
    private readonly PlaybackTimers timers = new(TimeProvider.System);
    private Action? fadeTick;
    private float fadeGain = 1;
    private float OutputVolume => Preferences.Volume * fadeGain;

    internal TimeSpan? SleepRemaining => timers.SleepRemaining;

    internal void StartSleep(TimeSpan duration)
    {
        timers.StartSleep(duration);
        UpdateFades();
        Changed?.Invoke();
    }

    internal void CancelSleep()
    {
        timers.CancelSleep();
        UpdateFades();
        Changed?.Invoke();
    }

    internal void PlayRandom(Station[] entries, Action<Station> reveal, bool fromFavourites = false)
    {
        int selected = PlaybackTimers.RandomIndex(entries, Current?.Url);
        if (selected >= 0) SwitchItem(entries, selected, fromFavourites, () => reveal(entries[selected]));
    }

    internal void StartAlarm(Station station)
    {
        timers.CancelSleep();
        var entries = Store.ReadFavourites();
        int selected = Array.FindIndex(entries, item => item.Url == station.Url);
        if (selected < 0)
            StartEntry([station], 0, fromFavourites: true, wake: true);
        else
            StartEntry(entries, selected, fromFavourites: true, wake: true);
    }

    private void ApplyVolume()
    {
        float volume = OutputVolume;
        native?.SetVolume(volume, volume);
        if (layer2 != null) layer2.Volume = volume;
        if (radio != null) radio.Volume = volume;
    }

    private void UpdateFades()
    {
        if (fadeTick != null) main.RemoveCallbacks(fadeTick);
        fadeTick = null;
        var sample = timers.Sample(State == TransportState.Playing);
        if (fadeGain != sample.Gain)
        {
            fadeGain = sample.Gain;
            ApplyVolume();
        }
        if (sample.SleepExpired)
        {
            Pause();
            Changed?.Invoke();
        }
        else if (sample.NextCheck is { } delay)
        {
            fadeTick = UpdateFades;
            main.PostDelayed(fadeTick, Math.Max(1L, (long)Math.Ceiling(delay.TotalMilliseconds)));
        }
    }
}

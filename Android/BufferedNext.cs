using Android.Media;

namespace StreamSift.Mobile;

internal sealed partial class AndroidPlayback
{
    private const int BufferNextMilliseconds = 15000;
    private NextTransition? nextTransition;
    private Action? revealPlayingResult;

    private void PlayRelative(int offset)
    {
        var entries = nextTransition?.Entries ?? queue;
        int target = (nextTransition?.Index ?? index) + offset;
        if ((uint)target >= (uint)entries.Length) return;
        var row = entries[target];
        SwitchItem(entries, target, nextTransition?.FromFavourites ?? favouritesQueue,
            () => RevealRelativeItem?.Invoke(row));
    }

    private void SwitchItem(Station[] entries, int target, bool fromFavourites, Action? reveal = null)
    {
        CancelNext();
        if (!Preferences.BufferNext || State != TransportState.Playing)
        {
            StartEntry(entries, target, fromFavourites, reveal: reveal);
            return;
        }

        var row = entries[target];
        if (Current is { } current && row.Url == current.Url && row.IsTrack == current.IsTrack)
        {
            queue = entries;
            index = target;
            favouritesQueue = fromFavourites;
            Checkpoint();
            Changed?.Invoke();
            reveal?.Invoke();
            return;
        }
        var pending = new NextTransition(entries, target, fromFavourites, reveal);
        nextTransition = pending;
        pending.Deadline = () => FailNext(pending);
        main.PostDelayed(pending.Deadline, BufferNextMilliseconds);
        try
        {
            if (row.IsTrack)
            {
                var next = CreateFilePlayer();
                pending.File = next;
                next.Prepared += (_, _) =>
                {
                    if (nextTransition == pending) CommitNext(pending);
                };
                next.Error += (_, args) =>
                {
                    args.Handled = true;
                    if (nextTransition == pending) FailNext(pending);
                };
                pending.FileInput = new PublicFileSource(row.Url);
                next.SetDataSource(pending.FileInput);
                next.PrepareAsync();
            }
            else if (!row.IsHls && row.Format.Equals("MP2", StringComparison.OrdinalIgnoreCase))
            {
                pending.Mp2 = new Mp2Output(row.Url, 0, this);
                pending.Mp2.Start();
            }
            else
            {
                pending.Radio = new LiveRadioPlayer(context, this, 0);
                pending.Radio.Start(row);
            }
        }
        catch (Exception error) when (error is Java.Lang.Exception or IOException or ArgumentException or InvalidOperationException)
        {
            FailNext(pending);
        }
    }

    private void CommitNext(NextTransition pending)
    {
        if (timers.SleepRemaining == TimeSpan.Zero)
        {
            UpdateFades();
            return;
        }
        nextTransition = null;
        main.RemoveCallbacks(pending.Deadline!);
        ResetEngine();
        revealPlayingResult = pending.Reveal;
        queue = pending.Entries;
        index = pending.Index;
        favouritesQueue = pending.FromFavourites;
        stagedPosition = 0;
        timers.CancelWake();
        liveRetry.Reset();
        StreamTitle = pending.Title;
        if (pending.File is { } file)
        {
            AttachFilePlayer(file, queue[index], pending.FileInput!);
            file.SetVolume(OutputVolume, OutputVolume);
            prepared = true;
            StartNative();
        }
        else if (pending.Mp2 is { } mp2)
        {
            layer2 = mp2;
            layer2.Volume = OutputVolume;
            LivePlaying();
        }
        else if (pending.Radio is { } player)
        {
            radio = player;
            radio.Volume = OutputVolume;
            LivePlaying();
        }
    }

    private void FailNext(NextTransition pending)
    {
        if (nextTransition != pending) return;
        string name = Preferences.DisplayName(pending.Entries[pending.Index].Name);
        CancelNext();
        Failed?.Invoke($"Could not buffer {name}.");
    }

    private void CancelNext()
    {
        revealPlayingResult = null;
        if (nextTransition is not { } pending) return;
        nextTransition = null;
        main.RemoveCallbacks(pending.Deadline!);
        pending.FileInput?.Close();
        pending.File?.Release();
        pending.File?.Dispose();
        pending.FileInput?.Dispose();
        pending.Mp2?.Dispose();
        pending.Radio?.Release();
    }

    private sealed class NextTransition(Station[] entries, int index, bool fromFavourites, Action? reveal)
    {
        internal Station[] Entries { get; } = entries;
        internal int Index { get; } = index;
        internal bool FromFavourites { get; } = fromFavourites;
        internal Action? Reveal { get; } = reveal;
        internal MediaPlayer? File;
        internal PublicFileSource? FileInput;
        internal Mp2Output? Mp2;
        internal LiveRadioPlayer? Radio;
        internal Action? Deadline;
        internal string? Title;
    }
}

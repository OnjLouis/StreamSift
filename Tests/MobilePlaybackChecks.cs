using StreamSift.Mobile;

namespace StreamSift;

internal static class MobilePlaybackChecks
{
    internal static int Run()
    {
        using var response = new MemoryStream();
        WriteAudio("123");
        WriteTitle("StreamTitle='Artist - Track';");
        WriteAudio("456");
        response.WriteByte(0);
        WriteAudio("789");
        WriteTitle("StreamTitle='';");
        WriteAudio("0");
        response.Position = 0;
        var titles = new List<string?>();
        using var audio = new LiveAudioStream(response, CancellationToken.None, 3, titles.Add);
        using var decoded = new MemoryStream();
        audio.CopyTo(decoded);
        Require(System.Text.Encoding.ASCII.GetString(decoded.ToArray()) == "1234567890" &&
            audio.Position == 10, "ICY blocks must never reach the audio decoder");
        Require(titles.SequenceEqual(["Artist - Track", null]), "ICY title changes and explicit clears must be delivered");
        using var truncated = new MemoryStream([1, 2, 1, 3]);
        using var shortAudio = new LiveAudioStream(truncated, CancellationToken.None, 2,
            _ => throw new InvalidOperationException("Incomplete metadata must be ignored"));
        Require(shortAudio.ReadByte() == 1 && shortAudio.ReadByte() == 2 && shortAudio.ReadByte() == -1,
            "A truncated ICY block must end the audio stream cleanly");

        var retry = new LiveRetry();
        Require(retry.NextDelay() == TimeSpan.FromSeconds(1) &&
            retry.NextDelay() == TimeSpan.FromSeconds(2) &&
            retry.NextDelay() == TimeSpan.FromSeconds(4) &&
            retry.NextDelay() == TimeSpan.FromSeconds(8) &&
            retry.NextDelay() == TimeSpan.FromSeconds(16) &&
            retry.NextDelay() == null, "Live retries must back off and stop");
        retry.Reset();
        Require(retry.NextDelay() == TimeSpan.FromSeconds(1), "A new playback choice must reset retry backoff");

        var clock = new TimerClock();
        var timers = new PlaybackTimers(clock);
        timers.StartSleep(TimeSpan.FromMinutes(2));
        Require(timers.Sample(true) is { Gain: 1, SleepExpired: false }, "Sleep must keep the saved level before fading");
        clock.Advance(TimeSpan.FromSeconds(105));
        Require(timers.Sample(true) is { Gain: 0.5f, SleepExpired: false }, "Sleep fades over its final 30 seconds");
        clock.Advance(TimeSpan.FromSeconds(15));
        Require(timers.Sample(true) is { Gain: 0, SleepExpired: true }, "Sleep reaches silence before stopping");
        Require(timers.Sample(false) is { Gain: 1, SleepExpired: false }, "The next playback uses the normal level");
        timers.ArmWake();
        clock.Advance(TimeSpan.FromMinutes(2));
        Require(timers.Sample(false).Gain == 0 && timers.Sample(true).Gain == 0,
            "Wake fade starts when sound is ready rather than when buffering begins");
        clock.Advance(TimeSpan.FromSeconds(10));
        Require(timers.Sample(true).Gain == 0.5f, "Wake fades to the saved level");
        clock.Advance(TimeSpan.FromSeconds(10));
        Require(timers.Sample(true) is { Gain: 1, NextCheck: null }, "Wake completes and stops ticking");
        var now = new DateTimeOffset(2026, 11, 25, 8, 30, 0, TimeSpan.Zero);
        Require(PlaybackTimers.NextAlarm(new TimeOnly(8, 0), now, TimeZoneInfo.Utc) == now.AddHours(23.5),
            "An earlier alarm time schedules tomorrow");

        string directory = Path.Combine(Path.GetTempPath(), $"streamsift-favourites-{Guid.NewGuid():N}");
        try
        {
            var row = new Station("Test radio", new Uri("https://audio.example/live"), "MP3", 128,
                new Uri("https://audio.example/"), false);
            Directory.CreateDirectory(directory);
            var store = new SessionStore(directory);
            var titled = row with { ScanTitle = "Song heard during the scan" };
            var saved = SavedResults.Capture("Test radio", [titled], false);
            Require(saved?.Stations is [{ ScanTitle: null }] && titled.ScanTitle != null,
                "The current scan title is visible in results without being saved as current on relaunch");
            Require(store.SetFavourite(titled, true) && store.ReadFavourites().SequenceEqual([row]),
                "Adding a favourite drops the temporary scan title");
            var other = row with { Url = new Uri("https://audio.example/other") };
            Require(PlaybackTimers.RandomIndex([row, other], row.Url) == 1 &&
                PlaybackTimers.RandomIndex([row], row.Url) == 0 && PlaybackTimers.RandomIndex([], null) == -1,
                "Random playback uses the available list and avoids the current station when possible");
            Require(store.SetFavourite(row, true) && store.SetFavourite(row, true) &&
                new SessionStore(directory).ReadFavourites().SequenceEqual([row]), "Favourites must persist without duplicates");
            var place = new SavedPlace(Guid.Empty, default, null, row, 0, WasPlaying: true, FavouritesQueue: true);
            store.WritePlace(place with { Media = titled });
            Require(new SessionStore(directory).ReadPlace() == place, "The current Favourites queue must survive relaunch");
            var alarm = new RadioAlarm(Guid.NewGuid(), now.AddHours(1), row);
            Require(store.WriteAlarm(alarm with { Station = titled }) && new SessionStore(directory).ReadAlarm() == alarm &&
                !store.WriteAlarm(alarm with { Station = row with { IsTrack = true } }),
                "A one-shot alarm persists and requires a live favourite");
            Require(store.ClearAlarm() && new SessionStore(directory).ReadAlarm() == null, "A cancelled alarm stays cancelled");
            var track = row with { Name = "Café, evening mix", IsTrack = true, Format = "MP2", Url = new Uri("https://audio.example/track.mp2") };
            var insecure = row with { Url = new Uri("http://audio.example/saved-station") };
            var signed = row with { Url = new Uri("https://audio.example/live?token=fixture") };
            var roundTrip = FavouritePlaylist.Parse(FavouritePlaylist.Export([row, track, insecure, signed]));
            Require(roundTrip.Stations.Select(item => (item.Url, item.Name, item.Format, item.IsTrack))
                .SequenceEqual(new[] { row, track, insecure }.Select(item => (item.Url, item.Name, item.Format, item.IsTrack))),
                "Favourites playlists preserve item order, format, type, and Unicode names without saving signed links");
            var mobileImport = FavouritePlaylist.Parse(FavouritePlaylist.Export([insecure, track]), httpsOnly: true);
            Require(mobileImport.Stations is [{ IsTrack: true, Format: "MP2" }] && mobileImport.Skipped == 1,
                "Mobile import excludes HTTP-only entries");
            Require(store.ImportFavourites([track, track]) == 1 &&
                new SessionStore(directory).ReadFavourites().SequenceEqual([row, track]), "Mobile favourites import merges duplicates and persists once");
            bool refusedHls = false;
            try { FavouritePlaylist.Parse("#EXTM3U\n#EXT-X-TARGETDURATION:10\nhttps://audio.example/segment.mp3\n"u8); }
            catch (InvalidDataException) { refusedHls = true; }
            Require(refusedHls, "An HLS segment manifest is not a favourites playlist");
            Require(!store.SetFavourite(row with { Url = new Uri("https://audio.example/live?signature=test") }, true),
                "Expiring media URLs must stay out of favourites");
            Require(store.SetFavourite(row, false) && new SessionStore(directory).ReadFavourites().SequenceEqual([track]),
                "Removed favourites must stay removed");
        }
        finally
        {
            File.Delete(Path.Combine(directory, "Favourites.json"));
            File.Delete(Path.Combine(directory, "Place.json"));
            File.Delete(Path.Combine(directory, "Alarm.json"));
            if (Directory.Exists(directory)) Directory.Delete(directory);
        }
        return 26;

        void WriteAudio(string value) => response.Write(System.Text.Encoding.ASCII.GetBytes(value));

        void WriteTitle(string value)
        {
            int blocks = (value.Length + 15) / 16;
            response.WriteByte((byte)blocks);
            WriteAudio(value.PadRight(blocks * 16, '\0'));
        }
    }

    private static void Require(bool passed, string message)
    {
        if (!passed) throw new InvalidOperationException(message);
    }

    internal static async Task<int> CheckSearchHeartbeatAsync()
    {
        using var cancelledQuickly = new CancellationTokenSource();
        int shortSearchPulses = 0;
        var shortSearch = SearchHeartbeat.RunAsync(() => shortSearchPulses++, cancelledQuickly.Token);
        cancelledQuickly.Cancel();
        await shortSearch;
        Require(shortSearchPulses == 0, "A short search must not emit a progress pulse");

        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(12));
        var first = new TaskCompletionSource<TimeSpan>(TaskCreationOptions.RunContinuationsAsynchronously);
        var second = new TaskCompletionSource<TimeSpan>(TaskCreationOptions.RunContinuationsAsynchronously);
        var clock = System.Diagnostics.Stopwatch.StartNew();
        int pulseCount = 0;
        var running = SearchHeartbeat.RunAsync(() =>
        {
            if (++pulseCount == 1) first.SetResult(clock.Elapsed);
            if (pulseCount == 2)
            {
                second.SetResult(clock.Elapsed);
                stop.Cancel();
            }
        }, stop.Token);
        var firstPulse = await first.Task.WaitAsync(stop.Token);
        var secondPulse = await second.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await running;
        Require(firstPulse >= TimeSpan.FromMilliseconds(1900) && secondPulse >= TimeSpan.FromMilliseconds(3900),
            "Search progress pulses should begin and recur at two-second intervals");
        Require(pulseCount == 2, "A cancelled heartbeat must stop emitting progress pulses");
        return 3;
    }

    private sealed class TimerClock : TimeProvider
    {
        private long elapsed;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => elapsed;
        internal void Advance(TimeSpan duration) => elapsed += duration.Ticks;
    }
}

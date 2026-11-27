namespace StreamSift.Mobile;

internal sealed record RadioAlarm(Guid Id, DateTimeOffset Time, Station Station);
internal readonly record struct FadeSample(float Gain, bool SleepExpired, TimeSpan? NextCheck);

internal sealed class PlaybackTimers(TimeProvider clock)
{
    private static readonly TimeSpan SleepFade = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan WakeFade = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan FadeStep = TimeSpan.FromMilliseconds(150);
    private long sleepStarted;
    private TimeSpan? sleepDuration;
    private bool wakeArmed;
    private long? wakeStarted;

    internal TimeSpan? SleepRemaining => sleepDuration is { } duration
        ? TimeSpan.FromTicks(Math.Max(0, (duration - clock.GetElapsedTime(sleepStarted)).Ticks))
        : null;

    internal void StartSleep(TimeSpan duration)
    {
        if (duration <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(duration));
        sleepStarted = clock.GetTimestamp();
        sleepDuration = duration;
    }

    internal void CancelSleep() => sleepDuration = null;
    internal void ArmWake() { wakeArmed = true; wakeStarted = null; }
    internal void CancelWake() { wakeArmed = false; wakeStarted = null; }

    internal FadeSample Sample(bool playing)
    {
        TimeSpan? next = null;
        float gain = 1;
        if (SleepRemaining is { } remaining)
        {
            if (remaining == TimeSpan.Zero)
            {
                CancelSleep();
                CancelWake();
                return new(0, true, null);
            }
            var fade = sleepDuration!.Value < SleepFade ? sleepDuration.Value : SleepFade;
            if (remaining <= fade)
            {
                gain = (float)(remaining / fade);
                next = remaining < FadeStep ? remaining : FadeStep;
            }
            else
                next = remaining - fade;
        }

        if (!wakeArmed) return new(gain, false, next);
        if (playing) wakeStarted ??= clock.GetTimestamp();
        if (wakeStarted is not long started) return new(0, false, next);
        var elapsed = clock.GetElapsedTime(started);
        if (elapsed >= WakeFade)
        {
            CancelWake();
            return new(gain, false, next);
        }
        var step = WakeFade - elapsed < FadeStep ? WakeFade - elapsed : FadeStep;
        return new(gain * (float)(elapsed / WakeFade), false, next is { } scheduled && scheduled < step ? scheduled : step);
    }

    internal static int RandomIndex(IReadOnlyList<Station> entries, Uri? current)
    {
        if (entries.Count < 2) return entries.Count - 1;
        for (int index = 0; index < entries.Count; index++)
        {
            if (entries[index].Url != current) continue;
            int other = Random.Shared.Next(entries.Count - 1);
            return other >= index ? other + 1 : other;
        }
        return Random.Shared.Next(entries.Count);
    }

    internal static DateTimeOffset NextAlarm(TimeOnly time, DateTimeOffset now, TimeZoneInfo zone)
    {
        DateOnly day = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(now, zone).DateTime);
        for (;; day = day.AddDays(1))
        {
            var local = day.ToDateTime(time, DateTimeKind.Unspecified);
            while (zone.IsInvalidTime(local)) local = local.AddMinutes(1);
            var offsets = zone.IsAmbiguousTime(local) ? zone.GetAmbiguousTimeOffsets(local) : [zone.GetUtcOffset(local)];
            var next = offsets.Select(offset => new DateTimeOffset(local, offset)).Where(candidate => candidate > now)
                .OrderBy(candidate => candidate).FirstOrDefault();
            if (next != default) return next;
        }
    }
}

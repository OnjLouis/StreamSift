using Android.Content;

namespace StreamSift.Mobile;

internal sealed class MobilePreferences(Context context)
{
    private const string SearchHistory = "SearchHistory";
    private const string UnderscoresKey = "ShowUnderscoresAsSpaces";
    private readonly ISharedPreferences data = context.GetSharedPreferences("stream_sift", FileCreationMode.Private)!;

    internal static int[] SkipIntervals { get; } = [5, 10, 30, 60];

    internal bool Sounds
    {
        get => data.GetBoolean("SearchSounds", false);
        set => data.Edit()!.PutBoolean("SearchSounds", value)!.Apply();
    }

    internal bool Haptics
    {
        get => data.GetBoolean("SearchHaptics", false);
        set => data.Edit()!.PutBoolean("SearchHaptics", value)!.Apply();
    }

    internal bool ResumeOnLaunch
    {
        get => data.GetBoolean("ResumePlaybackOnLaunch", false);
        set => data.Edit()!.PutBoolean("ResumePlaybackOnLaunch", value)!.Apply();
    }

    internal bool BufferNext
    {
        get => data.GetBoolean("BufferNextItem", true);
        set => data.Edit()!.PutBoolean("BufferNextItem", value)!.Apply();
    }

    internal bool UnderscoresAsSpaces
    {
        get => data.GetBoolean(UnderscoresKey, false);
        set => data.Edit()!.PutBoolean(UnderscoresKey, value)!.Apply();
    }

    internal string DisplayName(string original) => UnderscoresAsSpaces ? original.Replace('_', ' ') : original;

    internal bool RepeatFile
    {
        get => data.GetBoolean("RepeatCurrentFile", false);
        set => data.Edit()!.PutBoolean("RepeatCurrentFile", value)!.Apply();
    }

    internal bool AutoAdvance
    {
        get => data.GetBoolean("AutoAdvanceFiles", false);
        set => data.Edit()!.PutBoolean("AutoAdvanceFiles", value)!.Apply();
    }

    internal float Volume
    {
        get => Math.Clamp(data.GetFloat("PlaybackVolume", 1), 0, 1);
        set => data.Edit()!.PutFloat("PlaybackVolume", Math.Clamp(value, 0, 1))!.Apply();
    }

    internal int SkipSeconds
    {
        get
        {
            int value = data.GetInt("PlaybackSkipSeconds", 10);
            return SkipIntervals.Contains(value) ? value : 10;
        }
        set
        {
            if (SkipIntervals.Contains(value)) data.Edit()!.PutInt("PlaybackSkipSeconds", value)!.Apply();
        }
    }

    internal string[] History() => (data.GetString(SearchHistory, "") ?? "")
        .Split('\n', StringSplitOptions.RemoveEmptyEntries).Where(CanRemember).Take(10).ToArray();

    internal void ClearHistory() => data.Edit()!.Remove(SearchHistory)!.Apply();

    internal void Remember(string input)
    {
        input = input.Trim();
        if (!CanRemember(input)) return;
        var entries = History().Where(value => Key(value) != Key(input)).Prepend(input).Take(10);
        data.Edit()!.PutString(SearchHistory, string.Join('\n', entries))!.Apply();
    }

    internal static bool CanRemember(string input) => Key(input.Trim()) != null;

    private static string? Key(string value)
    {
        if (value.Length is 0 or > 1024 || value.Any(c => char.IsControl(c) || c is '?' or '#' or '\\')) return null;
        if (SearchSequence.CanSearchStation(value)) return "name:" + value.ToUpperInvariant();
        return PublicNetwork.TryInput(value, out var url) && url.Scheme == Uri.UriSchemeHttps
            ? "url:" + url.AbsoluteUri : null;
    }
}

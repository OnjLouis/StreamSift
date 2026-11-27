using Android.Content;
using AndroidX.Media3.Common;
using AndroidX.Media3.ExoPlayer;
using AndroidX.Media3.ExoPlayer.Source;
using AndroidX.Media3.Extractor.Metadata.Icy;

namespace StreamSift.Mobile;

internal sealed class LiveRadioPlayer : Java.Lang.Object, IPlayerListener
{
    private readonly IExoPlayer player;
    private readonly AndroidPlayback owner;
    private readonly CancellationTokenSource lifetime = new();
    private readonly PublicRadioSource.Factory sourceFactory;
    private readonly DefaultMediaSourceFactory mediaFactory;
    private bool hls;

    internal LiveRadioPlayer(Context context, AndroidPlayback owner, float volume)
    {
        this.owner = owner;
        sourceFactory = new PublicRadioSource.Factory(lifetime.Token);
        mediaFactory = new DefaultMediaSourceFactory(sourceFactory);
        player = new ExoPlayerBuilder(context).SetMediaSourceFactory(mediaFactory)!.Build()
            ?? throw new InvalidOperationException("Radio player could not initialize.");
        player.Volume = volume;
        player.SetWakeMode(C.WakeModeLocal);
        player.AddListener(this);
    }

    internal float Volume { set => player.Volume = value; }

    internal void Start(Station station)
    {
        var item = new MediaItem.Builder().SetUri(station.Url.AbsoluteUri)!;
        hls = station.IsHls;
        if (hls) item.SetMimeType(MimeTypes.ApplicationM3u8);
        player.SetMediaItem(item.Build()!);
        player.Prepare();
        player.Play();
    }

    internal void Release()
    {
        lifetime.Cancel();
        player.RemoveListener(this);
        player.Release();
        player.Dispose();
        mediaFactory.Dispose();
        sourceFactory.Dispose();
        lifetime.Dispose();
        Dispose();
    }

    public void OnIsPlayingChanged(bool isPlaying)
    {
        if (isPlaying) owner.RadioPlaying(this);
    }

    public void OnPlaybackStateChanged(int playbackState)
    {
        if (playbackState == BasePlayer.InterfaceConsts.StateBuffering) owner.RadioBuffering(this);
        if (playbackState == BasePlayer.InterfaceConsts.StateEnded) owner.RadioFailed(this);
    }

    public void OnPlayerError(PlaybackException? error) => owner.RadioFailed(this);

    public void OnMetadata(Metadata? metadata)
    {
        if (metadata == null) return;
        for (int index = 0; index < metadata.Length(); index++)
        {
            if (metadata.Get(index) is not IcyInfo info) continue;
            owner.RadioTitle(this, LiveAudioStream.CleanTitle(info.Title));
            return;
        }
    }

    public void OnMediaMetadataChanged(MediaMetadata? metadata)
    {
        if (!hls || metadata == null) return;
        var title = LiveAudioStream.CleanTitle(metadata.Title?.ToString());
        var artist = LiveAudioStream.CleanTitle(metadata.Artist?.ToString());
        owner.RadioTitle(this, title != null && artist != null ? $"{artist} - {title}" : title);
    }
}

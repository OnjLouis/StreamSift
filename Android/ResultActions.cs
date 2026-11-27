using Android.App;
using Android.Content;
using Android.OS;
using Android.Views;
using Android.Views.Accessibility;
using Android.Widget;
using OperationCanceledException = System.OperationCanceledException;

namespace StreamSift.Mobile;

public sealed partial class MainActivity
{
    private const int ImportFavouritesDocument = 602, ExportFavouritesDocument = 603;
    private string? pendingFile;
    private CancellationTokenSource? transfer;
    private ImageButton favouritesButton = null!;
    private System.Action? refreshFavourites;
    private static readonly (int Id, string Label)[] FileActions =
    [
        (Resource.Id.copy_result_url, "Copy URL"),
        (Resource.Id.share_result_url, "Share URL"),
        (Resource.Id.download_result, "Download")
    ];

    private (int Id, string Label)[] Actions(Station row)
    {
        var actions = row.IsTrack ? FileActions : FileActions[..2];
        return SessionStore.CanSave(row) ? [.. actions, (Resource.Id.favourite_result,
            playback.Store.IsFavourite(row) ? "Remove from Favourites" : "Add to Favourites")] : actions;
    }

    private void ShowFavourites()
    {
        Station[] entries = playback.Store.ReadFavourites();
        var content = PanelContent();
        var list = new ListView(this) { ContentDescription = "Favourites", ItemsCanFocus = true };
        var empty = Text("No favourites");
        content.AddView(empty);
        AlertDialog? dialog = null;
        var playRandom = Button("Play Random", () => playback.PlayRandom(entries,
            row => FocusPlayingRow(list, entries, row), fromFavourites: true));
        playRandom.Enabled = entries.Length > 0;
        content.AddView(playRandom);
        var import = Button("Import Favourites", () => ChooseFavouriteDocument(export: false));
        var export = Button("Export Favourites", () => ChooseFavouriteDocument(export: true));
        import.TooltipText = "Adds entries from an M3U8 playlist.";
        export.TooltipText = "Saves your favourites as an M3U8 playlist.";
        export.Enabled = entries.Length > 0;
        content.AddView(import);
        content.AddView(export);
        content.AddView(list, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, 0, 1));
        var items = new ResultAdapter(this, () => entries, index =>
        {
            playback.Select(entries, index, fromFavourites: true);
            dialog?.Dismiss();
        });
        list.Adapter = items;
        list.EmptyView = empty;
        dialog = OpenPanel("Favourites", content, favouritesButton, scroll: false);
        favouritesList = list;
        refreshFavourites = () =>
        {
            entries = playback.Store.ReadFavourites();
            playRandom.Enabled = entries.Length > 0;
            export.Enabled = entries.Length > 0;
            items.NotifyDataSetChanged();
        };
        dialog.DismissEvent += (_, _) =>
        {
            favouritesList = null;
            refreshFavourites = null;
        };
    }

    private void ShowResultActions(Station row)
    {
        var actions = Actions(row);
        new AlertDialog.Builder(this).SetTitle(preferences.DisplayName(row.Name))!
            .SetItems(actions.Select(action => action.Label).ToArray(), (_, args) => ResultAction(row, actions[args.Which].Id))!
            .SetNegativeButton("Cancel", (_, _) => { })!.Show();
    }

    private bool ResultAction(Station row, int action)
    {
        if (action == Resource.Id.favourite_result && SessionStore.CanSave(row))
        {
            bool add = !playback.Store.IsFavourite(row);
            bool saved = playback.Store.SetFavourite(row, add);
            if (saved) refreshFavourites?.Invoke();
            Toast.MakeText(this, saved ? add ? "Added to Favourites." : "Removed from Favourites."
                : "Could not update Favourites.", ToastLength.Short)?.Show();
            return true;
        }
        if (action == Resource.Id.copy_result_url)
        {
            var clipboard = GetSystemService(ClipboardService) as Android.Content.ClipboardManager;
            if (clipboard == null) return false;
            clipboard.PrimaryClip = ClipData.NewPlainText("Media URL", row.Url.AbsoluteUri)!;
            Toast.MakeText(this, "URL copied.", ToastLength.Short)?.Show();
            return true;
        }
        if (action == Resource.Id.share_result_url)
        {
            var share = new Intent(Intent.ActionSend).SetType("text/plain")!;
            share.PutExtra(Intent.ExtraText, row.Url.AbsoluteUri);
            StartActivity(Intent.CreateChooser(share, "Share URL"));
            return true;
        }
        if (action != Resource.Id.download_result || !row.IsTrack || pendingFile != null || transfer != null) return false;
        _ = Download(row);
        return true;
    }

    private async Task Download(Station row)
    {
        using var operation = new CancellationTokenSource(TimeSpan.FromMinutes(5));
        transfer = operation;
        var dialog = new AlertDialog.Builder(this).SetTitle("Downloading")!.SetMessage(preferences.DisplayName(row.Name))!
            .SetNegativeButton("Cancel Download", (_, _) => operation.Cancel())!.Create()!;
        dialog.CancelEvent += (_, _) => operation.Cancel();
        dialog.Show();
        string directory = Path.Combine(CacheDir!.AbsolutePath, "download-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(directory);
            pendingFile = await TrackDownload.SaveAsync(row, directory, operation.Token);
            operation.Token.ThrowIfCancellationRequested();
            dialog.Dismiss();
            var save = new Intent(Intent.ActionCreateDocument).SetType("application/octet-stream")!
                .AddCategory(Intent.CategoryOpenable)!;
            save.PutExtra(Intent.ExtraTitle, Path.GetFileName(pendingFile));
            StartActivityForResult(save, SaveDocument);
        }
        catch (OperationCanceledException) when (operation.IsCancellationRequested)
        {
            pendingFile = null;
            RemoveDownload(directory);
        }
        catch (Exception error) when (error is IOException or InvalidDataException or HttpRequestException or
            UnauthorizedAccessException or ArgumentException or Java.Lang.Exception)
        {
            pendingFile = null;
            RemoveDownload(directory);
            Announce(error is InvalidDataException
                ? "Download failed. " + error.Message
                : "Download failed. Check the connection and available storage, then try again.");
        }
        finally
        {
            transfer = null;
            dialog.Dismiss();
        }
    }

    protected override async void OnActivityResult(int requestCode, Result resultCode, Intent? data)
    {
        base.OnActivityResult(requestCode, resultCode, data);
        if (requestCode is ImportFavouritesDocument or ExportFavouritesDocument)
        {
            if (resultCode == Result.Ok && data?.Data is { } document)
                await TransferFavourites(document, requestCode == ExportFavouritesDocument);
            return;
        }
        if (requestCode != SaveDocument || pendingFile is not { } file) return;
        try
        {
            if (resultCode != Result.Ok || data?.Data is not { } uri) return;
            await using var target = ContentResolver!.OpenOutputStream(uri)
                ?? throw new IOException("The selected location is unavailable.");
            await using var source = File.OpenRead(file);
            await source.CopyToAsync(target);
            Toast.MakeText(this, "Download saved.", ToastLength.Short)?.Show();
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or Java.Lang.Exception)
        {
            Announce("Could not save the download to that location.");
        }
        finally
        {
            pendingFile = null;
            RemoveDownload(Path.GetDirectoryName(file)!);
        }
    }

    private void ChooseFavouriteDocument(bool export)
    {
        using var intent = new Intent(export ? Intent.ActionCreateDocument : Intent.ActionOpenDocument);
        intent.AddCategory(Intent.CategoryOpenable);
        // Match the provider's type for this extension so it preserves the filename.
        var playlistType = Android.Webkit.MimeTypeMap.Singleton?.GetMimeTypeFromExtension("m3u8") ?? "application/octet-stream";
        intent.SetType(export ? playlistType : "*/*");
        if (export) intent.PutExtra(Intent.ExtraTitle, "Favourites.m3u8");
        try { StartActivityForResult(intent, export ? ExportFavouritesDocument : ImportFavouritesDocument); }
        catch (ActivityNotFoundException) { ReportFavouriteTransfer("The file picker is unavailable."); }
    }

    private async Task TransferFavourites(Android.Net.Uri document, bool export)
    {
        try
        {
            if (export)
            {
                byte[] playlist = FavouritePlaylist.Export(playback.Store.ReadFavourites(), httpsOnly: true);
                await using (var output = ContentResolver!.OpenOutputStream(document, "wt") ?? throw new IOException())
                    await output.WriteAsync(playlist);
                ReportFavouriteTransfer("Favourites exported.");
                return;
            }

            await using var input = ContentResolver!.OpenInputStream(document) ?? throw new IOException();
            var list = await FavouritePlaylist.ReadAsync(input, httpsOnly: true);
            int added = playback.Store.ImportFavourites(list.Stations);
            refreshFavourites?.Invoke();
            ReportFavouriteTransfer(FavouritePlaylist.ImportSummary(list, added));
        }
        catch (InvalidDataException error) { ReportFavouriteTransfer(error.Message); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or Java.Lang.Exception)
        {
            ReportFavouriteTransfer(export ? "Could not export Favourites." : "Could not import Favourites.");
        }
    }

    private void ReportFavouriteTransfer(string message) =>
        Toast.MakeText(this, message, ToastLength.Long)?.Show();

    private static void RemoveDownload(string directory)
    {
        try { Directory.Delete(directory, recursive: true); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
    }

    private sealed class ResultAdapter(MainActivity activity, Func<Station[]> entries, Action<int> select,
        Action<int>? remember = null) : BaseAdapter<Station>
    {
        public override int Count => entries().Length;
        public override Station this[int position] => entries()[position];
        public override long GetItemId(int position) => position;
        public override View GetView(int position, View? convertView, ViewGroup? parent)
        {
            var view = convertView as ResultRow ?? new ResultRow(activity,
                (row, index) =>
                {
                    var current = entries();
                    if (index < current.Length && current[index] == row) select(index);
                }, remember);
            view.Bind(this[position], position);
            return view;
        }
    }

    private sealed class ResultRow : LinearLayout
    {
        private readonly MainActivity activity;
        private readonly Action<int>? remember;
        private readonly TextView name;
        private readonly TextView info;
        private Station? item;
        private int index;
        private bool markedPlaying;

        internal ResultRow(MainActivity activity, Action<Station, int> select, Action<int>? remember) : base(activity)
        {
            this.activity = activity;
            this.remember = remember;
            Orientation = Orientation.Vertical;
            Clickable = Focusable = LongClickable = true;
            ImportantForAccessibility = ImportantForAccessibility.Yes;
            SetPadding(activity.Dp(8), activity.Dp(9), activity.Dp(8), activity.Dp(9));
            name = activity.Text("");
            info = activity.Text("", 14);
            name.ImportantForAccessibility = info.ImportantForAccessibility = ImportantForAccessibility.No;
            AddView(name);
            AddView(info);
            Click += (_, _) =>
            {
                if (item != null) select(item, index);
            };
            LongClick += (_, args) =>
            {
                if (item == null) return;
                args.Handled = true;
                activity.ShowResultActions(item);
            };
        }

        internal bool Matches(Uri url) => item?.Url == url;

        internal void Bind(Station station, int position)
        {
            item = station;
            index = position;
            markedPlaying = activity.playingResult == (station.Url, station.IsTrack);
            string details = string.Join(", ", new string?[]
            {
                station.IsTrack ? "Track" : "Live stream",
                station.Format,
                station.Bitrate is int bitrate ? $"{bitrate} kbps" : null,
                station.Listeners is int listeners ? ResultView.ListenerLabel(listeners) : null,
                station.FileSizeLabel is string size ? $"Size {size}" : null,
                station.DateModifiedLabel is string date ? $"Date modified {date}" : null,
                station.ScanTitle is string title ? $"Scan title: {title}" : null
            }.OfType<string>());
            info.Text = details;
            UpdateTitle(station);
        }

        internal void RefreshPlaying()
        {
            if (item is not { } station) return;
            bool playing = activity.playingResult == (station.Url, station.IsTrack);
            if (playing == markedPlaying) return;
            markedPlaying = playing;
            UpdateTitle(station);
        }

        private void UpdateTitle(Station station)
        {
            string nameText = activity.preferences.DisplayName(station.Name);
            string title = markedPlaying ? $"Playing {nameText}" : nameText;
            name.Text = title;
            ContentDescription = $"{title}, {info.Text}";
        }

        public override void OnInitializeAccessibilityNodeInfo(AccessibilityNodeInfo? node)
        {
            base.OnInitializeAccessibilityNodeInfo(node);
            if (node == null || item == null) return;
            node.ClassName = "android.widget.Button";
            foreach (var action in activity.Actions(item))
                node.AddAction(new AccessibilityNodeInfo.AccessibilityAction(action.Id,
                    new Java.Lang.String(action.Label)));
        }

        public override bool PerformAccessibilityAction(Android.Views.Accessibility.Action action, Bundle? arguments)
        {
            if (item is { } station && activity.ResultAction(station, (int)action))
            {
                if ((int)action == Resource.Id.favourite_result) SendAccessibilityEvent(EventTypes.WindowContentChanged);
                return true;
            }
            bool handled = base.PerformAccessibilityAction(action, arguments);
            if (handled && (int)action == AccessibilityFocus) remember?.Invoke(index);
            return handled;
        }
    }
}

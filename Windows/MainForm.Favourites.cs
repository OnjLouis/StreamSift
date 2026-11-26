namespace StreamSift;

internal sealed partial class MainForm
{
    private const string FavouriteFilter = "M3U8 playlists (*.m3u8)|*.m3u8|M3U playlists (*.m3u)|*.m3u";
    private readonly FavouriteStore favourites;
    private ToolStripMenuItem showFavouritesCommand = null!, toggleFavouriteCommand = null!, randomCommand = null!;
    private ToolStripMenuItem importFavouritesCommand = null!, exportFavouritesCommand = null!;
    private bool showingFavourites;
    private ResultView favouriteView;
    private Uri? searchSelection, favouriteSelection, lastPlayed;
    private string searchStatus = "Ready.";
    private string searchResultQuery = "", favouriteResultQuery = "";

    private Station[] ActiveResults => showingFavourites ? favourites.Items : searchResults;
    private ResultView ActiveView => showingFavourites ? favouriteView : resultView;
    private bool FileColumns => showingFavourites ? favourites.Items.Any(row => row.IsTrack) : fileIndexResults;
    private Uri? FocusedResult => (results.FocusedItem?.Tag as Station)?.Url ?? Selected?.Url;

    private ToolStripMenuItem BuildFavouritesMenu()
    {
        var menu = new ToolStripMenuItem("Favour&ites");
        showFavouritesCommand = MenuCommand.Create("Show Favourites", SwitchFavourites, Keys.Control | Keys.Shift | Keys.B);
        toggleFavouriteCommand = MenuCommand.Create("Add to Favourites", ToggleFavourite, Keys.Control | Keys.D);
        importFavouritesCommand = MenuCommand.Create("Import Favourites...", () => _ = ImportFavourites(),
            Keys.Control | Keys.Shift | Keys.I);
        exportFavouritesCommand = MenuCommand.Create("Export Favourites...", ExportFavourites, Keys.Control | Keys.Shift | Keys.E);
        menu.DropDownItems.AddRange([showFavouritesCommand, toggleFavouriteCommand,
            new ToolStripSeparator(), importFavouritesCommand, exportFavouritesCommand]);
        return menu;
    }

    private void UpdateFavouritesCommands(Station? selected)
    {
        bool idle = scan == null && updateCheck == null;
        showFavouritesCommand.Enabled = importFavouritesCommand.Enabled = idle;
        exportFavouritesCommand.Enabled = idle && favourites.Items.Length > 0;
        randomCommand.Enabled = idle && visibleResults.Length > 0;
        toggleFavouriteCommand.Enabled = idle && selected != null;
        toggleFavouriteCommand.Text = selected != null && favourites.Contains(selected)
            ? "Remove from Favourites" : "Add to Favourites";
        results.FavouriteCaption = toggleFavouriteCommand.Enabled
            ? toggleFavouriteCommand.Text.Replace("Favourites", "&Favourites", StringComparison.Ordinal) : null;
    }

    private void SwitchFavourites()
    {
        if (scan != null) return;
        if (showingFavourites)
        {
            favouriteSelection = FocusedResult;
            favouriteResultQuery = resultQuery;
        }
        else
        {
            searchSelection = FocusedResult;
            searchResultQuery = resultQuery;
            searchStatus = status.Text;
        }
        showingFavourites = !showingFavourites;
        resultQuery = showingFavourites ? favouriteResultQuery : searchResultQuery;
        streamsLabel.Text = showingFavourites ? "Favourite&s:" : "Re&sults:";
        results.AccessibleName = showingFavourites ? "Favourites" : "Results";
        results.AccessibleDescription = showingFavourites ? "Press Escape or Ctrl+Shift+B to return to search results." : null;
        streamsCommand.Text = showingFavourites ? "Go to Favourites" : "Go to Results";
        showFavouritesCommand.Text = showingFavourites ? "Return to Search Results" : "Show Favourites";
        RenderResults(preserveSelection: false, restoredSelection: showingFavourites ? favouriteSelection : searchSelection);
        SetStatus(showingFavourites ? (favourites.Items.Length == 0 ? "No favourites." :
            $"Showing {visibleResults.Length} of {favourites.Items.Length} favourites.") +
            " Press Escape to return to search results." : searchStatus);
        if (visibleResults.Length > 0) results.Focus();
        else status.Focus();
    }

    private void ToggleFavourite()
    {
        if (scan != null || results.SelectedItems.Count != 1 || Selected is not { } row) return;
        if (!FavouritePlaylist.CanSave(row))
        {
            ShowText("Add to Favourites", "Choose a station or track with a stable public audio address.");
            return;
        }
        SafeAction(() =>
        {
            bool added = favourites.Toggle(row);
            RefreshFavourites();
            SetStatus(added ? "Added to Favourites." : "Removed from Favourites.");
        });
    }

    private void RefreshFavourites()
    {
        favouriteView = favouriteView.RetainAvailable(favourites.Items);
        if (showingFavourites)
        {
            bool hadFocus = results.Focused || details.Focused;
            RenderResults(preserveSelection: true);
            if (hadFocus && visibleResults.Length == 0) status.Focus();
        }
        else UpdateSelection();
    }

    private void PlayRandom()
    {
        if (scan != null || visibleResults.Length == 0) return;
        var choices = visibleResults.Where(row => row.Url != lastPlayed).ToArray();
        if (choices.Length == 0) choices = visibleResults;
        Play([choices[Random.Shared.Next(choices.Length)]], random: true);
    }

    private async Task ImportFavourites()
    {
        using var dialog = new OpenFileDialog { Title = "Import Favourites", Filter = FavouriteFilter };
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        try
        {
            await using var file = File.OpenRead(dialog.FileName);
            var list = await FavouritePlaylist.ReadAsync(file);
            if (closing || IsDisposed) return;
            int added = favourites.Merge(list.Stations);
            if (added > 0) RefreshFavourites();
            SetStatus(FavouritePlaylist.ImportSummary(list, added));
        }
        catch (InvalidDataException error) { if (!closing) ShowText("Import Favourites", error.Message); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            if (!closing) ShowText("Import Favourites", "Could not read the playlist or save Favourites.");
        }
    }

    private void ExportFavourites() => SafeAction(() =>
    {
        using var dialog = new SaveFileDialog
        {
            Title = "Export Favourites", Filter = FavouriteFilter, DefaultExt = "m3u8", FileName = "Favourites.m3u8"
        };
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        FavouriteStore.WriteFile(dialog.FileName, FavouritePlaylist.Export(favourites.Items));
        SetStatus("Favourites exported.");
    });
}

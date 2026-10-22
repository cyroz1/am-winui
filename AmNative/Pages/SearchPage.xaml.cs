using System.Collections.ObjectModel;
using AmNative.Models;
using AmNative.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace AmNative.Pages;

public sealed partial class SearchPage : Page
{
    private readonly ObservableCollection<MusicResource> _items = new();
    private readonly List<MusicResource> _loadedItems = new();
    private readonly List<Uri> _nextPages = new();
    private MusicResource? _selected;
    private bool _loading;
    private bool _showingChart;
    private bool _updatingGenreOptions;
    private bool _updatingTypeOptions;
    private string? _selectedGenre;
    private string? _selectedResourceType;
    private string _sortMode = "apple";
    private bool _resolvingPlayback;
    private bool _addingToLibrary;
    private bool _addingToFavorites;
    private CancellationTokenSource? _searchHintsCts;
    private string _chartLabel = "Top songs";
    private IReadOnlyList<MusicResource> SelectedLibraryItems => ResultsList.SelectedItems
        .OfType<MusicResource>()
        .Where(static item => item.CanBeAddedToLibrary)
        .DistinctBy(static item => (item.Type, item.Id))
        .ToArray();
    private IReadOnlyList<MusicResource> SelectedFavoriteItems => ResultsList.SelectedItems
        .OfType<MusicResource>()
        .Where(static item => item.CanBeFavorited && item.IsFavorited != true)
        .DistinctBy(static item => (item.Type, item.Id))
        .ToArray();
    private IReadOnlyList<MusicResource> SelectedPlaylistItems => ResultsList.SelectedItems
        .OfType<MusicResource>()
        .Where(static item => item.CanBeAddedToPlaylist)
        .ToArray();

    public SearchPage()
    {
        InitializeComponent();
        ResultsList.ItemsSource = _items;
        Unloaded += (_, _) => _searchHintsCts?.Cancel();
    }

    public void FocusSearchBox() => QueryBox.Focus(FocusState.Programmatic);

    private async void OnSearchClicked(object sender, RoutedEventArgs e) => await SearchAsync();

    private async void OnBrowseChartClicked(object sender, RoutedEventArgs e) => await LoadChartAsync();

    private async void OnLoadMoreClicked(object sender, RoutedEventArgs e) => await LoadMoreAsync();

    private void OnSortChanged(object sender, SelectionChangedEventArgs e)
    {
        if (SortBox.SelectedItem is not ComboBoxItem { Tag: string sortMode })
        {
            return;
        }

        _sortMode = sortMode;
        if (!IsLoaded)
        {
            return;
        }

        ResultsList.SelectedItems.Clear();
        _selected = null;
        ActionStatus.Text = "";
        RefreshVisibleItems();
        UpdateResultStatus();
        UpdateActions();
    }

    private void OnGenreChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_updatingGenreOptions)
        {
            return;
        }

        var selectedGenre = (GenreFilterBox.SelectedItem as ComboBoxItem)?.Tag as string;
        _selectedGenre = string.IsNullOrWhiteSpace(selectedGenre) ? null : selectedGenre;
        ResultsList.SelectedItems.Clear();
        _selected = null;
        ActionStatus.Text = "";
        RefreshVisibleItems();
        UpdateResultStatus();
        UpdateActions();
    }

    private void OnTypeChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_updatingTypeOptions)
        {
            return;
        }

        var selectedType = (TypeFilterBox.SelectedItem as ComboBoxItem)?.Tag as string;
        _selectedResourceType = string.IsNullOrWhiteSpace(selectedType) ? null : selectedType;
        ResultsList.SelectedItems.Clear();
        _selected = null;
        ActionStatus.Text = "";
        RefreshVisibleItems();
        UpdateResultStatus();
        UpdateActions();
    }

    private async void OnQueryTextChanged(AutoSuggestBox sender, AutoSuggestBoxTextChangedEventArgs args)
    {
        if (args.Reason != AutoSuggestionBoxTextChangeReason.UserInput || _loading)
        {
            return;
        }

        var term = sender.Text.Trim();
        _searchHintsCts?.Cancel();
        if (term.Length < 2)
        {
            sender.ItemsSource = Array.Empty<string>();
            sender.IsSuggestionListOpen = false;
            _searchHintsCts = null;
            return;
        }

        var cancellation = new CancellationTokenSource();
        _searchHintsCts = cancellation;
        try
        {
            await Task.Delay(TimeSpan.FromMilliseconds(250), cancellation.Token);
            var hints = await App.MusicApi.GetCatalogSearchHintsAsync(term, cancellation.Token);
            if (cancellation.IsCancellationRequested ||
                !ReferenceEquals(_searchHintsCts, cancellation) ||
                !string.Equals(term, sender.Text.Trim(), StringComparison.Ordinal))
            {
                return;
            }

            sender.ItemsSource = hints;
            sender.IsSuggestionListOpen = hints.Count > 0;
        }
        catch (OperationCanceledException)
        {
        }
        catch
        {
            if (ReferenceEquals(_searchHintsCts, cancellation))
            {
                sender.ItemsSource = Array.Empty<string>();
                sender.IsSuggestionListOpen = false;
            }
        }
        finally
        {
            if (ReferenceEquals(_searchHintsCts, cancellation))
            {
                _searchHintsCts = null;
            }

            cancellation.Dispose();
        }
    }

    private void OnSuggestionChosen(AutoSuggestBox sender, AutoSuggestBoxSuggestionChosenEventArgs args)
    {
        if (args.SelectedItem is string term)
        {
            sender.Text = term;
        }

        sender.IsSuggestionListOpen = false;
    }

    private async void OnQuerySubmitted(AutoSuggestBox sender, AutoSuggestBoxQuerySubmittedEventArgs args)
    {
        if (args.ChosenSuggestion is string term)
        {
            sender.Text = term;
        }

        await SearchAsync();
    }

    private async Task SearchAsync()
    {
        var term = QueryBox.Text.Trim();
        if (term.Length == 0)
        {
            return;
        }

        if (_loading)
        {
            return;
        }

        _searchHintsCts?.Cancel();
        QueryBox.IsSuggestionListOpen = false;
        _loading = true;
        _showingChart = false;
        _nextPages.Clear();
        _loadedItems.Clear();
        UpdateGenreFilterOptions();
        UpdateResourceTypeOptions();
        SetBusy(true);
        _items.Clear();
        _selected = null;
        UpdateActions();
        ActionStatus.Text = "";
        try
        {
            var page = await App.MusicApi.SearchCatalogPageAsync(term);
            _loadedItems.AddRange(page.Items);
            UpdateGenreFilterOptions();
            UpdateResourceTypeOptions();
            RefreshVisibleItems();
            _nextPages.AddRange(page.NextPages);
            UpdateResultStatus();
        }
        catch (Exception exception)
        {
            StatusText.Text = exception.Message;
        }
        finally
        {
            _loading = false;
            SetBusy(false);
        }
    }

    private async Task LoadChartAsync()
    {
        if (_loading)
        {
            return;
        }

        var selected = ChartTypeBox.SelectedItem as ComboBoxItem;
        var chartType = selected?.Tag as string ?? "songs";
        _chartLabel = selected?.Content?.ToString() ?? "Top songs";
        _loading = true;
        _showingChart = true;
        _nextPages.Clear();
        _loadedItems.Clear();
        UpdateGenreFilterOptions();
        UpdateResourceTypeOptions();
        SetBusy(true);
        _items.Clear();
        _selected = null;
        UpdateActions();
        ActionStatus.Text = "";
        StatusText.Text = $"Loading {_chartLabel.ToLowerInvariant()}…";
        try
        {
            var page = await App.MusicApi.GetCatalogChartsPageAsync(chartType);
            _loadedItems.AddRange(page.Items);
            UpdateGenreFilterOptions();
            UpdateResourceTypeOptions();
            RefreshVisibleItems();
            _nextPages.AddRange(page.NextPages);
            UpdateResultStatus();
        }
        catch (Exception exception)
        {
            StatusText.Text = exception.Message;
        }
        finally
        {
            _loading = false;
            SetBusy(false);
        }
    }

    private async Task LoadMoreAsync()
    {
        if (_loading || _nextPages.Count == 0)
        {
            return;
        }

        _loading = true;
        SetBusy(true);
        StatusText.Text = _showingChart ? $"Loading more {_chartLabel.ToLowerInvariant()}…" : "Loading more results…";
        try
        {
            var pages = new List<MusicResourcePage>();
            foreach (var nextPage in _nextPages)
            {
                pages.Add(await App.MusicApi.GetNextPageAsync(nextPage));
            }

            _nextPages.Clear();
            var addedItems = new List<MusicResource>();
            foreach (var page in pages)
            {
                addedItems.AddRange(page.Items);
                _nextPages.AddRange(page.NextPages);
            }

            _loadedItems.AddRange(addedItems);
            UpdateGenreFilterOptions();
            UpdateResourceTypeOptions();
            RefreshVisibleItems();
            UpdateResultStatus();
        }
        catch (Exception exception)
        {
            StatusText.Text = exception.Message;
        }
        finally
        {
            _loading = false;
            SetBusy(false);
        }
    }

    private void SetBusy(bool busy)
    {
        QueryBox.IsEnabled = !busy;
        SearchButton.IsEnabled = !busy;
        ChartTypeBox.IsEnabled = !busy;
        BrowseChartButton.IsEnabled = !busy;
        GenreFilterBox.IsEnabled = !busy && GenreFilterBox.Items.Count > 1;
        TypeFilterBox.IsEnabled = !busy && TypeFilterBox.Items.Count > 1;
        SortBox.IsEnabled = !busy;
        LoadMoreButton.IsEnabled = !busy && _nextPages.Count > 0;
        LoadMoreButton.Visibility = _nextPages.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        StatusText.Text = busy ? "Searching…" : StatusText.Text;
    }

    private void UpdateGenreFilterOptions()
    {
        var previousGenre = _selectedGenre;
        var genres = _loadedItems
            .SelectMany(static item => item.GenreNames)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(static genre => genre, StringComparer.CurrentCultureIgnoreCase)
            .ToArray();

        _updatingGenreOptions = true;
        GenreFilterBox.Items.Clear();
        GenreFilterBox.Items.Add(new ComboBoxItem { Content = "All genres", Tag = "" });
        foreach (var genre in genres)
        {
            GenreFilterBox.Items.Add(new ComboBoxItem { Content = genre, Tag = genre });
        }

        var previousGenreIndex = previousGenre is null
            ? -1
            : Array.FindIndex(
                genres,
                genre => string.Equals(genre, previousGenre, StringComparison.OrdinalIgnoreCase));
        GenreFilterBox.SelectedIndex = previousGenreIndex + 1;
        _selectedGenre = (GenreFilterBox.SelectedItem as ComboBoxItem)?.Tag as string;
        if (string.IsNullOrEmpty(_selectedGenre))
        {
            _selectedGenre = null;
        }

        _updatingGenreOptions = false;
    }

    private void UpdateResourceTypeOptions()
    {
        var previousType = _selectedResourceType;
        var types = _loadedItems
            .Select(static item => item.Type)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(GetResourceTypeLabel, StringComparer.CurrentCultureIgnoreCase)
            .ToArray();

        _updatingTypeOptions = true;
        TypeFilterBox.Items.Clear();
        TypeFilterBox.Items.Add(new ComboBoxItem { Content = "All types", Tag = "" });
        foreach (var type in types)
        {
            TypeFilterBox.Items.Add(new ComboBoxItem { Content = GetResourceTypeLabel(type), Tag = type });
        }

        var previousTypeIndex = previousType is null
            ? -1
            : Array.FindIndex(types, type => string.Equals(type, previousType, StringComparison.Ordinal));
        TypeFilterBox.SelectedIndex = previousTypeIndex + 1;
        var selectedType = (TypeFilterBox.SelectedItem as ComboBoxItem)?.Tag as string;
        _selectedResourceType = string.IsNullOrWhiteSpace(selectedType) ? null : selectedType;
        _updatingTypeOptions = false;
    }

    private static string GetResourceTypeLabel(string type) => type switch
    {
        "songs" => "Songs",
        "albums" => "Albums",
        "artists" => "Artists",
        "playlists" => "Playlists",
        "music-videos" => "Music videos",
        "stations" => "Radio stations",
        "activities" => "Activities",
        "apple-curators" => "Apple curators",
        "curators" => "Curators",
        "record-labels" => "Record labels",
        _ => type
    };

    private void RefreshVisibleItems()
    {
        _items.Clear();
        AddVisibleItems(_loadedItems);
    }

    private void AddVisibleItems(IEnumerable<MusicResource> items)
    {
        var visibleItems = items.Where(item =>
            (_selectedResourceType is null || string.Equals(item.Type, _selectedResourceType, StringComparison.Ordinal)) &&
            (_selectedGenre is null || item.GenreNames.Contains(_selectedGenre, StringComparer.OrdinalIgnoreCase)));
        visibleItems = _sortMode switch
        {
            "title" => visibleItems.OrderBy(static item => item.Name, StringComparer.CurrentCultureIgnoreCase),
            "artist" => visibleItems
                .OrderBy(static item => string.IsNullOrWhiteSpace(item.ArtistName))
                .ThenBy(static item => item.ArtistName, StringComparer.CurrentCultureIgnoreCase),
            "album" => visibleItems
                .OrderBy(static item => string.IsNullOrWhiteSpace(item.AlbumName))
                .ThenBy(static item => item.AlbumName, StringComparer.CurrentCultureIgnoreCase),
            "release-date-ascending" => visibleItems
                .OrderBy(static item => string.IsNullOrWhiteSpace(item.ReleaseDate))
                .ThenBy(static item => item.ReleaseDate, StringComparer.Ordinal)
                .ThenBy(static item => item.Name, StringComparer.CurrentCultureIgnoreCase),
            "release-date-descending" => visibleItems
                .OrderBy(static item => string.IsNullOrWhiteSpace(item.ReleaseDate))
                .ThenByDescending(static item => item.ReleaseDate, StringComparer.Ordinal)
                .ThenBy(static item => item.Name, StringComparer.CurrentCultureIgnoreCase),
            "duration-ascending" => visibleItems
                .OrderBy(static item => item.DurationInMillis is null)
                .ThenBy(static item => item.DurationInMillis ?? int.MaxValue),
            "duration-descending" => visibleItems
                .OrderBy(static item => item.DurationInMillis is null)
                .ThenByDescending(static item => item.DurationInMillis ?? int.MinValue),
            _ => visibleItems
        };

        foreach (var item in visibleItems)
        {
            _items.Add(item);
        }
    }

    private void UpdateResultStatus()
    {
        var genreSuffix = _selectedGenre is null ? "" : $" · {_selectedGenre}";
        var typeSuffix = _selectedResourceType is null
            ? ""
            : $" · {(TypeFilterBox.SelectedItem as ComboBoxItem)?.Content}";
        var sortSuffix = _sortMode == "apple"
            ? ""
            : $" · Sorted by {(SortBox.SelectedItem as ComboBoxItem)?.Content}";
        if (_sortMode is "release-date-ascending" or "release-date-descending")
        {
            var missingReleaseDateCount = _loadedItems.Count(static item => string.IsNullOrWhiteSpace(item.ReleaseDate));
            if (missingReleaseDateCount > 0)
            {
                sortSuffix += $" · Apple did not report a release date for {missingReleaseDateCount} loaded items";
            }
        }

        if (_showingChart)
        {
            StatusText.Text = _loadedItems.Count == 0
                ? $"No {_chartLabel.ToLowerInvariant()} were returned."
                : $"{_items.Count} {_chartLabel.ToLowerInvariant()}{genreSuffix}{typeSuffix}{sortSuffix}";
        }
        else
        {
            StatusText.Text = _loadedItems.Count == 0
                ? "No results found."
                : $"{_items.Count} results{genreSuffix}{typeSuffix}{sortSuffix}";
        }
    }

    private void OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        _selected = e.AddedItems.OfType<MusicResource>().LastOrDefault()
            ?? ResultsList.SelectedItems.OfType<MusicResource>().LastOrDefault();
        ActionStatus.Text = "";
        UpdateActions();
    }

    private void OnOpenClicked(object sender, RoutedEventArgs e)
    {
        if (_selected is not null && HasRelatedItems(_selected))
        {
            Frame.Navigate(typeof(ResourcePage), _selected);
        }
    }

    private async void OnOpenAppleMusicClicked(object sender, RoutedEventArgs e)
    {
        try
        {
            ActionStatus.Text = await AppleMusicLink.LaunchAsync(_selected, App.MusicApi)
                ? "Opened the Apple Music link."
                : "Windows could not open this Apple Music link.";
        }
        catch
        {
            ActionStatus.Text = "Windows could not open this Apple Music link.";
        }
    }

    private async void OnPlayClicked(object sender, RoutedEventArgs e)
    {
        var items = SelectedPlaylistItems.Where(static item => item.CanStartPlayback).ToArray();
        if (items.Length == 0 || _resolvingPlayback)
        {
            return;
        }

        var startAt = _selected;
        _resolvingPlayback = true;
        UpdateActions();
        ActionStatus.Text = "Preparing Apple Music playback…";
        try
        {
            var playableItems = await App.ResolvePlaybackItemsAsync(items);
            await App.Player.PlayQueueAsync(playableItems, startAt);
            ActionStatus.Text = playableItems.Count > 1
                ? "Playing the selected Apple Music tracks."
                : "Playing the selected Apple Music track.";
        }
        catch (Exception exception)
        {
            ActionStatus.Text = exception.Message;
        }
        finally
        {
            _resolvingPlayback = false;
            UpdateActions();
        }
    }

    private async void OnQueueClicked(object sender, RoutedEventArgs e)
    {
        var items = SelectedPlaylistItems.Where(static item => item.CanStartPlayback).ToArray();
        if (items.Length == 0 || _resolvingPlayback)
        {
            return;
        }

        _resolvingPlayback = true;
        UpdateActions();
        ActionStatus.Text = "Preparing tracks to add to the queue…";
        try
        {
            var count = await App.EnqueuePlaybackItemsAsync(items);
            ActionStatus.Text = count == 1 ? "Added one track to the queue." : $"Added {count} tracks to the queue.";
        }
        catch (Exception exception)
        {
            ActionStatus.Text = exception.Message;
        }
        finally
        {
            _resolvingPlayback = false;
            UpdateActions();
        }
    }

    private async void OnAddClicked(object sender, RoutedEventArgs e)
    {
        var items = SelectedLibraryItems;
        if (items.Count == 0 || _addingToLibrary)
        {
            return;
        }

        _addingToLibrary = true;
        UpdateActions();
        try
        {
            await App.MusicApi.AddToLibraryAsync(items);
            ActionStatus.Text = items.Count == 1
                ? $"Sent “{items[0].Name}” to your Apple Music library."
                : $"Sent {items.Count} items to your Apple Music library.";
        }
        catch (Exception exception)
        {
            ActionStatus.Text = exception.Message;
        }
        finally
        {
            _addingToLibrary = false;
            UpdateActions();
        }
    }

    private void OnAddToPlaylistClicked(object sender, RoutedEventArgs e)
    {
        var items = SelectedPlaylistItems;
        if (items.Count > 0)
        {
            Frame.Navigate(typeof(PlaylistsPage), items.ToArray());
        }
    }

    private async void OnFavoriteClicked(object sender, RoutedEventArgs e)
    {
        var items = SelectedFavoriteItems;
        if (items.Count == 0 || _addingToFavorites)
        {
            return;
        }

        _addingToFavorites = true;
        UpdateActions();
        try
        {
            await App.MusicApi.AddToFavoritesAsync(items);
            foreach (var item in items)
            {
                item.IsFavorited = true;
            }

            ActionStatus.Text = items.Count == 1
                ? $"Requested “{items[0].Name}” be added to favorites."
                : $"Requested {items.Count} items be added to favorites.";
        }
        catch (Exception exception)
        {
            ActionStatus.Text = exception.Message;
        }
        finally
        {
            _addingToFavorites = false;
            UpdateActions();
        }
    }

    private async void OnLikeClicked(object sender, RoutedEventArgs e) => await RateAsync(1, "Marked as liked.");

    private async void OnDislikeClicked(object sender, RoutedEventArgs e) => await RateAsync(-1, "Marked as disliked.");

    private async void OnClearRatingClicked(object sender, RoutedEventArgs e) => await RateAsync(null, "Rating cleared.");

    private async Task RateAsync(int? value, string successMessage)
    {
        if (_selected is null)
        {
            return;
        }

        await RunActionAsync(item => App.MusicApi.SetRatingAsync(item, value), successMessage);
    }

    private async Task RunActionAsync(Func<MusicResource, Task> action, string successMessage)
    {
        if (_selected is null)
        {
            return;
        }

        try
        {
            await action(_selected);
            ActionStatus.Text = successMessage;
        }
        catch (Exception exception)
        {
            ActionStatus.Text = exception.Message;
        }
    }

    private void UpdateActions()
    {
        OpenButton.IsEnabled = _selected is not null && HasRelatedItems(_selected);
        OpenAppleMusicButton.IsEnabled = !_loading && AppleMusicLink.CanOpen(_selected);
        var playableCount = SelectedPlaylistItems.Count(static item => item.CanStartPlayback);
        PlayButton.Content = playableCount > 1 ? $"Play {playableCount} tracks" : "Play";
        PlayButton.IsEnabled = !_loading && !_resolvingPlayback && playableCount > 0;
        QueueButton.Content = playableCount > 1 ? $"Add {playableCount} to queue" : "Add to queue";
        QueueButton.IsEnabled = !_loading && !_resolvingPlayback && playableCount > 0;
        var libraryItemCount = SelectedLibraryItems.Count;
        AddButton.Content = libraryItemCount > 1 ? $"Add {libraryItemCount} items to library" : "Add to library";
        AddButton.IsEnabled = !_loading && !_addingToLibrary && libraryItemCount > 0;
        var playlistItemCount = SelectedPlaylistItems.Count;
        PlaylistButton.Content = playlistItemCount > 1
            ? $"Add {playlistItemCount} items to playlist"
            : "Add to playlist";
        PlaylistButton.IsEnabled = playlistItemCount > 0;
        var favoriteItemCount = SelectedFavoriteItems.Count;
        FavoriteButton.Content = favoriteItemCount switch
        {
            > 1 => $"Add {favoriteItemCount} to favorites",
            1 => "Add to favorites",
            _ when _selected?.IsFavorited == true => "Already favorited",
            _ => "Add to favorites"
        };
        FavoriteButton.IsEnabled = !_loading && !_addingToFavorites && favoriteItemCount > 0;
        var supportsRating = _selected?.CanBeRated == true;
        LikeButton.IsEnabled = supportsRating;
        DislikeButton.IsEnabled = supportsRating;
        ClearRatingButton.IsEnabled = supportsRating;
    }

    private static bool HasRelatedItems(MusicResource item) => item.Type is
        "albums" or "album" or "artists" or "artist" or "playlists" or "playlist" or "songs" or "song" or
        "music-videos" or "music-video" or "activities" or "apple-curators" or "curators" or "record-labels";
}

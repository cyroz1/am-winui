using System.Collections.ObjectModel;
using AmNative.Models;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;

namespace AmNative.Pages;

public sealed partial class LibraryPage : Page
{
    private readonly ObservableCollection<MusicResource> _items = new();
    private readonly List<MusicResource> _loadedItems = new();
    private readonly List<Uri> _nextPages = new();
    private MusicResource? _selected;
    private bool _loading;
    private bool _isSearching;
    private bool _showFavoritesOnly;
    private bool _updatingGenreOptions;
    private string? _selectedGenre;
    private string _sortMode = "apple";
    private bool _resolvingPlayback;
    private bool _addingToFavorites;
    private bool _ratingLoading;
    private bool _ratingUpdating;
    private int? _selectedRating;
    private CancellationTokenSource? _ratingLookupCancellation;
    private IReadOnlyList<MusicResource> SelectedFavoriteItems => LibraryList.SelectedItems
        .OfType<MusicResource>()
        .Where(static item => item.CanBeFavorited && item.IsFavorited != true)
        .DistinctBy(static item => (item.Type, item.Id))
        .ToArray();
    private IReadOnlyList<MusicResource> SelectedPlaylistItems => LibraryList.SelectedItems
        .OfType<MusicResource>()
        .Where(static item => item.CanBeAddedToPlaylist)
        .ToArray();

    public LibraryPage()
    {
        InitializeComponent();
        LibraryList.ItemsSource = _items;
        Loaded += async (_, _) => await LoadAsync();
    }

    private async void OnKindChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_loading && IsLoaded)
        {
            await LoadAsync();
        }
    }

    private async void OnRefreshClicked(object sender, RoutedEventArgs e) => await LoadAsync();

    private async void OnLoadMoreClicked(object sender, RoutedEventArgs e) => await LoadMoreAsync();

    private void OnFavoriteFilterClicked(object sender, RoutedEventArgs e)
    {
        _showFavoritesOnly = FavoriteFilterButton.IsChecked == true;
        LibraryList.SelectedItems.Clear();
        _selected = null;
        _selectedRating = null;
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
        LibraryList.SelectedItems.Clear();
        _selected = null;
        _selectedRating = null;
        ActionStatus.Text = "";
        RefreshVisibleItems();
        UpdateResultStatus();
        UpdateActions();
    }

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

        LibraryList.SelectedItems.Clear();
        _selected = null;
        _selectedRating = null;
        ActionStatus.Text = "";
        RefreshVisibleItems();
        UpdateResultStatus();
        UpdateActions();
    }

    private async void OnSearchKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == Windows.System.VirtualKey.Enter)
        {
            e.Handled = true;
            await SearchAsync();
        }
    }

    private async Task LoadAsync()
    {
        if (_loading)
        {
            return;
        }

        var selected = KindBox.SelectedItem as ComboBoxItem;
        var kind = selected?.Tag as string ?? "songs";
        _loading = true;
        _isSearching = false;
        _nextPages.Clear();
        _loadedItems.Clear();
        UpdateGenreFilterOptions();
        UpdateBusyState();
        StatusText.Text = "Loading your library…";
        _items.Clear();
        _selected = null;
        UpdateActions();
        try
        {
            var page = await App.MusicApi.GetLibraryPageAsync(kind);
            _loadedItems.AddRange(page.Items);
            UpdateGenreFilterOptions();
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
            UpdateBusyState();
        }
    }

    private async Task SearchAsync()
    {
        var term = LibrarySearchBox.Text.Trim();
        if (term.Length == 0)
        {
            await LoadAsync();
            return;
        }

        if (_loading)
        {
            return;
        }

        _loading = true;
        _isSearching = true;
        _nextPages.Clear();
        _loadedItems.Clear();
        UpdateGenreFilterOptions();
        UpdateBusyState();
        StatusText.Text = "Searching your library…";
        _items.Clear();
        _selected = null;
        UpdateActions();
        ActionStatus.Text = "";
        try
        {
            var result = await App.MusicApi.SearchLibraryPageAsync(term);
            _loadedItems.AddRange(result.Items);
            UpdateGenreFilterOptions();
            RefreshVisibleItems();
            _nextPages.AddRange(result.NextPages);
            UpdateResultStatus();
        }
        catch (Exception exception)
        {
            StatusText.Text = exception.Message;
        }
        finally
        {
            _loading = false;
            UpdateBusyState();
        }
    }

    private async Task LoadMoreAsync()
    {
        if (_loading || _nextPages.Count == 0)
        {
            return;
        }

        _loading = true;
        UpdateBusyState();
        StatusText.Text = "Loading more items…";
        try
        {
            var pages = new List<MusicResourcePage>();
            foreach (var nextPage in _nextPages)
            {
                pages.Add(await App.MusicApi.GetNextPageAsync(nextPage));
            }

            _nextPages.Clear();
            foreach (var page in pages)
            {
                _loadedItems.AddRange(page.Items);
                _nextPages.AddRange(page.NextPages);
            }

            UpdateGenreFilterOptions();
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
            UpdateBusyState();
        }
    }

    private void UpdateBusyState()
    {
        KindBox.IsEnabled = !_loading;
        LibrarySearchBox.IsEnabled = !_loading;
        RefreshButton.IsEnabled = !_loading;
        FavoriteFilterButton.IsEnabled = !_loading;
        GenreFilterBox.IsEnabled = !_loading && GenreFilterBox.Items.Count > 1;
        UpdateLoadMoreButton();
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

    private void RefreshVisibleItems()
    {
        _items.Clear();
        var visibleItems = _loadedItems.Where(item =>
        {
            var matchesFavorites = !_showFavoritesOnly || item.IsFavorited == true;
            var matchesGenre = _selectedGenre is null ||
                               item.GenreNames.Contains(_selectedGenre, StringComparer.OrdinalIgnoreCase);
            return matchesFavorites && matchesGenre;
        });

        visibleItems = _sortMode switch
        {
            "title" => visibleItems.OrderBy(static item => item.Name, StringComparer.CurrentCultureIgnoreCase),
            "artist" => visibleItems
                .OrderBy(static item => string.IsNullOrWhiteSpace(item.ArtistName))
                .ThenBy(static item => item.ArtistName, StringComparer.CurrentCultureIgnoreCase),
            "album" => visibleItems
                .OrderBy(static item => string.IsNullOrWhiteSpace(item.AlbumName))
                .ThenBy(static item => item.AlbumName, StringComparer.CurrentCultureIgnoreCase),
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
        var sortSuffix = _sortMode == "apple"
            ? ""
            : $" · Sorted by {(SortBox.SelectedItem as ComboBoxItem)?.Content}";
        if (!_showFavoritesOnly)
        {
            var genreSuffix = (_selectedGenre is null ? "" : $" · {_selectedGenre}") + sortSuffix;
            if (_isSearching)
            {
                StatusText.Text = $"{_items.Count} results{genreSuffix}";
            }
            else
            {
                var selected = KindBox.SelectedItem as ComboBoxItem;
                var kind = selected?.Tag as string ?? "songs";
                StatusText.Text = $"{_items.Count} {kind}{genreSuffix}";
            }

            return;
        }

        var unknownCount = _loadedItems.Count(static item => item.IsFavorited is null);
        var status = $"{_items.Count} favorites among {_loadedItems.Count} loaded items";
        if (_selectedGenre is not null)
        {
            status += $" · {_selectedGenre}";
        }

        status += sortSuffix;

        if (unknownCount > 0)
        {
            status += $" · Apple did not report favorite status for {unknownCount}";
        }

        if (_nextPages.Count > 0)
        {
            status += " · load more to continue";
        }

        StatusText.Text = status;
    }

    private void UpdateLoadMoreButton()
    {
        LoadMoreButton.Visibility = _nextPages.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        LoadMoreButton.IsEnabled = !_loading && _nextPages.Count > 0;
    }

    private async void OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        _selected = e.AddedItems.OfType<MusicResource>().LastOrDefault()
            ?? LibraryList.SelectedItems.OfType<MusicResource>().LastOrDefault();
        ActionStatus.Text = "";
        _selectedRating = null;
        UpdateActions();
        await LoadSelectedRatingAsync(_selected);
    }

    private async Task LoadSelectedRatingAsync(MusicResource? item)
    {
        _ratingLookupCancellation?.Cancel();
        _ratingLookupCancellation?.Dispose();
        _ratingLookupCancellation = null;
        _ratingLoading = false;
        if (!SupportsLibraryRating(item))
        {
            UpdateActions();
            return;
        }

        var cancellation = new CancellationTokenSource();
        _ratingLookupCancellation = cancellation;
        _ratingLoading = true;
        ActionStatus.Text = "Loading saved rating…";
        UpdateActions();
        try
        {
            var rating = await App.MusicApi.GetRatingAsync(item!, cancellation.Token);
            if (!cancellation.IsCancellationRequested && ReferenceEquals(_selected, item))
            {
                _selectedRating = rating;
                ActionStatus.Text = rating switch
                {
                    1 => "This item is liked.",
                    -1 => "This item is disliked.",
                    _ => "This item has no rating."
                };
            }
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            if (!cancellation.IsCancellationRequested && ReferenceEquals(_selected, item))
            {
                ActionStatus.Text = exception.Message;
            }
        }
        finally
        {
            if (ReferenceEquals(_ratingLookupCancellation, cancellation))
            {
                _ratingLookupCancellation = null;
                _ratingLoading = false;
                UpdateActions();
            }

            cancellation.Dispose();
        }
    }

    private void OnOpenClicked(object sender, RoutedEventArgs e)
    {
        if (_selected is not null && HasRelatedItems(_selected))
        {
            Frame.Navigate(typeof(ResourcePage), _selected);
        }
    }

    private async void OnPlayClicked(object sender, RoutedEventArgs e)
    {
        var items = SelectedPlaylistItems.Where(static item => item.CanPlayPreview).ToArray();
        if (items.Length == 0 || _resolvingPlayback)
        {
            return;
        }

        var startAt = _selected;
        _resolvingPlayback = true;
        UpdateActions();
        ActionStatus.Text = "Resolving preview audio…";
        try
        {
            var playableItems = await App.MusicApi.ResolvePreviewsForPlaybackAsync(items);
            await App.Player.PlayQueueAsync(playableItems, startAt);
            ActionStatus.Text = playableItems.Count > 1
                ? "Playing available previews from the selection."
                : "Playing the available preview.";
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
        var items = SelectedPlaylistItems.Where(static item => item.CanPlayPreview).ToArray();
        if (items.Length == 0 || _resolvingPlayback)
        {
            return;
        }

        _resolvingPlayback = true;
        UpdateActions();
        ActionStatus.Text = "Resolving previews to add to the queue…";
        try
        {
            var count = await App.EnqueuePreviewsAsync(items);
            ActionStatus.Text = count == 1 ? "Added one preview to the queue." : $"Added {count} previews to the queue.";
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

    private void OnAddClicked(object sender, RoutedEventArgs e)
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

    private async void OnLikeClicked(object sender, RoutedEventArgs e) => await RateSelectedAsync(1, "Marked as liked.");

    private async void OnDislikeClicked(object sender, RoutedEventArgs e) => await RateSelectedAsync(-1, "Marked as disliked.");

    private async void OnClearRatingClicked(object sender, RoutedEventArgs e) => await RateSelectedAsync(null, "Rating cleared.");

    private async Task RateSelectedAsync(int? value, string successMessage)
    {
        var selected = _selected;
        if (!SupportsLibraryRating(selected) || _ratingUpdating || _ratingLoading)
        {
            return;
        }

        _ratingUpdating = true;
        UpdateActions();
        try
        {
            await App.MusicApi.SetRatingAsync(selected!, value);
            if (ReferenceEquals(_selected, selected))
            {
                _selectedRating = value;
                ActionStatus.Text = successMessage;
            }
        }
        catch (Exception exception)
        {
            if (ReferenceEquals(_selected, selected))
            {
                ActionStatus.Text = exception.Message;
            }
        }
        finally
        {
            _ratingUpdating = false;
            UpdateActions();
        }
    }

    private void UpdateActions()
    {
        var selected = _selected;
        OpenButton.IsEnabled = _selected is not null && HasRelatedItems(_selected);
        var playableCount = SelectedPlaylistItems.Count(static item => item.CanPlayPreview);
        PlayButton.Content = playableCount > 1 ? $"Play {playableCount} previews" : "Play preview";
        PlayButton.IsEnabled = !_loading && !_resolvingPlayback && playableCount > 0;
        QueueButton.Content = playableCount > 1 ? $"Add {playableCount} to queue" : "Add to queue";
        QueueButton.IsEnabled = !_loading && !_resolvingPlayback && playableCount > 0;
        var playlistItemCount = SelectedPlaylistItems.Count;
        AddButton.Content = playlistItemCount > 1
            ? $"Add {playlistItemCount} items to playlist"
            : "Add to playlist";
        AddButton.IsEnabled = playlistItemCount > 0;
        var favoriteItemCount = SelectedFavoriteItems.Count;
        FavoriteButton.Content = favoriteItemCount switch
        {
            > 1 => $"Add {favoriteItemCount} to favorites",
            1 => "Add to favorites",
            _ when selected?.IsFavorited == true => "Already favorited",
            _ => "Add to favorites"
        };
        FavoriteButton.IsEnabled = !_loading && !_addingToFavorites && favoriteItemCount > 0;
        var supportsRating = SupportsLibraryRating(selected);
        var canEditRating = supportsRating && !_ratingLoading && !_ratingUpdating;
        LikeButton.Content = _selectedRating == 1 ? "Liked" : "Like";
        DislikeButton.Content = _selectedRating == -1 ? "Disliked" : "Dislike";
        LikeButton.IsEnabled = canEditRating && _selectedRating != 1;
        DislikeButton.IsEnabled = canEditRating && _selectedRating != -1;
        ClearRatingButton.IsEnabled = canEditRating && _selectedRating is not null;
    }

    private static bool SupportsLibraryRating(MusicResource? item) => item?.Type is
        "library-songs" or "library-albums" or "library-playlists" or "library-music-videos";

    private static bool HasRelatedItems(MusicResource item) => item.Type is
        "library-albums" or "library-artists" or "library-playlists" or "library-songs";
}

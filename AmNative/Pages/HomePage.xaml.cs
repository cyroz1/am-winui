using System.Collections.ObjectModel;
using AmNative.Models;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace AmNative.Pages;

public sealed partial class HomePage : Page
{
    private readonly ObservableCollection<MusicResource> _recommendations = new();
    private readonly ObservableCollection<MusicResource> _recentlyPlayed = new();
    private readonly ObservableCollection<MusicResource> _heavyRotation = new();
    private MusicResource? _selected;
    private Uri? _nextRecentlyPlayedPage;
    private Uri? _nextHeavyRotationPage;
    private bool _loaded;
    private bool _loading;
    private bool _loadingMoreRecentlyPlayed;
    private bool _loadingMoreHeavyRotation;
    private bool _resolvingPlayback;
    private bool _addingToLibrary;
    private bool _addingToFavorites;
    private IReadOnlyList<MusicResource> SelectedResources => RecommendationsList.SelectedItems
        .OfType<MusicResource>()
        .Concat(RecentlyPlayedList.SelectedItems.OfType<MusicResource>())
        .Concat(HeavyRotationList.SelectedItems.OfType<MusicResource>())
        .DistinctBy(static item => (item.Type, item.Id))
        .ToArray();
    private IReadOnlyList<MusicResource> SelectedLibraryItems => SelectedResources
        .Where(static item => item.CanBeAddedToLibrary)
        .DistinctBy(static item => (item.Type, item.Id))
        .ToArray();
    private IReadOnlyList<MusicResource> SelectedFavoriteItems => SelectedResources
        .Where(static item => item.CanBeFavorited && item.IsFavorited != true)
        .DistinctBy(static item => (item.Type, item.Id))
        .ToArray();
    private IReadOnlyList<MusicResource> SelectedPlaylistItems => SelectedResources
        .Where(static item => item.CanBeAddedToPlaylist)
        .DistinctBy(static item => (item.Type, item.Id))
        .ToArray();

    public HomePage()
    {
        InitializeComponent();
        RecommendationsList.ItemsSource = _recommendations;
        RecentlyPlayedList.ItemsSource = _recentlyPlayed;
        HeavyRotationList.ItemsSource = _heavyRotation;
        Loaded += async (_, _) => await LoadAsync();
    }

    private void OnConnectClicked(object sender, RoutedEventArgs e)
    {
        Frame.Navigate(typeof(SettingsPage));
    }

    private async void OnRefreshClicked(object sender, RoutedEventArgs e) => await LoadAsync(refresh: true);

    private async void OnLoadMoreRecentlyPlayedClicked(object sender, RoutedEventArgs e) => await LoadMoreRecentlyPlayedAsync();

    private async void OnLoadMoreHeavyRotationClicked(object sender, RoutedEventArgs e) => await LoadMoreHeavyRotationAsync();

    private async Task LoadAsync(bool refresh = false)
    {
        if (_loading || _loadingMoreRecentlyPlayed || _loadingMoreHeavyRotation || (_loaded && !refresh))
        {
            return;
        }

        _loaded = true;
        _loading = true;
        ConnectButton.Visibility = App.Credentials.HasCompleteCredentials ? Visibility.Collapsed : Visibility.Visible;
        RefreshButton.IsEnabled = false;
        RecommendationsList.IsEnabled = false;
        RecentlyPlayedList.IsEnabled = false;
        HeavyRotationList.IsEnabled = false;
        if (refresh)
        {
            _recommendations.Clear();
            _recentlyPlayed.Clear();
            _heavyRotation.Clear();
            _nextRecentlyPlayedPage = null;
            _nextHeavyRotationPage = null;
            _selected = null;
            ActionStatus.Text = "";
            UpdateActions();
        }

        UpdateRecentlyPlayedLoadMoreButton();
        UpdateHeavyRotationLoadMoreButton();
        StatusText.Text = "Loading your Apple Music home…";
        var hadError = false;
        try
        {
            try
            {
                var recommendations = await App.MusicApi.GetRecommendationsAsync();
                foreach (var item in recommendations)
                {
                    _recommendations.Add(item);
                }
            }
            catch (Exception exception)
            {
                hadError = true;
                StatusText.Text = exception.Message;
            }

            try
            {
                var recentlyPlayed = await App.MusicApi.GetRecentlyPlayedTracksPageAsync();
                foreach (var item in recentlyPlayed.Items)
                {
                    _recentlyPlayed.Add(item);
                }

                _nextRecentlyPlayedPage = recentlyPlayed.NextPages.FirstOrDefault();
            }
            catch (Exception exception)
            {
                hadError = true;
                StatusText.Text = _recommendations.Count > 0
                    ? $"Recommendations loaded. {exception.Message}"
                    : exception.Message;
            }

            try
            {
                var heavyRotation = await App.MusicApi.GetHeavyRotationPageAsync();
                foreach (var item in heavyRotation.Items)
                {
                    _heavyRotation.Add(item);
                }

                _nextHeavyRotationPage = heavyRotation.NextPages.FirstOrDefault();
            }
            catch (Exception exception)
            {
                hadError = true;
                StatusText.Text = _recommendations.Count > 0 || _recentlyPlayed.Count > 0
                    ? $"Your other home sections loaded. {exception.Message}"
                    : exception.Message;
            }

            if (_recommendations.Count == 0 && _recentlyPlayed.Count == 0 && _heavyRotation.Count == 0 && App.Credentials.HasAnyCredential && !hadError)
            {
                StatusText.Text = "Apple Music returned no recommendations, recently played tracks, or heavy rotation items.";
            }
            else if ((_recommendations.Count > 0 || _recentlyPlayed.Count > 0 || _heavyRotation.Count > 0) && !hadError)
            {
                StatusText.Text = "Your recommendations, listening history, and heavy rotation.";
            }
        }
        finally
        {
            _loading = false;
            RefreshButton.IsEnabled = true;
            RecommendationsList.IsEnabled = true;
            RecentlyPlayedList.IsEnabled = true;
            HeavyRotationList.IsEnabled = true;
            UpdateActions();
            UpdateRecentlyPlayedLoadMoreButton();
            UpdateHeavyRotationLoadMoreButton();
        }
    }

    private async Task LoadMoreRecentlyPlayedAsync()
    {
        if (_loading || _loadingMoreRecentlyPlayed || _loadingMoreHeavyRotation || _nextRecentlyPlayedPage is null)
        {
            return;
        }

        var nextPage = _nextRecentlyPlayedPage;
        _loadingMoreRecentlyPlayed = true;
        UpdateRecentlyPlayedLoadMoreButton();
        try
        {
            var page = await App.MusicApi.GetNextPageAsync(nextPage);
            if (_nextRecentlyPlayedPage != nextPage)
            {
                return;
            }

            foreach (var item in page.Items)
            {
                if (!_recentlyPlayed.Any(existing => existing.Id == item.Id && existing.Type == item.Type))
                {
                    _recentlyPlayed.Add(item);
                }
            }

            _nextRecentlyPlayedPage = page.NextPages.FirstOrDefault();
            StatusText.Text = $"{_recentlyPlayed.Count} recently played tracks";
        }
        catch (Exception exception)
        {
            StatusText.Text = exception.Message;
        }
        finally
        {
            _loadingMoreRecentlyPlayed = false;
            UpdateRecentlyPlayedLoadMoreButton();
        }
    }

    private void UpdateRecentlyPlayedLoadMoreButton()
    {
        LoadMoreRecentlyPlayedButton.Visibility = _nextRecentlyPlayedPage is null
            ? Visibility.Collapsed
            : Visibility.Visible;
        LoadMoreRecentlyPlayedButton.IsEnabled = !_loading && !_loadingMoreRecentlyPlayed && !_loadingMoreHeavyRotation && _nextRecentlyPlayedPage is not null;
        RefreshButton.IsEnabled = !_loading && !_loadingMoreRecentlyPlayed && !_loadingMoreHeavyRotation;
    }

    private async Task LoadMoreHeavyRotationAsync()
    {
        if (_loading || _loadingMoreRecentlyPlayed || _loadingMoreHeavyRotation || _nextHeavyRotationPage is null)
        {
            return;
        }

        var nextPage = _nextHeavyRotationPage;
        _loadingMoreHeavyRotation = true;
        UpdateHeavyRotationLoadMoreButton();
        try
        {
            var page = await App.MusicApi.GetNextPageAsync(nextPage);
            if (_nextHeavyRotationPage != nextPage)
            {
                return;
            }

            foreach (var item in page.Items)
            {
                if (!_heavyRotation.Any(existing => existing.Id == item.Id && existing.Type == item.Type))
                {
                    _heavyRotation.Add(item);
                }
            }

            _nextHeavyRotationPage = page.NextPages.FirstOrDefault();
            StatusText.Text = $"{_heavyRotation.Count} heavy rotation items";
        }
        catch (Exception exception)
        {
            StatusText.Text = exception.Message;
        }
        finally
        {
            _loadingMoreHeavyRotation = false;
            UpdateHeavyRotationLoadMoreButton();
        }
    }

    private void UpdateHeavyRotationLoadMoreButton()
    {
        LoadMoreHeavyRotationButton.Visibility = _nextHeavyRotationPage is null
            ? Visibility.Collapsed
            : Visibility.Visible;
        LoadMoreHeavyRotationButton.IsEnabled = !_loading && !_loadingMoreRecentlyPlayed && !_loadingMoreHeavyRotation && _nextHeavyRotationPage is not null;
        RefreshButton.IsEnabled = !_loading && !_loadingMoreRecentlyPlayed && !_loadingMoreHeavyRotation;
    }

    private void OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        _selected = e.AddedItems.OfType<MusicResource>().LastOrDefault()
            ?? RecommendationsList.SelectedItems.OfType<MusicResource>().LastOrDefault()
            ?? RecentlyPlayedList.SelectedItems.OfType<MusicResource>().LastOrDefault()
            ?? HeavyRotationList.SelectedItems.OfType<MusicResource>().LastOrDefault();
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

    private void OnAddToPlaylistClicked(object sender, RoutedEventArgs e)
    {
        var items = SelectedPlaylistItems;
        if (items.Count > 0)
        {
            Frame.Navigate(typeof(PlaylistsPage), items.ToArray());
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
        PlayButton.IsEnabled = false;
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

    private void UpdateActions()
    {
        OpenButton.IsEnabled = !_loading && _selected is not null && HasRelatedItems(_selected);
        var playableCount = SelectedPlaylistItems.Count(static item => item.CanPlayPreview);
        PlayButton.Content = playableCount > 1 ? $"Play {playableCount} previews" : "Play preview";
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
        PlaylistButton.IsEnabled = !_loading && playlistItemCount > 0;
        var favoriteItemCount = SelectedFavoriteItems.Count;
        FavoriteButton.Content = favoriteItemCount switch
        {
            > 1 => $"Add {favoriteItemCount} to favorites",
            1 => "Add to favorites",
            _ when _selected?.IsFavorited == true => "Already favorited",
            _ => "Add to favorites"
        };
        FavoriteButton.IsEnabled = !_loading && !_addingToFavorites && favoriteItemCount > 0;
    }

    private static bool HasRelatedItems(MusicResource resource) => resource.Type is
        "albums" or "album" or "library-albums" or "artists" or "artist" or "library-artists" or "playlists" or "playlist" or "library-playlists";
}

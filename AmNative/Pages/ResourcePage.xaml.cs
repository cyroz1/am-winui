using System.Collections.ObjectModel;
using AmNative.Models;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;

namespace AmNative.Pages;

public sealed partial class ResourcePage : Page
{
    private readonly ObservableCollection<MusicResource> _related = new();
    private MusicResource? _resource;
    private MusicResource? _selected;
    private Uri? _nextPage;
    private ArtistView _artistView;
    private bool _loading;
    private bool _addingToLibrary;
    private bool _addingToFavorites;
    private IReadOnlyList<MusicResource> SelectedLibraryItems => RelatedList.SelectedItems
        .OfType<MusicResource>()
        .Where(static item => item.CanBeAddedToLibrary)
        .DistinctBy(static item => (item.Type, item.Id))
        .ToArray();
    private IReadOnlyList<MusicResource> SelectedFavoriteItems => RelatedList.SelectedItems
        .OfType<MusicResource>()
        .Where(static item => item.CanBeFavorited && item.IsFavorited != true)
        .DistinctBy(static item => (item.Type, item.Id))
        .ToArray();
    private IReadOnlyList<MusicResource> SelectedPlaylistItems => RelatedList.SelectedItems
        .OfType<MusicResource>()
        .Where(static item => item.CanBeAddedToPlaylist)
        .ToArray();

    public ResourcePage()
    {
        InitializeComponent();
        RelatedList.ItemsSource = _related;
    }

    protected override async void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        _resource = e.Parameter as MusicResource;
        _selected = null;
        _nextPage = null;
        _artistView = ArtistView.Albums;
        _related.Clear();
        UpdateActions();

        if (_resource is null)
        {
            TitleText.Text = "Apple Music item";
            SubtitleText.Text = "No item was selected.";
            StatusText.Text = "Return to the previous page and choose an album, artist, or playlist.";
            return;
        }

        var resource = _resource;
        TitleText.Text = resource.Name;
        SubtitleText.Text = resource.Subtitle;
        await LoadRelatedPageAsync(resource, append: false);
    }

    private async void OnLoadMoreClicked(object sender, RoutedEventArgs e)
    {
        if (_resource is not null)
        {
            await LoadRelatedPageAsync(_resource, append: true);
        }
    }

    private async Task LoadRelatedPageAsync(MusicResource resource, bool append)
    {
        if (_loading || !ReferenceEquals(_resource, resource) || (append && _nextPage is null))
        {
            return;
        }

        _loading = true;
        RelatedList.IsEnabled = false;
        UpdateActions();
        StatusText.Text = append
            ? "Loading more items…"
            : IsCatalogArtist(resource) ? $"Loading {GetArtistViewLabel(_artistView)}…" : IsArtist(resource) ? "Loading albums…" : "Loading tracks…";
        try
        {
            var page = append
                ? await App.MusicApi.GetNextPageAsync(_nextPage!)
                : IsCatalogArtist(resource)
                    ? await GetArtistViewPageAsync(resource)
                    : await App.MusicApi.GetRelatedResourcesPageAsync(resource);
            if (!ReferenceEquals(_resource, resource))
            {
                return;
            }

            if (!append)
            {
                _related.Clear();
            }

            foreach (var item in page.Items)
            {
                if (!_related.Any(existing => existing.Id == item.Id && existing.Type == item.Type))
                {
                    _related.Add(item);
                }
            }

            _nextPage = page.NextPages.FirstOrDefault();
            StatusText.Text = _related.Count == 0
                ? IsCatalogArtist(resource)
                    ? $"No {GetArtistViewLabel(_artistView)} were returned for this artist."
                    : IsArtist(resource) ? "No albums were returned for this artist." : "No tracks were returned for this item."
                : IsCatalogArtist(resource)
                    ? $"{_related.Count} {GetArtistViewLabel(_artistView)}"
                    : IsArtist(resource) ? $"{_related.Count} albums" : $"{_related.Count} tracks";
        }
        catch (Exception exception)
        {
            if (ReferenceEquals(_resource, resource))
            {
                StatusText.Text = exception.Message;
            }
        }
        finally
        {
            _loading = false;
            RelatedList.IsEnabled = true;
            UpdateActions();
        }
    }

    private Task<MusicResourcePage> GetArtistViewPageAsync(MusicResource artist) => _artistView switch
    {
        ArtistView.TopSongs => App.MusicApi.GetArtistTopSongsPageAsync(artist),
        ArtistView.TopMusicVideos => App.MusicApi.GetArtistTopMusicVideosPageAsync(artist),
        _ => App.MusicApi.GetRelatedResourcesPageAsync(artist)
    };

    private Task<IReadOnlyList<MusicResource>> GetArtistViewItemsAsync(MusicResource artist) => _artistView switch
    {
        ArtistView.TopSongs => App.MusicApi.GetArtistTopSongsAsync(artist),
        ArtistView.TopMusicVideos => App.MusicApi.GetArtistTopMusicVideosAsync(artist),
        _ => App.MusicApi.GetRelatedResourcesAsync(artist)
    };

    private static string GetArtistViewLabel(ArtistView view) => view switch
    {
        ArtistView.TopSongs => "top songs",
        ArtistView.TopMusicVideos => "top music videos",
        _ => "albums"
    };

    private void OnBackClicked(object sender, RoutedEventArgs e)
    {
        if (Frame.CanGoBack)
        {
            Frame.GoBack();
        }
    }

    private async void OnArtistAlbumsClicked(object sender, RoutedEventArgs e) =>
        await SetArtistViewAsync(ArtistView.Albums);

    private async void OnArtistTopSongsClicked(object sender, RoutedEventArgs e) =>
        await SetArtistViewAsync(ArtistView.TopSongs);

    private async void OnArtistTopMusicVideosClicked(object sender, RoutedEventArgs e) =>
        await SetArtistViewAsync(ArtistView.TopMusicVideos);

    private async Task SetArtistViewAsync(ArtistView view)
    {
        if (_loading || _resource is null || !IsCatalogArtist(_resource) || _artistView == view)
        {
            return;
        }

        var resource = _resource;
        _artistView = view;
        _nextPage = null;
        _selected = null;
        RelatedList.SelectedItems.Clear();
        _related.Clear();
        ActionStatus.Text = "";
        UpdateActions();
        await LoadRelatedPageAsync(resource, append: false);
    }

    private async void OnAddParentClicked(object sender, RoutedEventArgs e)
    {
        if (_resource is null || !_resource.CanBeAddedToLibrary || _addingToLibrary)
        {
            return;
        }

        _addingToLibrary = true;
        UpdateActions();
        try
        {
            await App.MusicApi.AddToLibraryAsync(_resource);
            ActionStatus.Text = $"Sent “{_resource.Name}” to your Apple Music library.";
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

    private async void OnFavoriteParentClicked(object sender, RoutedEventArgs e)
    {
        if (_resource is not null)
        {
            await FavoriteAsync(new[] { _resource });
        }
    }

    private void OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        _selected = e.AddedItems.OfType<MusicResource>().LastOrDefault()
            ?? RelatedList.SelectedItems.OfType<MusicResource>().LastOrDefault();
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

    private async void OnPlayClicked(object sender, RoutedEventArgs e)
    {
        if (_selected is null || _resource is null || _loading)
        {
            return;
        }

        var resource = _resource;
        var selected = _selected;
        var selectedItems = SelectedPlaylistItems;
        var queueSelection = selectedItems.Count > 1;
        _loading = true;
        RelatedList.IsEnabled = false;
        UpdateActions();
        try
        {
            var items = queueSelection
                ? await App.MusicApi.ResolvePreviewsForPlaybackAsync(
                    selectedItems.Where(static item => item.CanPlayPreview))
                : IsCatalogArtist(resource) && _artistView != ArtistView.Albums
                    ? await GetArtistViewItemsAsync(resource)
                    : await App.MusicApi.GetRelatedResourcesAsync(resource);
            if (!ReferenceEquals(_resource, resource))
            {
                return;
            }

            await App.Player.PlayQueueAsync(items, selected);
            ActionStatus.Text = queueSelection
                ? "Playing available previews from the selection."
                : IsCatalogArtist(resource) && _artistView == ArtistView.TopSongs
                    ? "Playing available top song previews from the selected track."
                    : IsCatalogArtist(resource) && _artistView == ArtistView.TopMusicVideos
                        ? "Playing available music video previews from the selected video."
                        : "Playing the selected preview and following tracks.";
        }
        catch (Exception exception)
        {
            ActionStatus.Text = exception.Message;
        }
        finally
        {
            _loading = false;
            RelatedList.IsEnabled = true;
            UpdateActions();
        }
    }

    private async void OnQueueClicked(object sender, RoutedEventArgs e)
    {
        var items = SelectedPlaylistItems.Where(static item => item.CanPlayPreview).ToArray();
        if (items.Length == 0 || _loading)
        {
            return;
        }

        _loading = true;
        RelatedList.IsEnabled = false;
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
            _loading = false;
            RelatedList.IsEnabled = true;
            UpdateActions();
        }
    }

    private async void OnPlayAllClicked(object sender, RoutedEventArgs e)
    {
        if (_resource is null || _loading)
        {
            return;
        }

        var resource = _resource;
        _loading = true;
        RelatedList.IsEnabled = false;
        UpdateActions();
        try
        {
            var artistView = IsCatalogArtist(resource) && _artistView != ArtistView.Albums;
            var items = artistView
                ? await GetArtistViewItemsAsync(resource)
                : await App.MusicApi.GetRelatedResourcesAsync(resource);
            if (!ReferenceEquals(_resource, resource))
            {
                return;
            }

            await App.Player.PlayQueueAsync(items);
            ActionStatus.Text = artistView
                ? _artistView == ArtistView.TopSongs
                    ? "Playing available top song previews."
                    : "Playing available music video previews."
                : "Playing available previews in this collection.";
        }
        catch (Exception exception)
        {
            ActionStatus.Text = exception.Message;
        }
        finally
        {
            _loading = false;
            RelatedList.IsEnabled = true;
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

    private async void OnFavoriteClicked(object sender, RoutedEventArgs e)
    {
        await FavoriteAsync(SelectedFavoriteItems);
    }

    private async Task FavoriteAsync(IEnumerable<MusicResource> resources)
    {
        var items = resources
            .Where(static item => item.CanBeFavorited && item.IsFavorited != true)
            .DistinctBy(static item => (item.Type, item.Id))
            .ToArray();
        if (items.Length == 0 || _addingToFavorites)
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

            ActionStatus.Text = items.Length == 1
                ? $"Requested “{items[0].Name}” be added to favorites."
                : $"Requested {items.Length} items be added to favorites.";
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

    private void UpdateActions()
    {
        AddParentButton.IsEnabled = !_loading && !_addingToLibrary && _resource?.CanBeAddedToLibrary == true;
        FavoriteParentButton.Content = _resource?.IsFavorited == true ? "Already favorited" : "Add to favorites";
        FavoriteParentButton.IsEnabled = !_loading && !_addingToFavorites &&
                                         _resource is { CanBeFavorited: true } &&
                                         _resource.IsFavorited != true;
        ArtistViewButtons.Visibility = IsCatalogArtist(_resource) ? Visibility.Visible : Visibility.Collapsed;
        ArtistAlbumsButton.IsEnabled = !_loading && _artistView != ArtistView.Albums;
        ArtistTopSongsButton.IsEnabled = !_loading && _artistView != ArtistView.TopSongs;
        ArtistTopMusicVideosButton.IsEnabled = !_loading && _artistView != ArtistView.TopMusicVideos;
        LoadMoreButton.Visibility = _nextPage is null ? Visibility.Collapsed : Visibility.Visible;
        LoadMoreButton.IsEnabled = !_loading && _nextPage is not null;
        OpenButton.IsEnabled = !_loading && _selected is not null && HasRelatedItems(_selected);
        PlayAllButton.Content = _artistView switch
        {
            ArtistView.TopSongs => "Play all top songs",
            ArtistView.TopMusicVideos => "Play all music video previews",
            _ => "Play all previews"
        };
        PlayAllButton.IsEnabled = !_loading && _related.Count > 0 &&
                                  (IsTrackCollection(_resource) || _artistView is ArtistView.TopSongs or ArtistView.TopMusicVideos);
        var selectedItemCount = SelectedPlaylistItems.Count;
        var playableCount = SelectedPlaylistItems.Count(static item => item.CanPlayPreview);
        var previewLabel = playableCount == 1 ? "preview" : "previews";
        PlayButton.Content = selectedItemCount > 1
            ? playableCount > 0 ? $"Play {playableCount} selected {previewLabel}" : "No selected previews"
            : "Play from here";
        PlayButton.IsEnabled = !_loading && playableCount > 0;
        QueueButton.Content = playableCount > 1 ? $"Add {playableCount} to queue" : "Add to queue";
        QueueButton.IsEnabled = !_loading && playableCount > 0;
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

    private static bool IsArtist(MusicResource resource) => resource.Type is "artists" or "artist" or "library-artists";

    private static bool IsCatalogArtist(MusicResource? resource) => resource?.Type is "artists" or "artist";

    private static bool IsTrackCollection(MusicResource? resource) => resource?.Type is
        "albums" or "album" or "library-albums" or "playlists" or "playlist" or "library-playlists";

    private static bool HasRelatedItems(MusicResource resource) => resource.Type is
        "albums" or "album" or "library-albums" or "artists" or "artist" or "library-artists" or "playlists" or "playlist" or "library-playlists";

    private enum ArtistView
    {
        Albums,
        TopSongs,
        TopMusicVideos
    }
}

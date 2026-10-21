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
    private bool _showArtistTopSongs;
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
        .Where(static item => item.CanBeFavorited && !item.IsFavorited)
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
        _showArtistTopSongs = false;
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
            : _showArtistTopSongs ? "Loading top songs…" : IsArtist(resource) ? "Loading albums…" : "Loading tracks…";
        try
        {
            var page = append
                ? await App.MusicApi.GetNextPageAsync(_nextPage!)
                : _showArtistTopSongs
                    ? await App.MusicApi.GetArtistTopSongsPageAsync(resource)
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
                ? _showArtistTopSongs ? "No top songs were returned for this artist." : IsArtist(resource) ? "No albums were returned for this artist." : "No tracks were returned for this item."
                : _showArtistTopSongs ? $"{_related.Count} top songs" : IsArtist(resource) ? $"{_related.Count} albums" : $"{_related.Count} tracks";
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

    private void OnBackClicked(object sender, RoutedEventArgs e)
    {
        if (Frame.CanGoBack)
        {
            Frame.GoBack();
        }
    }

    private async void OnArtistAlbumsClicked(object sender, RoutedEventArgs e)
    {
        if (_resource is null || !_showArtistTopSongs)
        {
            return;
        }

        _showArtistTopSongs = false;
        _nextPage = null;
        _related.Clear();
        _selected = null;
        ActionStatus.Text = "";
        UpdateActions();
        await LoadRelatedPageAsync(_resource, append: false);
    }

    private async void OnArtistTopSongsClicked(object sender, RoutedEventArgs e)
    {
        if (_resource is null || _showArtistTopSongs || !IsCatalogArtist(_resource))
        {
            return;
        }

        _showArtistTopSongs = true;
        _nextPage = null;
        _related.Clear();
        _selected = null;
        ActionStatus.Text = "";
        UpdateActions();
        await LoadRelatedPageAsync(_resource, append: false);
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
                : _showArtistTopSongs
                    ? await App.MusicApi.GetArtistTopSongsAsync(resource)
                    : await App.MusicApi.GetRelatedResourcesAsync(resource);
            if (!ReferenceEquals(_resource, resource))
            {
                return;
            }

            await App.Player.PlayQueueAsync(items, selected);
            ActionStatus.Text = queueSelection
                ? "Playing available previews from the selection."
                : _showArtistTopSongs
                    ? "Playing available top song previews from the selected track."
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
            var items = _showArtistTopSongs
                ? await App.MusicApi.GetArtistTopSongsAsync(resource)
                : await App.MusicApi.GetRelatedResourcesAsync(resource);
            if (!ReferenceEquals(_resource, resource))
            {
                return;
            }

            await App.Player.PlayQueueAsync(items);
            ActionStatus.Text = _showArtistTopSongs
                ? "Playing available top song previews."
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
            .Where(static item => item.CanBeFavorited && !item.IsFavorited)
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
        FavoriteParentButton.IsEnabled = !_loading && !_addingToFavorites && _resource is { CanBeFavorited: true, IsFavorited: false };
        ArtistViewButtons.Visibility = IsCatalogArtist(_resource) ? Visibility.Visible : Visibility.Collapsed;
        ArtistAlbumsButton.IsEnabled = !_loading && _showArtistTopSongs;
        ArtistTopSongsButton.IsEnabled = !_loading && !_showArtistTopSongs;
        LoadMoreButton.Visibility = _nextPage is null ? Visibility.Collapsed : Visibility.Visible;
        LoadMoreButton.IsEnabled = !_loading && _nextPage is not null;
        OpenButton.IsEnabled = !_loading && _selected is not null && HasRelatedItems(_selected);
        PlayAllButton.Content = _showArtistTopSongs ? "Play all top songs" : "Play all previews";
        PlayAllButton.IsEnabled = !_loading && _related.Count > 0 && (IsTrackCollection(_resource) || _showArtistTopSongs);
        var selectedItemCount = SelectedPlaylistItems.Count;
        var playableCount = SelectedPlaylistItems.Count(static item => item.CanPlayPreview);
        var previewLabel = playableCount == 1 ? "preview" : "previews";
        PlayButton.Content = selectedItemCount > 1
            ? playableCount > 0 ? $"Play {playableCount} selected {previewLabel}" : "No selected previews"
            : "Play from here";
        PlayButton.IsEnabled = !_loading && playableCount > 0;
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
}

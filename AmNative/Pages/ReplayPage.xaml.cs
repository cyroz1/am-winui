using System.Collections.ObjectModel;
using AmNative.Models;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace AmNative.Pages;

public sealed partial class ReplayPage : Page
{
    private readonly ObservableCollection<MusicResource> _songs = new();
    private readonly ObservableCollection<MusicResource> _albums = new();
    private readonly ObservableCollection<MusicResource> _artists = new();
    private Uri? _nextSongsPage;
    private Uri? _nextAlbumsPage;
    private Uri? _nextArtistsPage;
    private bool _loaded;
    private bool _loading;
    private bool _loadingMore;
    private bool _resolvingPlayback;
    private bool _addingToLibrary;
    private bool _addingToFavorites;
    private string _activeView = "top-songs";
    private MusicResource? _selected;

    private ObservableCollection<MusicResource> ActiveItems => _activeView switch
    {
        "top-albums" => _albums,
        "top-artists" => _artists,
        _ => _songs
    };

    private IReadOnlyList<MusicResource> SelectedLibraryItems => ReplayList.SelectedItems
        .OfType<MusicResource>()
        .Where(static item => item.CanBeAddedToLibrary)
        .DistinctBy(static item => (item.Type, item.Id))
        .ToArray();

    private IReadOnlyList<MusicResource> SelectedFavoriteItems => ReplayList.SelectedItems
        .OfType<MusicResource>()
        .Where(static item => item.CanBeFavorited && item.IsFavorited != true)
        .DistinctBy(static item => (item.Type, item.Id))
        .ToArray();

    private IReadOnlyList<MusicResource> SelectedPlaylistItems => ReplayList.SelectedItems
        .OfType<MusicResource>()
        .Where(static item => item.CanBeAddedToPlaylist)
        .DistinctBy(static item => (item.Type, item.Id))
        .ToArray();

    private IReadOnlyList<MusicResource> SelectedPlayableItems => ReplayList.SelectedItems
        .OfType<MusicResource>()
        .Where(static item => item.CanPlayPreview)
        .ToArray();

    public ReplayPage()
    {
        InitializeComponent();
        ReplayList.ItemsSource = _songs;
        Loaded += async (_, _) => await LoadAsync();
    }

    private async void OnRefreshClicked(object sender, RoutedEventArgs e) => await LoadAsync(refresh: true);

    private void OnViewChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ViewBox.SelectedItem is not ComboBoxItem { Tag: string view })
        {
            return;
        }

        _activeView = view;
        ReplayList.ItemsSource = ActiveItems;
        _selected = null;
        ActionStatus.Text = "";
        UpdateActions();
        UpdateLoadMoreButton();
        UpdateItemCount();
    }

    private async Task LoadAsync(bool refresh = false)
    {
        if (_loading || (_loaded && !refresh))
        {
            return;
        }

        _loaded = true;
        _loading = true;
        _songs.Clear();
        _albums.Clear();
        _artists.Clear();
        _nextSongsPage = null;
        _nextAlbumsPage = null;
        _nextArtistsPage = null;
        _selected = null;
        ActionStatus.Text = "";
        StatusText.Text = "Loading Replay…";
        RefreshButton.IsEnabled = false;
        ViewBox.IsEnabled = false;
        ReplayList.IsEnabled = false;
        UpdateActions();
        UpdateLoadMoreButton();

        try
        {
            var summary = await App.MusicApi.GetReplaySummaryAsync();
            if (summary.Year is int year)
            {
                TitleText.Text = $"Apple Music Replay {year}";
            }

            AddItems(_songs, summary.TopSongs.Items);
            AddItems(_albums, summary.TopAlbums.Items);
            AddItems(_artists, summary.TopArtists.Items);
            _nextSongsPage = summary.TopSongs.NextPages.FirstOrDefault();
            _nextAlbumsPage = summary.TopAlbums.NextPages.FirstOrDefault();
            _nextArtistsPage = summary.TopArtists.NextPages.FirstOrDefault();

            if (_songs.Count + _albums.Count + _artists.Count == 0)
            {
                StatusText.Text = "Replay appears here after Apple Music has enough listening history for your account.";
            }
            else
            {
                UpdateItemCount();
            }
        }
        catch (Exception exception)
        {
            StatusText.Text = exception.Message;
        }
        finally
        {
            _loading = false;
            RefreshButton.IsEnabled = true;
            ViewBox.IsEnabled = true;
            ReplayList.IsEnabled = true;
            UpdateActions();
            UpdateLoadMoreButton();
        }
    }

    private async void OnLoadMoreClicked(object sender, RoutedEventArgs e)
    {
        var nextPage = ActiveNextPage;
        if (_loading || _loadingMore || nextPage is null)
        {
            return;
        }

        _loadingMore = true;
        UpdateLoadMoreButton();
        try
        {
            var page = await App.MusicApi.GetNextReplayViewPageAsync(nextPage, ActiveRelationship);
            if (ActiveNextPage != nextPage)
            {
                return;
            }

            AddItems(ActiveItems, page.Items);
            SetActiveNextPage(page.NextPages.FirstOrDefault());
            UpdateItemCount();
        }
        catch (Exception exception)
        {
            StatusText.Text = exception.Message;
        }
        finally
        {
            _loadingMore = false;
            UpdateLoadMoreButton();
        }
    }

    private void OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        _selected = e.AddedItems.OfType<MusicResource>().LastOrDefault()
            ?? ReplayList.SelectedItems.OfType<MusicResource>().LastOrDefault();
        ActionStatus.Text = "";
        UpdateActions();
    }

    private void OnOpenClicked(object sender, RoutedEventArgs e)
    {
        if (_selected is not null && CanOpen(_selected))
        {
            Frame.Navigate(typeof(ResourcePage), _selected);
        }
    }

    private async void OnPlayClicked(object sender, RoutedEventArgs e)
    {
        var items = SelectedPlayableItems;
        if (items.Count == 0 || _resolvingPlayback)
        {
            return;
        }

        _resolvingPlayback = true;
        UpdateActions();
        ActionStatus.Text = "Resolving preview audio…";
        try
        {
            var playableItems = await App.MusicApi.ResolvePreviewsForPlaybackAsync(items);
            await App.Player.PlayQueueAsync(playableItems, _selected);
            ActionStatus.Text = $"Playing {playableItems.Count} available preview{(playableItems.Count == 1 ? "" : "s")}.";
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
        var items = SelectedPlayableItems;
        if (items.Count == 0 || _resolvingPlayback)
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
        if (SelectedPlaylistItems.Count > 0)
        {
            Frame.Navigate(typeof(PlaylistsPage), SelectedPlaylistItems.ToArray());
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

    private Uri? ActiveNextPage => _activeView switch
    {
        "top-albums" => _nextAlbumsPage,
        "top-artists" => _nextArtistsPage,
        _ => _nextSongsPage
    };

    private string ActiveRelationship => _activeView switch
    {
        "top-albums" => "album",
        "top-artists" => "artist",
        _ => "song"
    };

    private void SetActiveNextPage(Uri? uri)
    {
        switch (_activeView)
        {
            case "top-albums":
                _nextAlbumsPage = uri;
                break;
            case "top-artists":
                _nextArtistsPage = uri;
                break;
            default:
                _nextSongsPage = uri;
                break;
        }
    }

    private void UpdateActions()
    {
        var selected = ReplayList.SelectedItems.OfType<MusicResource>().ToArray();
        OpenButton.IsEnabled = !_loading && selected.Length == 1 && _selected is not null && CanOpen(_selected);
        PlayButton.IsEnabled = !_loading && !_resolvingPlayback && SelectedPlayableItems.Count > 0;
        PlayButton.Content = SelectedPlayableItems.Count > 1 ? "Play previews" : "Play preview";
        QueueButton.IsEnabled = !_loading && !_resolvingPlayback && SelectedPlayableItems.Count > 0;
        QueueButton.Content = SelectedPlayableItems.Count > 1 ? $"Add {SelectedPlayableItems.Count} to queue" : "Add to queue";
        AddButton.IsEnabled = !_loading && !_addingToLibrary && SelectedLibraryItems.Count > 0;
        PlaylistButton.IsEnabled = !_loading && SelectedPlaylistItems.Count > 0;
        FavoriteButton.Content = SelectedFavoriteItems.Count > 1
            ? $"Add {SelectedFavoriteItems.Count} to favorites"
            : _selected?.IsFavorited == true
                ? "Already favorited"
                : "Add to favorites";
        FavoriteButton.IsEnabled = !_loading && !_addingToFavorites && SelectedFavoriteItems.Count > 0;
    }

    private void UpdateLoadMoreButton()
    {
        LoadMoreButton.Visibility = ActiveNextPage is null ? Visibility.Collapsed : Visibility.Visible;
        LoadMoreButton.IsEnabled = !_loading && !_loadingMore && ActiveNextPage is not null;
    }

    private void UpdateItemCount()
    {
        var label = _activeView switch
        {
            "top-albums" => "top albums",
            "top-artists" => "top artists",
            _ => "top songs"
        };
        StatusText.Text = $"{ActiveItems.Count} {label}";
    }

    private static void AddItems(ObservableCollection<MusicResource> destination, IEnumerable<MusicResource> resources)
    {
        foreach (var resource in resources)
        {
            if (!destination.Any(existing => existing.Id == resource.Id && existing.Type == resource.Type))
            {
                destination.Add(resource);
            }
        }
    }

    private static bool CanOpen(MusicResource resource) => resource.Type is
        "albums" or "album" or "artists" or "artist" or "playlists" or "playlist" or
        "library-albums" or "library-artists" or "library-playlists";
}

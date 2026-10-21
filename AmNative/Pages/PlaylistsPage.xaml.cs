using System.Collections.ObjectModel;
using AmNative.Models;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;

namespace AmNative.Pages;

public sealed partial class PlaylistsPage : Page
{
    private const string RootPlaylistFolderId = "p.playlistsroot";
    private readonly ObservableCollection<MusicResource> _playlists = new();
    private readonly ObservableCollection<MusicResource> _tracks = new();
    private readonly List<MusicResource> _folderStack = new();
    private MusicResource? _selectedPlaylist;
    private MusicResource? _selectedTrack;
    private IReadOnlyList<MusicResource> _pendingItems = Array.Empty<MusicResource>();
    private Uri? _nextFolderPage;
    private Uri? _nextTrackPage;
    private bool _loading;
    private bool _loadingTracks;
    private bool _mutating;

    private string CurrentFolderId => _folderStack.Count == 0 ? RootPlaylistFolderId : _folderStack[^1].Id;

    public PlaylistsPage()
    {
        InitializeComponent();
        PlaylistList.ItemsSource = _playlists;
        TracksList.ItemsSource = _tracks;
        Loaded += async (_, _) => await LoadAsync();
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        _pendingItems = e.Parameter switch
        {
            MusicResource item when item.CanBeAddedToPlaylist => new[] { item },
            IEnumerable<MusicResource> items => items.Where(static item => item.CanBeAddedToPlaylist).ToArray(),
            _ => Array.Empty<MusicResource>()
        };
        UpdateSelection();
    }

    private async Task LoadAsync(bool append = false)
    {
        if (_loading || (append && _nextFolderPage is null))
        {
            return;
        }

        if (!append)
        {
            _nextFolderPage = null;
            _playlists.Clear();
        }

        _loading = true;
        var folderId = CurrentFolderId;
        var isRoot = _folderStack.Count == 0;
        CurrentFolderText.Text = isRoot ? "Playlist folders" : _folderStack[^1].Name;
        UpFolderButton.Visibility = isRoot ? Visibility.Collapsed : Visibility.Visible;
        UpdateCommandState();
        StatusText.Text = append ? "Loading more folders and playlists…" : "Loading your playlists…";
        try
        {
            var page = append
                ? await App.MusicApi.GetNextPageAsync(_nextFolderPage!)
                : await App.MusicApi.GetPlaylistFolderContentsPageAsync(folderId);
            if (folderId != CurrentFolderId)
            {
                return;
            }

            foreach (var playlist in page.Items)
            {
                if (!_playlists.Any(existing => existing.Id == playlist.Id && existing.Type == playlist.Type))
                {
                    _playlists.Add(playlist);
                }
            }

            _nextFolderPage = page.NextPages.FirstOrDefault();
            StatusText.Text = $"{_playlists.Count} items";
        }
        catch (Exception exception)
        {
            StatusText.Text = exception.Message;
        }
        finally
        {
            _loading = false;
            UpdateCommandState();
            UpdateSelection();
        }
    }

    private async void OnCreateClicked(object sender, RoutedEventArgs e)
    {
        var name = PlaylistNameBox.Text.Trim();
        var description = PlaylistDescriptionBox.Text.Trim();
        if (name.Length == 0)
        {
            StatusText.Text = "Enter a playlist name first.";
            return;
        }

        if (_loading || _mutating)
        {
            return;
        }

        _mutating = true;
        UpdateCommandState();
        try
        {
            var playlist = await App.MusicApi.CreatePlaylistAsync(name, description, parentFolderId: CurrentFolderId);
            PlaylistNameBox.Text = "";
            PlaylistDescriptionBox.Text = "";
            StatusText.Text = playlist is null ? "Playlist created." : $"Created “{playlist.Name}”.";
            await LoadAsync();
        }
        catch (Exception exception)
        {
            StatusText.Text = exception.Message;
        }
        finally
        {
            _mutating = false;
            UpdateCommandState();
        }
    }

    private async void OnCreateFolderClicked(object sender, RoutedEventArgs e)
    {
        var name = PlaylistNameBox.Text.Trim();
        if (name.Length == 0)
        {
            StatusText.Text = "Enter a folder name first.";
            return;
        }

        if (_loading || _mutating)
        {
            return;
        }

        _mutating = true;
        UpdateCommandState();
        try
        {
            await App.MusicApi.CreatePlaylistFolderAsync(name, CurrentFolderId);
            PlaylistNameBox.Text = "";
            PlaylistDescriptionBox.Text = "";
            StatusText.Text = $"Created folder “{name}”.";
            await LoadAsync();
        }
        catch (Exception exception)
        {
            StatusText.Text = exception.Message;
        }
        finally
        {
            _mutating = false;
            UpdateCommandState();
        }
    }

    private async void OnUpFolderClicked(object sender, RoutedEventArgs e)
    {
        if (_folderStack.Count == 0)
        {
            return;
        }

        _folderStack.RemoveAt(_folderStack.Count - 1);
        _selectedPlaylist = null;
        _selectedTrack = null;
        _nextFolderPage = null;
        _nextTrackPage = null;
        _tracks.Clear();
        TracksHeading.Text = "Tracks";
        UpdateSelection();
        UpdateCommandState();
        await LoadAsync();
    }

    private async void OnPlaylistClicked(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is not MusicResource clickedItem)
        {
            return;
        }

        if (clickedItem.Type == "library-playlist-folders")
        {
            _selectedPlaylist = null;
            _selectedTrack = null;
            _nextFolderPage = null;
            _nextTrackPage = null;
            _tracks.Clear();
            TracksHeading.Text = "Tracks";
            _folderStack.Add(clickedItem);
            UpdateSelection();
            UpdateCommandState();
            await LoadAsync();
            return;
        }

        if (clickedItem.Type != "library-playlists")
        {
            return;
        }

        _selectedPlaylist = clickedItem;
        _selectedTrack = null;
        _nextTrackPage = null;
        _tracks.Clear();
        UpdateSelection();

        if (_selectedPlaylist is null)
        {
            return;
        }

        var selectedPlaylist = _selectedPlaylist;
        TracksHeading.Text = $"Tracks in {selectedPlaylist.Name}";
        await LoadPlaylistTracksAsync(selectedPlaylist, append: false);
    }

    private void OnTrackClicked(object sender, ItemClickEventArgs e)
    {
        _selectedTrack = e.ClickedItem as MusicResource;
        UpdateSelection();
    }

    private async void OnLoadMoreTracksClicked(object sender, RoutedEventArgs e)
    {
        if (_selectedPlaylist is not null)
        {
            await LoadPlaylistTracksAsync(_selectedPlaylist, append: true);
        }
    }

    private async void OnLoadMoreFoldersClicked(object sender, RoutedEventArgs e) => await LoadAsync(append: true);

    private async Task LoadPlaylistTracksAsync(MusicResource playlist, bool append)
    {
        if (_loadingTracks || _selectedPlaylist?.Id != playlist.Id || (append && _nextTrackPage is null))
        {
            return;
        }

        _loadingTracks = true;
        UpdateCommandState();
        StatusText.Text = append ? "Loading more tracks…" : "Loading playlist tracks…";
        try
        {
            var page = append
                ? await App.MusicApi.GetNextPageAsync(_nextTrackPage!)
                : await App.MusicApi.GetPlaylistTracksPageAsync(playlist.Id);
            if (_selectedPlaylist?.Id != playlist.Id)
            {
                return;
            }

            if (!append)
            {
                _tracks.Clear();
            }

            foreach (var track in page.Items)
            {
                if (!_tracks.Any(existing => existing.Id == track.Id && existing.Type == track.Type))
                {
                    _tracks.Add(track);
                }
            }

            _nextTrackPage = page.NextPages.FirstOrDefault();
            StatusText.Text = $"{_tracks.Count} tracks";
        }
        catch (Exception exception)
        {
            StatusText.Text = exception.Message;
        }
        finally
        {
            _loadingTracks = false;
            UpdateCommandState();
            UpdateSelection();
        }
    }

    private async void OnAddSongClicked(object sender, RoutedEventArgs e)
    {
        if (_pendingItems.Count == 0 || _selectedPlaylist is null || !_selectedPlaylist.CanEdit ||
            _loading || _loadingTracks || _mutating)
        {
            return;
        }

        _mutating = true;
        UpdateCommandState();
        try
        {
            var selectedPlaylist = _selectedPlaylist;
            var pendingItems = _pendingItems;
            await App.MusicApi.AddTracksToPlaylistAsync(selectedPlaylist.Id, pendingItems);
            await OnReloadTracksAsync();
            StatusText.Text = pendingItems.Count == 1
                ? $"Added “{pendingItems[0].Name}” to “{selectedPlaylist.Name}”."
                : $"Added {pendingItems.Count} items to “{selectedPlaylist.Name}”.";
        }
        catch (Exception exception)
        {
            StatusText.Text = exception.Message;
        }
        finally
        {
            _mutating = false;
            UpdateCommandState();
            UpdateSelection();
        }
    }

    private async Task OnReloadTracksAsync()
    {
        if (_selectedPlaylist is null)
        {
            return;
        }

        await LoadPlaylistTracksAsync(_selectedPlaylist, append: false);
    }

    private async void OnPlayTrackClicked(object sender, RoutedEventArgs e)
    {
        if (_selectedTrack is null || _selectedPlaylist is null || _loading || _loadingTracks || _mutating)
        {
            return;
        }

        var selectedPlaylist = _selectedPlaylist;
        var selectedTrack = _selectedTrack;
        _loadingTracks = true;
        UpdateCommandState();
        try
        {
            StatusText.Text = "Loading the playlist queue…";
            var tracks = await App.MusicApi.GetPlaylistTracksAsync(selectedPlaylist.Id);
            if (tracks.Count == 0)
            {
                StatusText.Text = "This playlist has no tracks to play.";
                return;
            }

            await App.Player.PlayQueueAsync(tracks, selectedTrack);
            StatusText.Text = "Playing available previews from the selected track.";
        }
        catch (Exception exception)
        {
            StatusText.Text = exception.Message;
        }
        finally
        {
            _loadingTracks = false;
            UpdateCommandState();
            UpdateSelection();
        }
    }

    private async void OnQueueTrackClicked(object sender, RoutedEventArgs e)
    {
        if (_selectedTrack is null || !_selectedTrack.CanPlayPreview || _loading || _loadingTracks || _mutating)
        {
            return;
        }

        var track = _selectedTrack;
        _loadingTracks = true;
        UpdateCommandState();
        try
        {
            var count = await App.EnqueuePreviewsAsync(new[] { track });
            StatusText.Text = count == 1 ? $"Added “{track.Name}” to the queue." : $"Added {count} previews to the queue.";
        }
        catch (Exception exception)
        {
            StatusText.Text = exception.Message;
        }
        finally
        {
            _loadingTracks = false;
            UpdateCommandState();
        }
    }

    private void UpdateSelection()
    {
        SelectedPlaylistText.Text = _selectedPlaylist is { CanEdit: false }
            ? $"“{_selectedPlaylist.Name}” is read-only and can’t be changed."
            : _pendingItems.Count == 0
                ? "Choose tracks or videos in Home, Search, Library, or item details first."
                : _pendingItems.Count == 1
                    ? $"Selected item: {_pendingItems[0].Name}"
                    : $"{_pendingItems.Count} items selected";
        UpdateCommandState();
    }

    private void UpdateCommandState()
    {
        var enabled = !_loading && !_loadingTracks && !_mutating;
        UpFolderButton.IsEnabled = enabled;
        CreatePlaylistButton.IsEnabled = enabled;
        CreateFolderButton.IsEnabled = enabled;
        LoadMoreFoldersButton.Visibility = _nextFolderPage is null ? Visibility.Collapsed : Visibility.Visible;
        LoadMoreFoldersButton.IsEnabled = enabled && _nextFolderPage is not null;
        PlaylistList.IsEnabled = enabled;
        TracksList.IsEnabled = !_loadingTracks && !_mutating;
        LoadMoreTracksButton.Visibility = _nextTrackPage is null ? Visibility.Collapsed : Visibility.Visible;
        LoadMoreTracksButton.IsEnabled = enabled && _nextTrackPage is not null;
        AddSongButton.Content = _pendingItems.Count switch
        {
            1 => "Add selected item",
            > 1 => $"Add {_pendingItems.Count} selected items",
            _ => "Add selected items"
        };
        AddSongButton.IsEnabled = enabled && _pendingItems.Count > 0 && _selectedPlaylist is not null &&
                                  _selectedPlaylist.CanEdit && _pendingItems.All(static item => item.CanBeAddedToPlaylist);
        PlayTrackButton.IsEnabled = enabled && _selectedTrack?.CanPlayPreview == true;
        QueueTrackButton.IsEnabled = enabled && _selectedTrack?.CanPlayPreview == true;
    }
}

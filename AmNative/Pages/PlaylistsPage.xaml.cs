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
    private MusicResource? _pendingSong;
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
        _pendingSong = e.Parameter as MusicResource;
        UpdateSelection();
    }

    private async Task LoadAsync()
    {
        if (_loading)
        {
            return;
        }

        _loading = true;
        var folderId = CurrentFolderId;
        var isRoot = _folderStack.Count == 0;
        CurrentFolderText.Text = isRoot ? "Playlist folders" : _folderStack[^1].Name;
        UpFolderButton.Visibility = isRoot ? Visibility.Collapsed : Visibility.Visible;
        UpdateCommandState();
        StatusText.Text = "Loading your playlists…";
        try
        {
            var playlists = await App.MusicApi.GetPlaylistFolderContentsAsync(folderId);
            if (folderId != CurrentFolderId)
            {
                return;
            }

            _playlists.Clear();
            foreach (var playlist in playlists)
            {
                _playlists.Add(playlist);
            }

            StatusText.Text = $"{playlists.Count} items";
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
            var playlist = await App.MusicApi.CreatePlaylistAsync(name, parentFolderId: CurrentFolderId);
            PlaylistNameBox.Text = "";
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
        if (_pendingSong is null || _selectedPlaylist is null || !_selectedPlaylist.CanEdit ||
            _loading || _loadingTracks || _mutating)
        {
            return;
        }

        _mutating = true;
        UpdateCommandState();
        try
        {
            var selectedPlaylist = _selectedPlaylist;
            var pendingSong = _pendingSong;
            await App.MusicApi.AddTracksToPlaylistAsync(selectedPlaylist.Id, new[] { pendingSong });
            await OnReloadTracksAsync();
            StatusText.Text = $"Added “{pendingSong.Name}” to “{selectedPlaylist.Name}”.";
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

    private void UpdateSelection()
    {
        SelectedPlaylistText.Text = _selectedPlaylist is { CanEdit: false }
            ? $"“{_selectedPlaylist.Name}” is read-only and can’t be changed."
            : _pendingSong is null
                ? "Choose a track in Search or Library first."
                : $"Selected track: {_pendingSong.Name}";
        UpdateCommandState();
    }

    private void UpdateCommandState()
    {
        var enabled = !_loading && !_loadingTracks && !_mutating;
        UpFolderButton.IsEnabled = enabled;
        CreatePlaylistButton.IsEnabled = enabled;
        CreateFolderButton.IsEnabled = enabled;
        PlaylistList.IsEnabled = enabled;
        TracksList.IsEnabled = !_loadingTracks && !_mutating;
        LoadMoreTracksButton.Visibility = _nextTrackPage is null ? Visibility.Collapsed : Visibility.Visible;
        LoadMoreTracksButton.IsEnabled = enabled && _nextTrackPage is not null;
        AddSongButton.IsEnabled = enabled && _pendingSong is not null && _selectedPlaylist is not null &&
                                  _selectedPlaylist.CanEdit && IsPlaylistTrack(_pendingSong);
        PlayTrackButton.IsEnabled = enabled && _selectedTrack?.CanPlayPreview == true;
    }

    private static bool IsPlaylistTrack(MusicResource resource) => resource.Type is
        "songs" or "song" or "library-songs" or "music-videos" or "music-video" or "library-music-videos";
}

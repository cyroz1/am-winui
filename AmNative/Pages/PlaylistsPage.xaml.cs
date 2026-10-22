using System.Collections.ObjectModel;
using AmNative.Models;
using AmNative.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;

namespace AmNative.Pages;

public sealed partial class PlaylistsPage : Page
{
    private const string RootPlaylistFolderId = "p.playlistsroot";
    private readonly ObservableCollection<MusicResource> _playlists = new();
    private readonly ObservableCollection<MusicResource> _tracks = new();
    private readonly List<MusicResource> _loadedTracks = new();
    private readonly List<MusicResource> _folderStack = new();
    private MusicResource? _selectedPlaylist;
    private MusicResource? _selectedTrack;
    private IReadOnlyList<MusicResource> _pendingItems = Array.Empty<MusicResource>();
    private IReadOnlyList<MusicResource> SelectedPlaylistItems => TracksList.SelectedItems
        .OfType<MusicResource>()
        .Where(static item => item.CanBeAddedToPlaylist)
        .ToArray();
    private IReadOnlyList<MusicResource> SelectedPlayableItems => SelectedPlaylistItems
        .Where(static item => item.CanStartPlayback)
        .ToArray();
    private Uri? _nextFolderPage;
    private Uri? _nextTrackPage;
    private string _trackSortMode = "apple";
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
            var selectedTracks = _pendingItems;
            var isPublic = PublicPlaylistCheckBox.IsChecked == true;
            var playlist = await App.MusicApi.CreatePlaylistAsync(
                name,
                description,
                parentFolderId: CurrentFolderId,
                initialTracks: selectedTracks,
                isPublic: isPublic);
            PlaylistNameBox.Text = "";
            PlaylistDescriptionBox.Text = "";
            PublicPlaylistCheckBox.IsChecked = false;
            var playlistName = playlist?.Name ?? name;
            var visibility = isPublic ? "public " : "";
            var trackSummary = selectedTracks.Count == 0 ? "" : $" with {selectedTracks.Count} selected items";
            StatusText.Text = $"Created {visibility}playlist “{playlistName}”{trackSummary}.";
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
        TrackFilterBox.Text = "";
        TracksList.SelectedItems.Clear();
        _loadedTracks.Clear();
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
            TrackFilterBox.Text = "";
            TracksList.SelectedItems.Clear();
            _loadedTracks.Clear();
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
        TrackFilterBox.Text = "";
        TracksList.SelectedItems.Clear();
        _loadedTracks.Clear();
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

    private void OnTrackSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        _selectedTrack = e.AddedItems.OfType<MusicResource>().LastOrDefault()
            ?? TracksList.SelectedItems.OfType<MusicResource>().LastOrDefault();
        UpdateSelection();
    }

    private async void OnOpenAppleMusicClicked(object sender, RoutedEventArgs e)
    {
        var item = _selectedTrack ?? _selectedPlaylist;
        if (!AppleMusicLink.CanOpen(item))
        {
            return;
        }

        try
        {
            StatusText.Text = await AppleMusicLink.LaunchAsync(item, App.MusicApi)
                ? "Opened the selected Apple Music link."
                : "Windows could not open this Apple Music link.";
        }
        catch (Exception exception)
        {
            StatusText.Text = exception.Message;
        }
    }

    private void OnTrackFilterChanged(object sender, TextChangedEventArgs e)
    {
        if (!IsLoaded)
        {
            return;
        }

        TracksList.SelectedItems.Clear();
        _selectedTrack = null;
        RefreshVisibleTracks();
        UpdateTrackResultStatus();
        UpdateSelection();
    }

    private void OnTrackSortChanged(object sender, SelectionChangedEventArgs e)
    {
        if (TrackSortBox.SelectedItem is not ComboBoxItem { Tag: string sortMode } || !IsLoaded)
        {
            return;
        }

        _trackSortMode = sortMode;
        TracksList.SelectedItems.Clear();
        _selectedTrack = null;
        RefreshVisibleTracks();
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
                TracksList.SelectedItems.Clear();
                _selectedTrack = null;
                _loadedTracks.Clear();
                _tracks.Clear();
            }

            foreach (var track in page.Items)
            {
                _loadedTracks.Add(track);
            }

            _nextTrackPage = page.NextPages.FirstOrDefault();
            RefreshVisibleTracks(preserveSelection: append);
            UpdateTrackResultStatus();
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
        if (_loading || _loadingTracks || _mutating)
        {
            return;
        }

        if (_pendingItems.Count == 0)
        {
            var selectedItems = SelectedPlaylistItems;
            if (selectedItems.Count > 0)
            {
                Frame.Navigate(typeof(PlaylistsPage), selectedItems.ToArray());
            }

            return;
        }

        if (_selectedPlaylist is null || !_selectedPlaylist.CanEdit)
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
        var selectedItems = SelectedPlaylistItems;
        if (_selectedTrack is null || _selectedPlaylist is null || selectedItems.Count == 0 ||
            _loading || _loadingTracks || _mutating)
        {
            return;
        }

        var selectedPlaylist = _selectedPlaylist;
        var selectedTrack = _selectedTrack;
        _loadingTracks = true;
        UpdateCommandState();
        try
        {
            if (selectedItems.Count > 1)
            {
                StatusText.Text = "Preparing selected tracks…";
                var playableItems = await App.ResolvePlaybackItemsAsync(
                    selectedItems.Where(static item => item.CanStartPlayback));
                if (playableItems.Count == 0)
                {
                    StatusText.Text = "No selected tracks are available for playback.";
                    return;
                }

                await App.Player.PlayQueueAsync(playableItems, selectedTrack);
                StatusText.Text = playableItems.Count == 1
                    ? "Playing the selected Apple Music track."
                    : $"Playing {playableItems.Count} selected Apple Music tracks.";
            }
            else
            {
                StatusText.Text = "Loading the playlist queue…";
                var tracks = await App.MusicApi.GetPlaylistTracksAsync(selectedPlaylist.Id);
                if (tracks.Count == 0)
                {
                    StatusText.Text = "This playlist has no tracks to play.";
                    return;
                }

                var playableTracks = await App.ResolvePlaybackItemsAsync(tracks);
                if (playableTracks.Count == 0)
                {
                    StatusText.Text = "This playlist has no tracks available for playback.";
                    return;
                }

                await App.Player.PlayQueueAsync(playableTracks, selectedTrack);
                StatusText.Text = "Playing tracks from the selected item.";
            }
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
        var items = SelectedPlayableItems;
        if (items.Count == 0 || _loading || _loadingTracks || _mutating)
        {
            return;
        }

        _loadingTracks = true;
        UpdateCommandState();
        try
        {
            var count = await App.EnqueuePlaybackItemsAsync(items);
            StatusText.Text = count == 1
                ? items.Count == 1
                    ? $"Added “{items[0].Name}” to the queue."
                    : "Added one track to the queue."
                : $"Added {count} tracks to the queue.";
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
            && _pendingItems.Count > 0
                ? $"“{_selectedPlaylist.Name}” is read-only and can’t be changed."
            : _pendingItems.Count == 0
                ? SelectedPlaylistItems.Count == 0
                    ? _selectedPlaylist is null
                        ? "Choose tracks or videos in Home, Search, Library, or item details first."
                        : "Select tracks above to play, queue, or copy them."
                    : SelectedPlaylistItems.Count == 1
                        ? $"Selected track: {SelectedPlaylistItems[0].Name}. Choose a destination playlist."
                        : $"{SelectedPlaylistItems.Count} tracks selected. Choose a destination playlist."
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
        PublicPlaylistCheckBox.IsEnabled = enabled;
        LoadMoreFoldersButton.Visibility = _nextFolderPage is null ? Visibility.Collapsed : Visibility.Visible;
        LoadMoreFoldersButton.IsEnabled = enabled && _nextFolderPage is not null;
        PlaylistList.IsEnabled = enabled;
        TracksList.IsEnabled = !_loadingTracks && !_mutating;
        LoadMoreTracksButton.Visibility = _nextTrackPage is null ? Visibility.Collapsed : Visibility.Visible;
        LoadMoreTracksButton.IsEnabled = enabled && _nextTrackPage is not null;
        TrackFilterBox.IsEnabled = _selectedPlaylist is not null && !_loading && !_loadingTracks && !_mutating;
        TrackSortBox.IsEnabled = _selectedPlaylist is not null && !_loading && !_loadingTracks && !_mutating;
        OpenAppleMusicButton.IsEnabled = enabled && AppleMusicLink.CanOpen(_selectedTrack ?? _selectedPlaylist);
        AddSongButton.Content = _pendingItems.Count switch
        {
            1 => "Add selected item",
            > 1 => $"Add {_pendingItems.Count} selected items",
            _ => SelectedPlaylistItems.Count > 0 ? "Choose destination playlist" : "Add selected items"
        };
        AddSongButton.IsEnabled = enabled && (_pendingItems.Count > 0
            ? _selectedPlaylist is not null && _selectedPlaylist.CanEdit && _pendingItems.All(static item => item.CanBeAddedToPlaylist)
            : SelectedPlaylistItems.Count > 0);
        PlayTrackButton.Content = SelectedPlaylistItems.Count > 1 ? "Play selected tracks" : "Play playlist from here";
        PlayTrackButton.IsEnabled = enabled && SelectedPlaylistItems.Count > 0 && SelectedPlayableItems.Count > 0;
        QueueTrackButton.Content = SelectedPlayableItems.Count > 1
            ? $"Add {SelectedPlayableItems.Count} to queue"
            : "Add to queue";
        QueueTrackButton.IsEnabled = enabled && SelectedPlayableItems.Count > 0;
    }

    private void RefreshVisibleTracks(bool preserveSelection = false)
    {
        var selectedTracks = preserveSelection
            ? TracksList.SelectedItems.OfType<MusicResource>().ToArray()
            : Array.Empty<MusicResource>();
        var filter = TrackFilterBox.Text.Trim();
        IEnumerable<MusicResource> visibleTracks = _loadedTracks;
        if (filter.Length > 0)
        {
            visibleTracks = visibleTracks.Where(item =>
                item.Name.Contains(filter, StringComparison.CurrentCultureIgnoreCase) ||
                item.ArtistName.Contains(filter, StringComparison.CurrentCultureIgnoreCase) ||
                item.AlbumName.Contains(filter, StringComparison.CurrentCultureIgnoreCase));
        }

        visibleTracks = _trackSortMode switch
        {
            "title" => visibleTracks.OrderBy(static item => item.Name, StringComparer.CurrentCultureIgnoreCase),
            "artist" => visibleTracks
                .OrderBy(static item => string.IsNullOrWhiteSpace(item.ArtistName))
                .ThenBy(static item => item.ArtistName, StringComparer.CurrentCultureIgnoreCase),
            "album" => visibleTracks
                .OrderBy(static item => string.IsNullOrWhiteSpace(item.AlbumName))
                .ThenBy(static item => item.AlbumName, StringComparer.CurrentCultureIgnoreCase),
            "duration-ascending" => visibleTracks
                .OrderBy(static item => item.DurationInMillis is null)
                .ThenBy(static item => item.DurationInMillis ?? int.MaxValue),
            "duration-descending" => visibleTracks
                .OrderBy(static item => item.DurationInMillis is null)
                .ThenByDescending(static item => item.DurationInMillis ?? int.MinValue),
            _ => visibleTracks
        };

        _tracks.Clear();
        foreach (var track in visibleTracks)
        {
            _tracks.Add(track);
        }

        var restoredSelection = selectedTracks
            .Where(selected => _tracks.Any(track => ReferenceEquals(track, selected)))
            .ToArray();
        foreach (var track in restoredSelection)
        {
            TracksList.SelectedItems.Add(track);
        }

        if (preserveSelection)
        {
            _selectedTrack = restoredSelection.LastOrDefault()
                ?? TracksList.SelectedItems.OfType<MusicResource>().LastOrDefault();
        }
    }

    private void UpdateTrackResultStatus()
    {
        StatusText.Text = string.IsNullOrWhiteSpace(TrackFilterBox.Text)
            ? $"{_loadedTracks.Count} tracks loaded"
            : $"{_tracks.Count} matching of {_loadedTracks.Count} loaded tracks";
    }
}

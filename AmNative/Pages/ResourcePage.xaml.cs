using System.Collections.ObjectModel;
using AmNative.Models;
using AmNative.Services;
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
    private AlbumView _albumView;
    private SongView _songView;
    private MusicVideoView _musicVideoView;
    private string _recordLabelView = "latest-releases";
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
        _albumView = AlbumView.Tracks;
        _songView = SongView.Albums;
        _musicVideoView = MusicVideoView.Albums;
        _recordLabelView = "latest-releases";
        ArtistViewsBox.SelectedItem = ArtistViewsBox.Items
            .OfType<ComboBoxItem>()
            .FirstOrDefault(item => string.Equals(item.Tag as string, GetArtistViewKey(_artistView), StringComparison.Ordinal));
        AlbumViewsBox.SelectedItem = AlbumViewsBox.Items
            .OfType<ComboBoxItem>()
            .FirstOrDefault(item => string.Equals(item.Tag as string, GetAlbumViewKey(_albumView), StringComparison.Ordinal));
        SongViewsBox.SelectedItem = SongViewsBox.Items
            .OfType<ComboBoxItem>()
            .FirstOrDefault(item => string.Equals(item.Tag as string, GetSongViewKey(_songView), StringComparison.Ordinal));
        MusicVideoViewsBox.SelectedItem = MusicVideoViewsBox.Items
            .OfType<ComboBoxItem>()
            .FirstOrDefault(item => string.Equals(item.Tag as string, GetMusicVideoViewKey(_musicVideoView), StringComparison.Ordinal));
        RecordLabelViewsBox.SelectedItem = RecordLabelViewsBox.Items
            .OfType<ComboBoxItem>()
            .FirstOrDefault(item => string.Equals(item.Tag as string, _recordLabelView, StringComparison.Ordinal));
        _related.Clear();
        UpdateActions();

        if (_resource is null)
        {
            TitleText.Text = "Apple Music item";
            SubtitleText.Text = "No item was selected.";
            StatusText.Text = "Return to the previous page and choose an album, artist, playlist, song, or music video.";
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
            : IsCatalogArtist(resource)
                ? $"Loading {GetArtistViewLabel(_artistView)}…"
                : CanBrowseAlbumCatalogViews(resource) && _albumView != AlbumView.Tracks
                    ? $"Loading {GetAlbumViewLabel(_albumView)}…"
                    : IsSongResource(resource)
                        ? $"Loading {GetSongViewLabel(_songView)}…"
                    : IsMusicVideoResource(resource)
                        ? $"Loading {GetMusicVideoViewLabel(_musicVideoView)}…"
                    : IsRecordLabel(resource)
                        ? $"Loading {GetRecordLabelViewLabel(_recordLabelView)}…"
                    : IsCuratorCollection(resource)
                        ? "Loading playlists…"
                    : IsArtist(resource) ? "Loading albums…" : "Loading tracks…";
        try
        {
            var page = append
                ? await App.MusicApi.GetNextPageAsync(_nextPage!)
                : IsCatalogArtist(resource)
                    ? await GetArtistViewPageAsync(resource)
                    : CanBrowseAlbumCatalogViews(resource) && _albumView != AlbumView.Tracks
                        ? await GetAlbumViewPageAsync(resource)
                    : IsSongResource(resource)
                        ? await GetSongViewPageAsync(resource)
                    : IsMusicVideoResource(resource)
                        ? await GetMusicVideoViewPageAsync(resource)
                    : IsRecordLabel(resource)
                        ? await App.MusicApi.GetRecordLabelCatalogViewPageAsync(resource, _recordLabelView)
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
                _related.Add(item);
            }

            _nextPage = page.NextPages.FirstOrDefault();
            StatusText.Text = _related.Count == 0
                ? IsCatalogArtist(resource)
                    ? $"No {GetArtistViewLabel(_artistView)} were returned for this artist."
                    : IsRecordLabel(resource)
                        ? $"No {GetRecordLabelViewLabel(_recordLabelView)} were returned for this record label."
                    : IsCuratorCollection(resource)
                        ? "No playlists were returned for this curator or activity."
                    : CanBrowseAlbumCatalogViews(resource) && _albumView != AlbumView.Tracks
                        ? $"No {GetAlbumViewLabel(_albumView)} were returned for this album."
                    : IsSongResource(resource)
                        ? $"No {GetSongViewLabel(_songView)} were returned for this song."
                    : IsMusicVideoResource(resource)
                        ? $"No {GetMusicVideoViewLabel(_musicVideoView)} were returned for this music video."
                        : IsArtist(resource) ? "No albums were returned for this artist." : "No tracks were returned for this item."
                : IsCatalogArtist(resource)
                    ? $"{_related.Count} {GetArtistViewLabel(_artistView)}"
                    : IsRecordLabel(resource)
                        ? $"{_related.Count} {GetRecordLabelViewLabel(_recordLabelView)}"
                    : IsCuratorCollection(resource)
                        ? $"{_related.Count} playlists"
                    : CanBrowseAlbumCatalogViews(resource) && _albumView != AlbumView.Tracks
                        ? $"{_related.Count} {GetAlbumViewLabel(_albumView)}"
                    : IsSongResource(resource)
                        ? $"{_related.Count} {GetSongViewLabel(_songView)}"
                    : IsMusicVideoResource(resource)
                        ? $"{_related.Count} {GetMusicVideoViewLabel(_musicVideoView)}"
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
        ArtistView.Albums => App.MusicApi.GetRelatedResourcesPageAsync(artist),
        _ => App.MusicApi.GetArtistCatalogViewPageAsync(artist, GetArtistViewKey(_artistView))
    };

    private Task<IReadOnlyList<MusicResource>> GetArtistViewItemsAsync(MusicResource artist) => _artistView switch
    {
        ArtistView.Albums => App.MusicApi.GetRelatedResourcesAsync(artist),
        _ => App.MusicApi.GetArtistCatalogViewAsync(artist, GetArtistViewKey(_artistView))
    };

    private Task<MusicResourcePage> GetAlbumViewPageAsync(MusicResource album) =>
        App.MusicApi.GetAlbumCatalogViewPageAsync(album, GetAlbumViewKey(_albumView));

    private Task<IReadOnlyList<MusicResource>> GetAlbumViewItemsAsync(MusicResource album) =>
        App.MusicApi.GetAlbumCatalogViewAsync(album, GetAlbumViewKey(_albumView));

    private Task<MusicResourcePage> GetSongViewPageAsync(MusicResource song) =>
        App.MusicApi.GetSongRelationshipPageAsync(song, GetSongViewKey(_songView));

    private Task<IReadOnlyList<MusicResource>> GetSongViewItemsAsync(MusicResource song) =>
        App.MusicApi.GetSongRelationshipAsync(song, GetSongViewKey(_songView));

    private Task<MusicResourcePage> GetMusicVideoViewPageAsync(MusicResource musicVideo) =>
        App.MusicApi.GetMusicVideoRelationshipPageAsync(musicVideo, GetMusicVideoViewKey(_musicVideoView));

    private Task<IReadOnlyList<MusicResource>> GetMusicVideoViewItemsAsync(MusicResource musicVideo) =>
        App.MusicApi.GetMusicVideoRelationshipAsync(musicVideo, GetMusicVideoViewKey(_musicVideoView));

    private static string GetArtistViewLabel(ArtistView view) => view switch
    {
        ArtistView.TopSongs => "top songs",
        ArtistView.TopMusicVideos => "top music videos",
        ArtistView.AppearsOnAlbums => "albums this artist appears on",
        ArtistView.CompilationAlbums => "compilation albums",
        ArtistView.FeaturedAlbums => "featured albums",
        ArtistView.FeaturedMusicVideos => "featured music videos",
        ArtistView.FeaturedPlaylists => "featured playlists",
        ArtistView.FullAlbums => "full albums",
        ArtistView.LatestRelease => "latest release",
        ArtistView.LiveAlbums => "live albums",
        ArtistView.SimilarArtists => "similar artists",
        ArtistView.Singles => "singles",
        _ => "albums"
    };

    private static string GetArtistViewKey(ArtistView view) => view switch
    {
        ArtistView.TopSongs => "top-songs",
        ArtistView.TopMusicVideos => "top-music-videos",
        ArtistView.AppearsOnAlbums => "appears-on-albums",
        ArtistView.CompilationAlbums => "compilation-albums",
        ArtistView.FeaturedAlbums => "featured-albums",
        ArtistView.FeaturedMusicVideos => "featured-music-videos",
        ArtistView.FeaturedPlaylists => "featured-playlists",
        ArtistView.FullAlbums => "full-albums",
        ArtistView.LatestRelease => "latest-release",
        ArtistView.LiveAlbums => "live-albums",
        ArtistView.SimilarArtists => "similar-artists",
        ArtistView.Singles => "singles",
        _ => "albums"
    };

    private static ArtistView? GetArtistView(string key) => key switch
    {
        "albums" => ArtistView.Albums,
        "top-songs" => ArtistView.TopSongs,
        "top-music-videos" => ArtistView.TopMusicVideos,
        "appears-on-albums" => ArtistView.AppearsOnAlbums,
        "compilation-albums" => ArtistView.CompilationAlbums,
        "featured-albums" => ArtistView.FeaturedAlbums,
        "featured-music-videos" => ArtistView.FeaturedMusicVideos,
        "featured-playlists" => ArtistView.FeaturedPlaylists,
        "full-albums" => ArtistView.FullAlbums,
        "latest-release" => ArtistView.LatestRelease,
        "live-albums" => ArtistView.LiveAlbums,
        "similar-artists" => ArtistView.SimilarArtists,
        "singles" => ArtistView.Singles,
        _ => null
    };

    private static bool IsArtistViewPlayable(ArtistView view) => view is
        ArtistView.TopSongs or ArtistView.TopMusicVideos or ArtistView.FeaturedMusicVideos;

    private static bool IsArtistVideoView(ArtistView view) => view is
        ArtistView.TopMusicVideos or ArtistView.FeaturedMusicVideos;

    private static string GetAlbumViewLabel(AlbumView view) => view switch
    {
        AlbumView.AppearsOn => "playlists this album appears on",
        AlbumView.OtherVersions => "other versions",
        AlbumView.RelatedAlbums => "related albums",
        AlbumView.RelatedVideos => "related music videos",
        _ => "tracks"
    };

    private static string GetAlbumViewKey(AlbumView view) => view switch
    {
        AlbumView.AppearsOn => "appears-on",
        AlbumView.OtherVersions => "other-versions",
        AlbumView.RelatedAlbums => "related-albums",
        AlbumView.RelatedVideos => "related-videos",
        _ => "tracks"
    };

    private static AlbumView? GetAlbumView(string key) => key switch
    {
        "tracks" => AlbumView.Tracks,
        "appears-on" => AlbumView.AppearsOn,
        "other-versions" => AlbumView.OtherVersions,
        "related-albums" => AlbumView.RelatedAlbums,
        "related-videos" => AlbumView.RelatedVideos,
        _ => null
    };

    private static bool CanBrowseAlbumCatalogViews(MusicResource? resource) =>
        (resource?.Type is "albums" or "album") ||
        (resource is { Type: "library-albums" } libraryAlbum && !string.IsNullOrWhiteSpace(libraryAlbum.CatalogId));

    private bool IsAlbumVideoView(MusicResource? resource) =>
        CanBrowseAlbumCatalogViews(resource) && _albumView == AlbumView.RelatedVideos;

    private static bool IsSongResource(MusicResource? resource) => resource?.Type is
        "songs" or "song" or "library-songs";

    private static bool IsCatalogSong(MusicResource? resource) => resource?.Type is "songs" or "song";

    private static bool IsLibrarySongWithCatalogId(MusicResource? resource) =>
        resource is { Type: "library-songs" } song && !string.IsNullOrWhiteSpace(song.CatalogId);

    private static bool IsMusicVideoResource(MusicResource? resource) => resource?.Type is
        "music-videos" or "music-video" or "library-music-videos";

    private static bool IsCatalogMusicVideo(MusicResource? resource) => resource?.Type is "music-videos" or "music-video";

    private static bool IsLibraryMusicVideoWithCatalogId(MusicResource? resource) =>
        resource is { Type: "library-music-videos" } musicVideo && !string.IsNullOrWhiteSpace(musicVideo.CatalogId);

    private static string GetSongViewLabel(SongView view) => view switch
    {
        SongView.Artists => "artists",
        SongView.MusicVideos => "music videos",
        SongView.Station => "radio station",
        SongView.Catalog => "catalog song",
        _ => "albums"
    };

    private static string GetSongViewKey(SongView view) => view switch
    {
        SongView.Artists => "artists",
        SongView.MusicVideos => "music-videos",
        SongView.Station => "station",
        SongView.Catalog => "catalog",
        _ => "albums"
    };

    private static SongView? GetSongView(string key) => key switch
    {
        "albums" => SongView.Albums,
        "artists" => SongView.Artists,
        "music-videos" => SongView.MusicVideos,
        "station" => SongView.Station,
        "catalog" => SongView.Catalog,
        _ => null
    };

    private static string GetMusicVideoViewLabel(MusicVideoView view) => view switch
    {
        MusicVideoView.Artists => "artists",
        MusicVideoView.Songs => "songs",
        MusicVideoView.Catalog => "catalog video",
        _ => "albums"
    };

    private static string GetMusicVideoViewKey(MusicVideoView view) => view switch
    {
        MusicVideoView.Artists => "artists",
        MusicVideoView.Songs => "songs",
        MusicVideoView.Catalog => "catalog",
        _ => "albums"
    };

    private static MusicVideoView? GetMusicVideoView(string key) => key switch
    {
        "albums" => MusicVideoView.Albums,
        "artists" => MusicVideoView.Artists,
        "songs" => MusicVideoView.Songs,
        "catalog" => MusicVideoView.Catalog,
        _ => null
    };

    private bool IsMusicVideoPlayableView(MusicResource? resource) => IsMusicVideoResource(resource) &&
        (_musicVideoView == MusicVideoView.Songs ||
         (IsLibraryMusicVideoWithCatalogId(resource) && _musicVideoView == MusicVideoView.Catalog));

    private bool IsSongVideoView(MusicResource? resource) =>
        IsCatalogSong(resource) && _songView == SongView.MusicVideos;

    private bool IsSongPreviewView(MusicResource? resource) =>
        IsSongVideoView(resource) || (IsLibrarySongWithCatalogId(resource) && _songView == SongView.Catalog);

    private void OnBackClicked(object sender, RoutedEventArgs e)
    {
        if (Frame.CanGoBack)
        {
            Frame.GoBack();
        }
    }

    private async void OnArtistViewSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ArtistViewsBox.SelectedItem is ComboBoxItem { Tag: string key } && GetArtistView(key) is ArtistView view)
        {
            await SetArtistViewAsync(view);
        }
    }

    private async void OnAlbumViewSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (AlbumViewsBox.SelectedItem is ComboBoxItem { Tag: string key } && GetAlbumView(key) is AlbumView view)
        {
            await SetAlbumViewAsync(view);
        }
    }

    private async void OnSongViewSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (SongViewsBox.SelectedItem is ComboBoxItem { Tag: string key } && GetSongView(key) is SongView view)
        {
            await SetSongViewAsync(view);
        }
    }

    private async void OnMusicVideoViewSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (MusicVideoViewsBox.SelectedItem is ComboBoxItem { Tag: string key } && GetMusicVideoView(key) is MusicVideoView view)
        {
            await SetMusicVideoViewAsync(view);
        }
    }

    private async void OnRecordLabelViewSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (RecordLabelViewsBox.SelectedItem is not ComboBoxItem { Tag: string view } ||
            view is not ("latest-releases" or "top-releases") ||
            _loading || _resource is null || !IsRecordLabel(_resource) ||
            string.Equals(_recordLabelView, view, StringComparison.Ordinal))
        {
            return;
        }

        var resource = _resource;
        _recordLabelView = view;
        _nextPage = null;
        _selected = null;
        RelatedList.SelectedItems.Clear();
        _related.Clear();
        ActionStatus.Text = "";
        UpdateActions();
        await LoadRelatedPageAsync(resource, append: false);
    }

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

    private async Task SetAlbumViewAsync(AlbumView view)
    {
        if (_loading || _resource is null || !CanBrowseAlbumCatalogViews(_resource) || _albumView == view)
        {
            return;
        }

        var resource = _resource;
        _albumView = view;
        _nextPage = null;
        _selected = null;
        RelatedList.SelectedItems.Clear();
        _related.Clear();
        ActionStatus.Text = "";
        UpdateActions();
        await LoadRelatedPageAsync(resource, append: false);
    }

    private async Task SetSongViewAsync(SongView view)
    {
        if (_loading || _resource is null || !IsSongResource(_resource) || _songView == view)
        {
            return;
        }

        var resource = _resource;
        _songView = view;
        _nextPage = null;
        _selected = null;
        RelatedList.SelectedItems.Clear();
        _related.Clear();
        ActionStatus.Text = "";
        UpdateActions();
        await LoadRelatedPageAsync(resource, append: false);
    }

    private async Task SetMusicVideoViewAsync(MusicVideoView view)
    {
        if (_loading || _resource is null || !IsMusicVideoResource(_resource) || _musicVideoView == view)
        {
            return;
        }

        if ((view == MusicVideoView.Songs && !IsCatalogMusicVideo(_resource)) ||
            (view == MusicVideoView.Catalog && !IsLibraryMusicVideoWithCatalogId(_resource)))
        {
            return;
        }

        var resource = _resource;
        _musicVideoView = view;
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

    private async void OnOpenParentAppleMusicClicked(object sender, RoutedEventArgs e)
    {
        try
        {
            ActionStatus.Text = await AppleMusicLink.LaunchAsync(_resource, App.MusicApi)
                ? "Opened the Apple Music link."
                : "Windows could not open this Apple Music link.";
        }
        catch
        {
            ActionStatus.Text = "Windows could not open this Apple Music link.";
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
            IReadOnlyList<MusicResource> items;
            if (queueSelection)
            {
                items = await App.ResolvePlaybackItemsAsync(
                    selectedItems.Where(static item => item.CanStartPlayback));
            }
            else if (IsCatalogArtist(resource) && IsArtistViewPlayable(_artistView))
            {
                items = await GetArtistViewItemsAsync(resource);
            }
            else if (IsAlbumVideoView(resource))
            {
                items = await GetAlbumViewItemsAsync(resource);
            }
            else if (IsSongPreviewView(resource))
            {
                items = await GetSongViewItemsAsync(resource);
            }
            else if (IsMusicVideoResource(resource))
            {
                items = await GetMusicVideoViewItemsAsync(resource);
            }
            else
            {
                items = await App.MusicApi.GetRelatedResourcesAsync(resource);
            }

            if (!ReferenceEquals(_resource, resource))
            {
                return;
            }

            items = await App.ResolvePlaybackItemsAsync(items);
            if (items.Count == 0)
            {
                ActionStatus.Text = "No tracks in this selection are available for playback.";
                return;
            }

            await App.Player.PlayQueueAsync(items, selected);
            ActionStatus.Text = queueSelection
                ? "Playing the selected Apple Music tracks."
                : IsCatalogArtist(resource) && _artistView == ArtistView.TopSongs
                    ? "Playing top songs from the selected track."
                    : IsCatalogArtist(resource) && IsArtistVideoView(_artistView)
                        ? "Playing music videos related to the selected video."
                        : IsAlbumVideoView(resource)
                            ? "Playing music videos from this album."
                        : IsSongVideoView(resource)
                            ? "Playing music videos from this song."
                        : IsMusicVideoResource(resource)
                            ? $"Playing the selected item from the music video’s {GetMusicVideoViewLabel(_musicVideoView)}."
                            : IsLibrarySongWithCatalogId(resource) && _songView == SongView.Catalog
                                ? "Playing the catalog song."
                                : "Playing the selected track and following tracks.";
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

    private async void OnPlayParentClicked(object sender, RoutedEventArgs e)
    {
        if (_resource is null || !(IsSongResource(_resource) || IsMusicVideoResource(_resource)) || !_resource.CanStartPlayback || _loading)
        {
            return;
        }

        var previewResource = _resource;
        var isMusicVideo = IsMusicVideoResource(previewResource);
        _loading = true;
        UpdateActions();
        ActionStatus.Text = isMusicVideo ? "Preparing music video playback…" : "Preparing song playback…";
        try
        {
            var items = await App.ResolvePlaybackItemsAsync(new[] { previewResource });
            if (!ReferenceEquals(_resource, previewResource))
            {
                return;
            }

            if (items.Count == 0)
            {
                ActionStatus.Text = isMusicVideo
                    ? "Apple Music could not prepare this music video for playback."
                    : "Apple Music could not prepare this song for playback.";
                return;
            }

            await App.Player.PlayQueueAsync(items, items[0]);
            ActionStatus.Text = isMusicVideo ? "Playing the music video." : "Playing the song.";
        }
        catch (Exception exception)
        {
            ActionStatus.Text = exception.Message;
        }
        finally
        {
            _loading = false;
            UpdateActions();
        }
    }

    private async void OnQueueClicked(object sender, RoutedEventArgs e)
    {
        var items = SelectedPlaylistItems.Where(static item => item.CanStartPlayback).ToArray();
        if (items.Length == 0 || _loading)
        {
            return;
        }

        _loading = true;
        RelatedList.IsEnabled = false;
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
            _loading = false;
            RelatedList.IsEnabled = true;
            UpdateActions();
        }
    }

    private async void OnPlayAllClicked(object sender, RoutedEventArgs e)
    {
        if (_resource is null || _loading || !CanPlayAllFromCurrentView())
        {
            return;
        }

        var resource = _resource;
        _loading = true;
        RelatedList.IsEnabled = false;
        UpdateActions();
        try
        {
            var artistView = IsCatalogArtist(resource) && IsArtistViewPlayable(_artistView);
            var albumVideoView = IsAlbumVideoView(resource);
            var songVideoView = IsSongVideoView(resource);
            var musicVideoView = IsMusicVideoPlayableView(resource);
            var items = artistView
                ? await GetArtistViewItemsAsync(resource)
                : albumVideoView
                    ? await GetAlbumViewItemsAsync(resource)
                    : songVideoView
                        ? await GetSongViewItemsAsync(resource)
                    : musicVideoView
                        ? await GetMusicVideoViewItemsAsync(resource)
                    : await App.MusicApi.GetRelatedResourcesAsync(resource);
            if (!ReferenceEquals(_resource, resource))
            {
                return;
            }

            items = await App.ResolvePlaybackItemsAsync(items);
            if (items.Count == 0)
            {
                ActionStatus.Text = "This collection has no tracks available for playback.";
                return;
            }

            await App.Player.PlayQueueAsync(items);
            ActionStatus.Text = artistView
                ? $"Playing Apple Music tracks from {GetArtistViewLabel(_artistView)}."
                : albumVideoView
                    ? "Playing music videos from this album."
                    : songVideoView
                        ? "Playing music videos from this song."
                    : musicVideoView
                        ? $"Playing Apple Music tracks from the music video’s {GetMusicVideoViewLabel(_musicVideoView)}."
                        : "Playing tracks in this collection.";
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
        OpenParentAppleMusicButton.IsEnabled = !_loading && AppleMusicLink.CanOpen(_resource);
        AddParentButton.IsEnabled = !_loading && !_addingToLibrary && _resource?.CanBeAddedToLibrary == true;
        FavoriteParentButton.Content = _resource?.IsFavorited == true ? "Already favorited" : "Add to favorites";
        FavoriteParentButton.IsEnabled = !_loading && !_addingToFavorites &&
                                         _resource is { CanBeFavorited: true } &&
                                         _resource.IsFavorited != true;
        ArtistViewSelector.Visibility = IsCatalogArtist(_resource) ? Visibility.Visible : Visibility.Collapsed;
        ArtistViewsBox.IsEnabled = !_loading;
        AlbumViewSelector.Visibility = CanBrowseAlbumCatalogViews(_resource) ? Visibility.Visible : Visibility.Collapsed;
        AlbumViewsBox.IsEnabled = !_loading;
        SongViewSelector.Visibility = IsSongResource(_resource) ? Visibility.Visible : Visibility.Collapsed;
        SongViewsBox.IsEnabled = !_loading;
        SongMusicVideosItem.Visibility = IsCatalogSong(_resource) ? Visibility.Visible : Visibility.Collapsed;
        SongStationItem.Visibility = IsCatalogSong(_resource) ? Visibility.Visible : Visibility.Collapsed;
        SongCatalogItem.Visibility = IsLibrarySongWithCatalogId(_resource) ? Visibility.Visible : Visibility.Collapsed;
        MusicVideoViewSelector.Visibility = IsMusicVideoResource(_resource) ? Visibility.Visible : Visibility.Collapsed;
        MusicVideoViewsBox.IsEnabled = !_loading;
        MusicVideoSongsItem.Visibility = IsCatalogMusicVideo(_resource) ? Visibility.Visible : Visibility.Collapsed;
        MusicVideoCatalogItem.Visibility = IsLibraryMusicVideoWithCatalogId(_resource) ? Visibility.Visible : Visibility.Collapsed;
        RecordLabelViewSelector.Visibility = IsRecordLabel(_resource) ? Visibility.Visible : Visibility.Collapsed;
        RecordLabelViewsBox.IsEnabled = !_loading;
        LoadMoreButton.Visibility = _nextPage is null ? Visibility.Collapsed : Visibility.Visible;
        LoadMoreButton.IsEnabled = !_loading && _nextPage is not null;
        OpenButton.IsEnabled = !_loading && _selected is not null && HasRelatedItems(_selected);
        OpenAppleMusicButton.IsEnabled = !_loading && AppleMusicLink.CanOpen(_selected);
        PlayParentButton.Visibility = IsSongResource(_resource) || IsMusicVideoResource(_resource)
            ? Visibility.Visible
            : Visibility.Collapsed;
        PlayParentButton.Content = IsMusicVideoResource(_resource) ? "Play video" : "Play song";
        PlayParentButton.IsEnabled = !_loading && _resource?.CanStartPlayback == true;
        PlayAllButton.Content = GetPlayAllLabel();
        PlayAllButton.IsEnabled = !_loading && _related.Count > 0 && CanPlayAllFromCurrentView();
        var selectedItemCount = SelectedPlaylistItems.Count;
        var playableCount = SelectedPlaylistItems.Count(static item => item.CanStartPlayback);
        var previewLabel = playableCount == 1 ? "track" : "tracks";
        PlayButton.Content = selectedItemCount > 1
            ? playableCount > 0 ? $"Play {playableCount} selected {previewLabel}" : "No playable tracks selected"
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

    private static bool IsRecordLabel(MusicResource? resource) => resource?.Type is "record-labels";

    private static bool IsCuratorCollection(MusicResource? resource) => resource?.Type is
        "activities" or "apple-curators" or "curators";

    private static bool IsAlbumCollection(MusicResource? resource) => resource?.Type is
        "albums" or "album" or "library-albums";

    private static bool IsPlaylistCollection(MusicResource? resource) => resource?.Type is
        "playlists" or "playlist" or "library-playlists";

    private bool CanPlayAllFromCurrentView() => IsPlaylistCollection(_resource) ||
        (IsAlbumCollection(_resource) && (_albumView is AlbumView.Tracks or AlbumView.RelatedVideos)) ||
        (IsCatalogArtist(_resource) && IsArtistViewPlayable(_artistView)) ||
        IsSongVideoView(_resource) || IsMusicVideoPlayableView(_resource);

    private string GetPlayAllLabel() => IsCatalogArtist(_resource)
        ? GetArtistViewPlayAllLabel(_artistView)
        : IsAlbumVideoView(_resource) || IsSongVideoView(_resource)
            ? "Play all music videos"
        : IsMusicVideoPlayableView(_resource) && _musicVideoView == MusicVideoView.Songs
            ? "Play all related songs"
        : IsMusicVideoPlayableView(_resource)
            ? "Play music video"
            : "Play all";

    private static bool HasRelatedItems(MusicResource resource) => resource.Type is
        "albums" or "album" or "library-albums" or "artists" or "artist" or "library-artists" or
        "playlists" or "playlist" or "library-playlists" or "songs" or "song" or "library-songs" or
        "music-videos" or "music-video" or "library-music-videos" or "activities" or
        "apple-curators" or "curators" or "record-labels";

    private static string GetRecordLabelViewLabel(string view) => view switch
    {
        "top-releases" => "top releases",
        _ => "latest releases"
    };

    private static string GetArtistViewPlayAllLabel(ArtistView view) => view switch
    {
        ArtistView.TopSongs => "Play all top songs",
        ArtistView.TopMusicVideos => "Play all music videos",
        ArtistView.FeaturedMusicVideos => "Play all featured videos",
        _ => "Play all"
    };

    private enum ArtistView
    {
        Albums,
        TopSongs,
        TopMusicVideos,
        AppearsOnAlbums,
        CompilationAlbums,
        FeaturedAlbums,
        FeaturedMusicVideos,
        FeaturedPlaylists,
        FullAlbums,
        LatestRelease,
        LiveAlbums,
        SimilarArtists,
        Singles
    }

    private enum AlbumView
    {
        Tracks,
        AppearsOn,
        OtherVersions,
        RelatedAlbums,
        RelatedVideos
    }

    private enum SongView
    {
        Albums,
        Artists,
        MusicVideos,
        Station,
        Catalog
    }

    private enum MusicVideoView
    {
        Albums,
        Artists,
        Songs,
        Catalog
    }
}

using System.Collections.ObjectModel;
using AmNative.Models;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;

namespace AmNative.Pages;

public sealed partial class LibraryPage : Page
{
    private readonly ObservableCollection<MusicResource> _items = new();
    private readonly List<Uri> _nextPages = new();
    private MusicResource? _selected;
    private bool _loading;
    private bool _isSearching;
    private bool _resolvingPlayback;
    private bool _addingToFavorites;
    private IReadOnlyList<MusicResource> SelectedFavoriteItems => LibraryList.SelectedItems
        .OfType<MusicResource>()
        .Where(static item => item.CanBeFavorited && !item.IsFavorited)
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
        UpdateBusyState();
        StatusText.Text = "Loading your library…";
        _items.Clear();
        _selected = null;
        UpdateActions();
        try
        {
            var page = await App.MusicApi.GetLibraryPageAsync(kind);
            foreach (var item in page.Items)
            {
                _items.Add(item);
            }

            _nextPages.AddRange(page.NextPages);
            StatusText.Text = $"{_items.Count} {kind}";
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
        UpdateBusyState();
        StatusText.Text = "Searching your library…";
        _items.Clear();
        _selected = null;
        UpdateActions();
        ActionStatus.Text = "";
        try
        {
            var result = await App.MusicApi.SearchLibraryPageAsync(term);
            _items.Clear();
            foreach (var item in result.Items)
            {
                _items.Add(item);
            }

            _nextPages.AddRange(result.NextPages);
            StatusText.Text = $"{_items.Count} results";
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
                foreach (var item in page.Items)
                {
                    _items.Add(item);
                }

                _nextPages.AddRange(page.NextPages);
            }

            if (_isSearching)
            {
                StatusText.Text = $"{_items.Count} results";
            }
            else
            {
                var selected = KindBox.SelectedItem as ComboBoxItem;
                var kind = selected?.Tag as string ?? "songs";
                StatusText.Text = $"{_items.Count} {kind}";
            }
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
        UpdateLoadMoreButton();
    }

    private void UpdateLoadMoreButton()
    {
        LoadMoreButton.Visibility = _nextPages.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        LoadMoreButton.IsEnabled = !_loading && _nextPages.Count > 0;
    }

    private void OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        _selected = e.AddedItems.OfType<MusicResource>().LastOrDefault()
            ?? LibraryList.SelectedItems.OfType<MusicResource>().LastOrDefault();
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
        if (_selected is null)
        {
            return;
        }

        try
        {
            await App.MusicApi.SetRatingAsync(_selected, value);
            ActionStatus.Text = successMessage;
        }
        catch (Exception exception)
        {
            ActionStatus.Text = exception.Message;
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
        var supportsRating = _selected?.Type is "library-songs" or "library-albums" or "library-playlists" or "library-music-videos";
        LikeButton.IsEnabled = supportsRating;
        DislikeButton.IsEnabled = supportsRating;
        ClearRatingButton.IsEnabled = supportsRating;
    }

    private static bool HasRelatedItems(MusicResource item) => item.Type is
        "library-albums" or "library-artists" or "library-playlists";
}

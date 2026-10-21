using System.Collections.ObjectModel;
using AmNative.Models;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;

namespace AmNative.Pages;

public sealed partial class SearchPage : Page
{
    private readonly ObservableCollection<MusicResource> _items = new();
    private readonly List<Uri> _nextPages = new();
    private MusicResource? _selected;
    private bool _loading;
    private bool _showingChart;
    private string _chartLabel = "Top songs";

    public SearchPage()
    {
        InitializeComponent();
        ResultsList.ItemsSource = _items;
    }

    private async void OnSearchClicked(object sender, RoutedEventArgs e) => await SearchAsync();

    private async void OnBrowseChartClicked(object sender, RoutedEventArgs e) => await LoadChartAsync();

    private async void OnLoadMoreClicked(object sender, RoutedEventArgs e) => await LoadMoreAsync();

    private async void OnQueryKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == Windows.System.VirtualKey.Enter)
        {
            e.Handled = true;
            await SearchAsync();
        }
    }

    private async Task SearchAsync()
    {
        var term = QueryBox.Text.Trim();
        if (term.Length == 0)
        {
            return;
        }

        if (_loading)
        {
            return;
        }

        _loading = true;
        _showingChart = false;
        _nextPages.Clear();
        SetBusy(true);
        _items.Clear();
        _selected = null;
        UpdateActions();
        ActionStatus.Text = "";
        try
        {
            var page = await App.MusicApi.SearchCatalogPageAsync(term);
            foreach (var result in page.Items)
            {
                _items.Add(result);
            }

            _nextPages.AddRange(page.NextPages);
            StatusText.Text = page.Items.Count == 0 ? "No results found." : $"{_items.Count} results";
        }
        catch (Exception exception)
        {
            StatusText.Text = exception.Message;
        }
        finally
        {
            _loading = false;
            SetBusy(false);
        }
    }

    private async Task LoadChartAsync()
    {
        if (_loading)
        {
            return;
        }

        var selected = ChartTypeBox.SelectedItem as ComboBoxItem;
        var chartType = selected?.Tag as string ?? "songs";
        _chartLabel = selected?.Content?.ToString() ?? "Top songs";
        _loading = true;
        _showingChart = true;
        _nextPages.Clear();
        SetBusy(true);
        _items.Clear();
        _selected = null;
        UpdateActions();
        ActionStatus.Text = "";
        StatusText.Text = $"Loading {_chartLabel.ToLowerInvariant()}…";
        try
        {
            var page = await App.MusicApi.GetCatalogChartsPageAsync(chartType);
            foreach (var result in page.Items)
            {
                _items.Add(result);
            }

            _nextPages.AddRange(page.NextPages);
            StatusText.Text = _items.Count == 0 ? $"No {_chartLabel.ToLowerInvariant()} were returned." : $"{_items.Count} {_chartLabel.ToLowerInvariant()}";
        }
        catch (Exception exception)
        {
            StatusText.Text = exception.Message;
        }
        finally
        {
            _loading = false;
            SetBusy(false);
        }
    }

    private async Task LoadMoreAsync()
    {
        if (_loading || _nextPages.Count == 0)
        {
            return;
        }

        _loading = true;
        SetBusy(true);
        StatusText.Text = _showingChart ? $"Loading more {_chartLabel.ToLowerInvariant()}…" : "Loading more results…";
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

            StatusText.Text = _showingChart
                ? $"{_items.Count} {_chartLabel.ToLowerInvariant()}"
                : $"{_items.Count} results";
        }
        catch (Exception exception)
        {
            StatusText.Text = exception.Message;
        }
        finally
        {
            _loading = false;
            SetBusy(false);
        }
    }

    private void SetBusy(bool busy)
    {
        QueryBox.IsEnabled = !busy;
        SearchButton.IsEnabled = !busy;
        ChartTypeBox.IsEnabled = !busy;
        BrowseChartButton.IsEnabled = !busy;
        LoadMoreButton.IsEnabled = !busy && _nextPages.Count > 0;
        LoadMoreButton.Visibility = _nextPages.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        StatusText.Text = busy ? "Searching…" : StatusText.Text;
    }

    private void OnItemClicked(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is MusicResource item)
        {
            _selected = item;
            ActionStatus.Text = "";
            UpdateActions();
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
        if (_selected is null)
        {
            return;
        }

        try
        {
            var playableItem = await App.MusicApi.ResolvePreviewAsync(_selected);
            await App.Player.PlayPreviewAsync(playableItem);
            ActionStatus.Text = "Playing the available preview.";
        }
        catch (Exception exception)
        {
            ActionStatus.Text = exception.Message;
        }
    }

    private async void OnAddClicked(object sender, RoutedEventArgs e)
    {
        if (_selected is null)
        {
            return;
        }

        try
        {
            await App.MusicApi.AddToLibraryAsync(_selected);
            ActionStatus.Text = "Added to your Apple Music library.";
        }
        catch (Exception exception)
        {
            ActionStatus.Text = exception.Message;
        }
    }

    private void OnAddToPlaylistClicked(object sender, RoutedEventArgs e)
    {
        if (_selected is not null)
        {
            Frame.Navigate(typeof(PlaylistsPage), _selected);
        }
    }

    private async void OnFavoriteClicked(object sender, RoutedEventArgs e)
    {
        if (_selected is null)
        {
            return;
        }

        try
        {
            await App.MusicApi.AddToFavoritesAsync(_selected);
            _selected.IsFavorited = true;
            UpdateActions();
            ActionStatus.Text = "Added to favorites.";
        }
        catch (Exception exception)
        {
            ActionStatus.Text = exception.Message;
        }
    }

    private async void OnLikeClicked(object sender, RoutedEventArgs e) => await RateAsync(1, "Marked as liked.");

    private async void OnDislikeClicked(object sender, RoutedEventArgs e) => await RateAsync(-1, "Marked as disliked.");

    private async void OnClearRatingClicked(object sender, RoutedEventArgs e) => await RateAsync(null, "Rating cleared.");

    private async Task RateAsync(int? value, string successMessage)
    {
        if (_selected is null)
        {
            return;
        }

        await RunActionAsync(item => App.MusicApi.SetRatingAsync(item, value), successMessage);
    }

    private async Task RunActionAsync(Func<MusicResource, Task> action, string successMessage)
    {
        if (_selected is null)
        {
            return;
        }

        try
        {
            await action(_selected);
            ActionStatus.Text = successMessage;
        }
        catch (Exception exception)
        {
            ActionStatus.Text = exception.Message;
        }
    }

    private void UpdateActions()
    {
        OpenButton.IsEnabled = _selected is not null && HasRelatedItems(_selected);
        PlayButton.IsEnabled = _selected?.CanPlayPreview == true;
        AddButton.IsEnabled = _selected is not null;
        PlaylistButton.IsEnabled = _selected?.Type is "songs" or "song" or "music-videos" or "music-video";
        var canFavorite = _selected?.CanBeFavorited == true;
        FavoriteButton.Content = _selected?.IsFavorited == true ? "Already favorited" : "Add to favorites";
        FavoriteButton.IsEnabled = canFavorite && _selected?.IsFavorited != true;
        var supportsRating = _selected?.Type is "songs" or "albums" or "playlists" or "music-videos";
        LikeButton.IsEnabled = supportsRating;
        DislikeButton.IsEnabled = supportsRating;
        ClearRatingButton.IsEnabled = supportsRating;
    }

    private static bool HasRelatedItems(MusicResource item) => item.Type is
        "albums" or "album" or "artists" or "artist" or "playlists" or "playlist";
}

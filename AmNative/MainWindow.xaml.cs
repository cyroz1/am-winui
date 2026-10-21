using AmNative.Pages;
using AmNative.Playback;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Windows.System;

namespace AmNative;

public sealed partial class MainWindow : Window
{
    private readonly DispatcherQueueTimer _playbackProgressTimer;
    private bool _isScrubbing;

    public MainWindow()
    {
        InitializeComponent();
        App.Player.Attach(PlaybackElement);
        App.Player.TrackChanged += OnTrackChanged;

        _playbackProgressTimer = PlaybackElement.DispatcherQueue.CreateTimer();
        _playbackProgressTimer.Interval = TimeSpan.FromMilliseconds(400);
        _playbackProgressTimer.IsRepeating = true;
        _playbackProgressTimer.Tick += (_, _) => UpdatePlaybackTimeline();
        _playbackProgressTimer.Start();
        Closed += (_, _) => _playbackProgressTimer.Stop();

        RootNavigation.SelectedItem = RootNavigation.MenuItems[0];
        if (ContentFrame.CurrentSourcePageType != typeof(HomePage))
        {
            ContentFrame.Navigate(typeof(HomePage));
        }
    }

    private void OnNavigationSelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        if (args.SelectedItem is not NavigationViewItem item || item.Tag is not string route)
        {
            return;
        }

        var pageType = route switch
        {
            "Home" => typeof(HomePage),
            "Search" => typeof(SearchPage),
            "Library" => typeof(LibraryPage),
            "Playlists" => typeof(PlaylistsPage),
            "Settings" => typeof(SettingsPage),
            _ => typeof(HomePage)
        };

        if (ContentFrame.CurrentSourcePageType != pageType)
        {
            ContentFrame.Navigate(pageType);
        }
    }

    private void OnTrackChanged(object? sender, TrackChangedEventArgs args)
    {
        NowPlayingTitle.Text = args.Title;
        NowPlayingArtist.Text = args.Artist;
        UpdatePlaybackActions();
        UpdatePlaybackTimeline();
    }

    private void OnTimelineValueChanged(object sender, RangeBaseValueChangedEventArgs args)
    {
        if (_isScrubbing)
        {
            CurrentTimeText.Text = FormatTime(TimeSpan.FromSeconds(args.NewValue));
        }
    }

    private void OnTimelinePointerPressed(object sender, PointerRoutedEventArgs args)
    {
        _isScrubbing = PlaybackTimeline.IsEnabled;
    }

    private void OnTimelinePointerReleased(object sender, PointerRoutedEventArgs args) => CommitTimelineSeek();

    private void OnTimelinePointerCaptureLost(object sender, PointerRoutedEventArgs args) => CommitTimelineSeek();

    private void OnTimelineKeyUp(object sender, KeyRoutedEventArgs args)
    {
        if (PlaybackTimeline.IsEnabled &&
            args.Key is (VirtualKey.Left or VirtualKey.Right or VirtualKey.Home or VirtualKey.End or
                VirtualKey.PageUp or VirtualKey.PageDown))
        {
            SeekToTimelineValue();
        }
    }

    private void CommitTimelineSeek()
    {
        if (!_isScrubbing)
        {
            return;
        }

        _isScrubbing = false;
        SeekToTimelineValue();
        UpdatePlaybackTimeline();
    }

    private void SeekToTimelineValue() => App.Player.Seek(TimeSpan.FromSeconds(PlaybackTimeline.Value));

    private void UpdatePlaybackTimeline()
    {
        var position = App.Player.CurrentPosition;
        var duration = App.Player.CurrentDuration;
        var durationSeconds = Math.Max(0, duration.TotalSeconds);

        PlaybackTimeline.IsEnabled = durationSeconds > 0;
        PlaybackTimeline.Maximum = durationSeconds > 0 ? durationSeconds : 1;
        if (!_isScrubbing)
        {
            PlaybackTimeline.Value = Math.Clamp(position.TotalSeconds, 0, PlaybackTimeline.Maximum);
            CurrentTimeText.Text = FormatTime(position);
        }

        DurationText.Text = FormatTime(duration);
    }

    private static string FormatTime(TimeSpan value)
    {
        var totalSeconds = Math.Max(0, (long)value.TotalSeconds);
        var minutes = totalSeconds / 60;
        var seconds = totalSeconds % 60;
        return totalSeconds >= 3600
            ? $"{minutes / 60}:{minutes % 60:00}:{seconds:00}"
            : $"{minutes}:{seconds:00}";
    }

    private void OnPreviousTrackClicked(object sender, RoutedEventArgs e) => App.Player.SkipPrevious();

    private void OnNextTrackClicked(object sender, RoutedEventArgs e) => App.Player.SkipNext();

    private void OnShuffleClicked(object sender, RoutedEventArgs e)
    {
        App.Player.ToggleShuffle();
        UpdatePlaybackActions();
    }

    private void OnRepeatClicked(object sender, RoutedEventArgs e)
    {
        App.Player.ToggleRepeat();
        UpdatePlaybackActions();
    }

    private async void OnQueueClicked(object sender, RoutedEventArgs e)
    {
        var queue = App.Player.Queue;
        if (queue.Count == 0)
        {
            return;
        }

        var rows = queue
            .Select((item, index) => $"{(index == App.Player.CurrentQueueIndex ? "▶  " : "")} {index + 1}. {item.Name}  ·  {item.ArtistName}")
            .ToArray();
        var list = new ListView
        {
            ItemsSource = rows,
            SelectionMode = ListViewSelectionMode.Single,
            MaxHeight = 520,
            MinWidth = 520
        };
        list.SelectedIndex = Math.Clamp(App.Player.CurrentQueueIndex, 0, queue.Count - 1);

        var dialog = new ContentDialog
        {
            Title = "Up next",
            Content = list,
            PrimaryButtonText = "Play from here",
            CloseButtonText = "Close",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = PlaybackElement.XamlRoot
        };
        dialog.PrimaryButtonClick += (sender, args) =>
        {
            if (list.SelectedIndex < 0 || list.SelectedIndex >= queue.Count)
            {
                args.Cancel = true;
                return;
            }

            _ = App.Player.PlayQueueAsync(queue, queue[list.SelectedIndex]);
        };

        await dialog.ShowAsync();
    }

    private void UpdatePlaybackActions()
    {
        PreviousTrackButton.IsEnabled = App.Player.CanSkipPrevious;
        NextTrackButton.IsEnabled = App.Player.CanSkipNext;
        ShuffleButton.IsEnabled = App.Player.CanShuffle;
        ShuffleButton.IsChecked = App.Player.IsShuffleEnabled;
        RepeatButton.IsEnabled = App.Player.CanRepeat;
        RepeatButton.IsChecked = App.Player.IsRepeatEnabled;
        QueueButton.Content = App.Player.Queue.Count > 0 ? $"Queue ({App.Player.Queue.Count})" : "Queue";
        QueueButton.IsEnabled = App.Player.Queue.Count > 0;
    }
}

using AmNative.Pages;
using AmNative.Playback;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Composition.SystemBackdrops;
using Windows.System;

namespace AmNative;

public sealed partial class MainWindow : Window
{
    private readonly Microsoft.UI.Dispatching.DispatcherQueueTimer _playbackProgressTimer;
    private bool _isScrubbing;

    public MainWindow()
    {
        InitializeComponent();
        SystemBackdrop = new MicaBackdrop { Kind = MicaKind.BaseAlt };
        App.Player.Attach(PlaybackElement);
        App.Player.TrackChanged += OnTrackChanged;
        App.Player.QueueChanged += OnQueueChanged;
        App.Player.PlaybackStateChanged += OnPlayerPlaybackStateChanged;
        VolumeSlider.Value = App.Player.Volume;
        UpdateMuteButton();

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
            "Replay" => typeof(ReplayPage),
            "Playlists" => typeof(PlaylistsPage),
            "Settings" => typeof(SettingsPage),
            _ => typeof(HomePage)
        };

        if (ContentFrame.CurrentSourcePageType != pageType)
        {
            ContentFrame.Navigate(pageType);
        }
    }

    private void OnAppShortcutInvoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        var route = args.KeyboardAccelerator.Key switch
        {
            VirtualKey.Number1 => "Home",
            VirtualKey.Number2 or VirtualKey.F => "Search",
            VirtualKey.Number3 => "Library",
            VirtualKey.Number4 => "Replay",
            VirtualKey.Number5 => "Playlists",
            _ => null
        };
        if (route is null)
        {
            return;
        }

        args.Handled = true;
        var item = RootNavigation.MenuItems
            .OfType<NavigationViewItem>()
            .FirstOrDefault(candidate => string.Equals(candidate.Tag as string, route, StringComparison.Ordinal));
        if (item is null)
        {
            return;
        }

        RootNavigation.SelectedItem = item;
        if (route == "Search")
        {
            RootNavigation.DispatcherQueue.TryEnqueue(() => (ContentFrame.Content as SearchPage)?.FocusSearchBox());
        }
    }

    private void OnTrackChanged(object? sender, TrackChangedEventArgs args)
    {
        NowPlayingTitle.Text = args.Title;
        NowPlayingArtist.Text = args.Artist;
        var artwork = args.Resource?.ArtworkSource;
        NowPlayingArtwork.Source = artwork;
        NowPlayingArtwork.Visibility = artwork is null ? Visibility.Collapsed : Visibility.Visible;
        NowPlayingArtworkPlaceholder.Visibility = artwork is null ? Visibility.Visible : Visibility.Collapsed;
        UpdatePlaybackActions();
        UpdatePlaybackTimeline();
    }

    private void OnNowPlayingArtworkFailed(object sender, ExceptionRoutedEventArgs args)
    {
        NowPlayingArtwork.Visibility = Visibility.Collapsed;
        NowPlayingArtworkPlaceholder.Visibility = Visibility.Visible;
    }

    private void OnPlayerPlaybackStateChanged(object? sender, EventArgs args) => UpdatePlaybackActions();

    private void OnQueueChanged(object? sender, EventArgs args) => UpdatePlaybackActions();

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

    private void OnVolumeValueChanged(object sender, RangeBaseValueChangedEventArgs args)
    {
        App.Player.SetVolume(args.NewValue);
        UpdateMuteButton();
    }

    private void OnMuteClicked(object sender, RoutedEventArgs e)
    {
        App.Player.ToggleMute();
        VolumeSlider.Value = App.Player.Volume;
        UpdateMuteButton();
    }

    private void UpdateMuteButton() => MuteButton.Content = App.Player.IsMuted ? "Unmute" : "Mute";

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

    private void OnPlayPauseClicked(object sender, RoutedEventArgs e) => App.Player.TogglePlayback();

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
        if (App.Player.Queue.Count == 0)
        {
            return;
        }

        var list = new ListView
        {
            SelectionMode = ListViewSelectionMode.Single,
            MaxHeight = 520,
            MinWidth = 520
        };
        var moveUpButton = new Button { Content = "Move up" };
        var moveDownButton = new Button { Content = "Move down" };
        var clearQueueButton = new Button
        {
            Content = "Clear queue",
            HorizontalAlignment = HorizontalAlignment.Right
        };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(clearQueueButton, "Clear playback queue and stop preview playback");
        var queueActions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        queueActions.Children.Add(moveUpButton);
        queueActions.Children.Add(moveDownButton);
        queueActions.Children.Add(clearQueueButton);
        var content = new StackPanel { Spacing = 12 };
        content.Children.Add(list);
        content.Children.Add(queueActions);

        var dialog = new ContentDialog
        {
            Title = "Up next",
            Content = content,
            PrimaryButtonText = "Play from here",
            SecondaryButtonText = "Remove selected",
            CloseButtonText = "Close",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = PlaybackElement.XamlRoot
        };

        void UpdateQueueActionStates()
        {
            var selectedIndex = list.SelectedIndex;
            moveUpButton.IsEnabled = App.Player.CanMoveQueueItem(selectedIndex, selectedIndex - 1);
            moveDownButton.IsEnabled = App.Player.CanMoveQueueItem(selectedIndex, selectedIndex + 1);
        }

        void RefreshQueueRows(int preferredSelection = -1)
        {
            var queue = App.Player.Queue;
            var previousSelection = preferredSelection >= 0 ? preferredSelection : list.SelectedIndex;
            list.ItemsSource = queue
                .Select((item, index) => $"{(index == App.Player.CurrentQueueIndex ? "▶  " : "")} {index + 1}. {item.Name}  ·  {item.ArtistName}")
                .ToArray();
            list.SelectedIndex = queue.Count == 0
                ? -1
                : Math.Clamp(previousSelection < 0 ? App.Player.CurrentQueueIndex : previousSelection, 0, queue.Count - 1);
            dialog.IsPrimaryButtonEnabled = queue.Count > 0;
            dialog.IsSecondaryButtonEnabled = queue.Count > 0;
            UpdateQueueActionStates();
        }

        void OnQueueChanged(object? queueSender, EventArgs queueArgs) => RefreshQueueRows();

        RefreshQueueRows(App.Player.CurrentQueueIndex);
        list.SelectionChanged += (_, _) => UpdateQueueActionStates();
        dialog.PrimaryButtonClick += (sender, args) =>
        {
            var queue = App.Player.Queue;
            if (list.SelectedIndex < 0 || list.SelectedIndex >= queue.Count)
            {
                args.Cancel = true;
                return;
            }

            _ = App.Player.PlayQueueAsync(queue, queue[list.SelectedIndex]);
        };
        dialog.SecondaryButtonClick += (sender, args) =>
        {
            if (list.SelectedIndex < 0 || list.SelectedIndex >= App.Player.Queue.Count)
            {
                args.Cancel = true;
                return;
            }

            args.Cancel = true;
            var selectedIndex = list.SelectedIndex;
            App.Player.RemoveQueueItem(selectedIndex);
            RefreshQueueRows(selectedIndex);
        };
        moveUpButton.Click += (_, _) =>
        {
            var index = list.SelectedIndex;
            App.Player.MoveQueueItem(index, index - 1);
            RefreshQueueRows(index - 1);
        };
        moveDownButton.Click += (_, _) =>
        {
            var index = list.SelectedIndex;
            App.Player.MoveQueueItem(index, index + 1);
            RefreshQueueRows(index + 1);
        };
        clearQueueButton.Click += (_, _) =>
        {
            App.Player.ClearQueue();
            dialog.Hide();
        };

        App.Player.QueueChanged += OnQueueChanged;
        try
        {
            await dialog.ShowAsync();
        }
        finally
        {
            App.Player.QueueChanged -= OnQueueChanged;
        }
    }

    private void UpdatePlaybackActions()
    {
        PreviousTrackButton.IsEnabled = App.Player.CanSkipPrevious;
        PlayPauseButton.Content = App.Player.IsPlaying ? "Pause" : "Play";
        PlayPauseButton.IsEnabled = App.Player.Queue.Count > 0;
        NextTrackButton.IsEnabled = App.Player.CanSkipNext;
        ShuffleButton.IsEnabled = App.Player.CanShuffle;
        ShuffleButton.IsChecked = App.Player.IsShuffleEnabled;
        RepeatButton.IsEnabled = App.Player.CanRepeat;
        RepeatButton.IsChecked = App.Player.IsRepeatEnabled;
        QueueButton.Content = App.Player.Queue.Count > 0 ? $"Queue ({App.Player.Queue.Count})" : "Queue";
        QueueButton.IsEnabled = App.Player.Queue.Count > 0;
    }
}

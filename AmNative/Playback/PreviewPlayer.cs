using AmNative.Models;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml.Controls;
using Windows.Media;
using Windows.Media.Core;
using Windows.Media.Playback;
using Windows.Storage.Streams;

namespace AmNative.Playback;

public sealed class PreviewPlayer
{
    private MediaPlayer? _mediaPlayer;
    private MediaPlayerElement? _element;
    private DispatcherQueue? _dispatcherQueue;
    private MediaPlaybackList? _playbackList;
    private IReadOnlyList<MusicResource> _queue = Array.Empty<MusicResource>();
    private int _currentQueueIndex = -1;
    private bool _shuffleEnabled;
    private bool _repeatEnabled;
    private double _lastNonZeroVolume = 0.5;

    public MediaPlayer MediaPlayer => _mediaPlayer ??= _element?.MediaPlayer ?? new MediaPlayer();
    public bool CanSkipPrevious => _queue.Count > 1 &&
                                   (_currentQueueIndex > 0 || _shuffleEnabled || _repeatEnabled);
    public bool CanSkipNext => _queue.Count > 1 &&
                               (_currentQueueIndex < _queue.Count - 1 || _shuffleEnabled || _repeatEnabled);
    public bool CanShuffle => _queue.Count > 1;
    public bool CanRepeat => _queue.Count > 0;
    public bool IsShuffleEnabled => _shuffleEnabled;
    public bool IsRepeatEnabled => _repeatEnabled;
    public IReadOnlyList<MusicResource> Queue => _queue;
    public int CurrentQueueIndex => _currentQueueIndex;
    public TimeSpan CurrentPosition => MediaPlayer.PlaybackSession.Position;
    public TimeSpan CurrentDuration => MediaPlayer.PlaybackSession.NaturalDuration;
    public double Volume => MediaPlayer.Volume;
    public bool IsMuted => Volume <= 0;

    public event EventHandler<TrackChangedEventArgs>? TrackChanged;

    public void Attach(MediaPlayerElement element)
    {
        _element = element;
        _dispatcherQueue = element.DispatcherQueue;
        _mediaPlayer = element.MediaPlayer;
        var controls = MediaPlayer.SystemMediaTransportControls;
        controls.IsEnabled = true;
        controls.IsPlayEnabled = true;
        controls.IsPauseEnabled = true;
        controls.IsStopEnabled = true;
        UpdateTransportNavigation();
        controls.ButtonPressed += OnTransportButtonPressed;
        MediaPlayer.PlaybackSession.PlaybackStateChanged += OnPlaybackStateChanged;
    }

    public Task PlayPreviewAsync(MusicResource item) => PlayQueueAsync(new[] { item }, item);

    public Task PlayQueueAsync(IEnumerable<MusicResource> items, MusicResource? startAt = null)
    {
        var queue = items
            .Where(static item => TryGetPreviewUri(item, out _))
            .ToArray();
        if (queue.Length == 0)
        {
            throw new InvalidOperationException("Apple Music has no playable previews in this selection.");
        }

        var startIndex = startAt is null
            ? 0
            : Array.FindIndex(queue, item => ReferenceEquals(item, startAt) ||
                (item.Id == startAt.Id && item.Type == startAt.Type));
        if (startIndex < 0)
        {
            startIndex = 0;
        }

        var playbackList = new MediaPlaybackList();
        playbackList.ShuffleEnabled = _shuffleEnabled;
        playbackList.AutoRepeatEnabled = _repeatEnabled;
        foreach (var item in queue)
        {
            if (TryGetPreviewUri(item, out var uri))
            {
                var playbackItem = new MediaPlaybackItem(MediaSource.CreateFromUri(uri));
                var displayProperties = playbackItem.GetDisplayProperties();
                displayProperties.Type = MediaPlaybackType.Music;
                displayProperties.MusicProperties.Title = item.Name;
                displayProperties.MusicProperties.Artist = item.ArtistName;
                var artworkUrl = item.ArtworkUrl?.Replace("{w}", "512", StringComparison.Ordinal)
                    .Replace("{h}", "512", StringComparison.Ordinal);
                if (Uri.TryCreate(artworkUrl, UriKind.Absolute, out var artworkUri) && artworkUri.Scheme == Uri.UriSchemeHttps)
                {
                    displayProperties.Thumbnail = RandomAccessStreamReference.CreateFromUri(artworkUri);
                }

                playbackItem.ApplyDisplayProperties(displayProperties);
                playbackList.Items.Add(playbackItem);
            }
        }

        playbackList.StartingItem = playbackList.Items.ElementAt(startIndex);
        if (_playbackList is not null)
        {
            _playbackList.CurrentItemChanged -= OnCurrentItemChanged;
        }

        _queue = queue;
        _currentQueueIndex = startIndex;
        _playbackList = playbackList;
        playbackList.CurrentItemChanged += OnCurrentItemChanged;
        if (_element is not null)
        {
            _element.Source = playbackList;
        }
        else
        {
            MediaPlayer.Source = playbackList;
        }

        UpdateTransportNavigation();

        MediaPlayer.Play();
        TrackChanged?.Invoke(this, new TrackChangedEventArgs(queue[startIndex].Name, queue[startIndex].ArtistName));
        return Task.CompletedTask;
    }

    public void SkipPrevious()
    {
        if (CanSkipPrevious)
        {
            _playbackList?.MovePrevious();
        }
    }

    public void SkipNext()
    {
        if (CanSkipNext)
        {
            _playbackList?.MoveNext();
        }
    }

    public void Seek(TimeSpan position)
    {
        var session = MediaPlayer.PlaybackSession;
        var duration = session.NaturalDuration;
        if (duration <= TimeSpan.Zero)
        {
            return;
        }

        var clampedTicks = Math.Clamp(position.Ticks, 0, duration.Ticks);
        session.Position = TimeSpan.FromTicks(clampedTicks);
    }

    public void SetVolume(double volume)
    {
        var clampedVolume = Math.Clamp(volume, 0, 1);
        MediaPlayer.Volume = clampedVolume;
        if (clampedVolume > 0)
        {
            _lastNonZeroVolume = clampedVolume;
        }
    }

    public void ToggleMute()
    {
        if (IsMuted)
        {
            SetVolume(_lastNonZeroVolume);
            return;
        }

        _lastNonZeroVolume = Volume;
        MediaPlayer.Volume = 0;
    }

    public void ToggleShuffle()
    {
        if (_playbackList is null || !CanShuffle)
        {
            return;
        }

        _shuffleEnabled = !_shuffleEnabled;
        _playbackList.ShuffleEnabled = _shuffleEnabled;
        UpdateTransportNavigation();
    }

    public void ToggleRepeat()
    {
        if (_playbackList is null || !CanRepeat)
        {
            return;
        }

        _repeatEnabled = !_repeatEnabled;
        _playbackList.AutoRepeatEnabled = _repeatEnabled;
        UpdateTransportNavigation();
    }

    private void OnCurrentItemChanged(MediaPlaybackList sender, CurrentMediaPlaybackItemChangedEventArgs args)
    {
        var index = unchecked((int)sender.CurrentItemIndex);
        var queue = _queue;
        var dispatcherQueue = _dispatcherQueue;
        if (index < 0 || index >= queue.Count || dispatcherQueue is null)
        {
            return;
        }

        var item = queue[index];
        dispatcherQueue.TryEnqueue(() =>
        {
            if (!ReferenceEquals(_playbackList, sender))
            {
                return;
            }

            _currentQueueIndex = index;
            UpdateTransportNavigation();
            TrackChanged?.Invoke(this, new TrackChangedEventArgs(item.Name, item.ArtistName));
        });
    }

    private void UpdateTransportNavigation()
    {
        var controls = MediaPlayer.SystemMediaTransportControls;
        controls.IsPreviousEnabled = CanSkipPrevious;
        controls.IsNextEnabled = CanSkipNext;
    }

    private static bool TryGetPreviewUri(MusicResource item, out Uri uri)
    {
        if (Uri.TryCreate(item.PreviewUrl, UriKind.Absolute, out var parsedUri) && parsedUri.Scheme == Uri.UriSchemeHttps)
        {
            uri = parsedUri;
            return true;
        }

        uri = null!;
        return false;
    }

    private void OnTransportButtonPressed(SystemMediaTransportControls sender, SystemMediaTransportControlsButtonPressedEventArgs args)
    {
        var dispatcherQueue = _dispatcherQueue;
        if (dispatcherQueue is null)
        {
            return;
        }

        var button = args.Button;
        dispatcherQueue.TryEnqueue(() =>
        {
            switch (button)
            {
                case SystemMediaTransportControlsButton.Play:
                    MediaPlayer.Play();
                    break;
                case SystemMediaTransportControlsButton.Pause:
                case SystemMediaTransportControlsButton.Stop:
                    MediaPlayer.Pause();
                    break;
                case SystemMediaTransportControlsButton.Previous:
                    SkipPrevious();
                    break;
                case SystemMediaTransportControlsButton.Next:
                    SkipNext();
                    break;
            }
        });
    }

    private void OnPlaybackStateChanged(MediaPlaybackSession sender, object args)
    {
        var dispatcherQueue = _dispatcherQueue;
        if (dispatcherQueue is null)
        {
            return;
        }

        var state = sender.PlaybackState;
        dispatcherQueue.TryEnqueue(() =>
        {
            MediaPlayer.SystemMediaTransportControls.PlaybackStatus = state switch
            {
                MediaPlaybackState.Playing => MediaPlaybackStatus.Playing,
                MediaPlaybackState.Paused => MediaPlaybackStatus.Paused,
                MediaPlaybackState.Opening or MediaPlaybackState.Buffering => MediaPlaybackStatus.Changing,
                _ => MediaPlaybackStatus.Stopped
            };
        });
    }
}

public sealed class TrackChangedEventArgs(string title, string artist) : EventArgs
{
    public string Title { get; } = title;
    public string Artist { get; } = artist;
}

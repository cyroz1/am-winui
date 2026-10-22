using AmNative.Models;
using AmNative.Services;
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
    private readonly CredentialStore? _credentials;
    private readonly AppleMusicApiClient? _api;
    private Func<WebView2>? _musicKitViewFactory;
    private Action<bool, bool>? _setMusicKitPresentation;
    private MusicKitWebPlayer? _musicKit;
    private IReadOnlyList<MusicResource> _queue = Array.Empty<MusicResource>();
    private MusicKitStateChangedEventArgs? _musicKitState;
    private int _currentQueueIndex = -1;
    private bool _shuffleEnabled;
    private bool _repeatEnabled;
    private bool _isMusicKitActive;
    private double _volume = 0.5;
    private double _lastNonZeroVolume = 0.5;
    private string _transportTrackIdentity = "";

    public PreviewPlayer()
    {
    }

    public PreviewPlayer(CredentialStore credentials, AppleMusicApiClient api)
    {
        _credentials = credentials;
        _api = api;
    }

    public MediaPlayer MediaPlayer => _mediaPlayer ??= _element?.MediaPlayer ?? new MediaPlayer();
    public bool CanSkipPrevious => _queue.Count > 1 &&
                                   (_currentQueueIndex > 0 || _shuffleEnabled || _repeatEnabled);
    public bool CanSkipNext => _queue.Count > 1 &&
                               (_currentQueueIndex < _queue.Count - 1 || _shuffleEnabled || _repeatEnabled);
    public bool CanShuffle => _queue.Count > 1;
    public bool CanRepeat => _queue.Count > 0;
    public bool IsShuffleEnabled => _shuffleEnabled;
    public bool IsRepeatEnabled => _repeatEnabled;
    public bool IsPlaying => _isMusicKitActive
        ? _musicKitState?.IsPlaying == true
        : MediaPlayer.PlaybackSession.PlaybackState == MediaPlaybackState.Playing;
    public bool IsMusicKitActive => _isMusicKitActive;
    public bool IsMusicKitAuthorized => _musicKit?.IsAuthorized == true;
    public IReadOnlyList<MusicResource> Queue => _queue;
    public MusicResource? CurrentItem => _currentQueueIndex >= 0 && _currentQueueIndex < _queue.Count
        ? _queue[_currentQueueIndex]
        : null;
    public int CurrentQueueIndex => _currentQueueIndex;
    public TimeSpan CurrentPosition => _isMusicKitActive
        ? _musicKitState?.Position ?? TimeSpan.Zero
        : MediaPlayer.PlaybackSession.Position;
    public TimeSpan CurrentDuration
    {
        get
        {
            if (!_isMusicKitActive)
            {
                return MediaPlayer.PlaybackSession.NaturalDuration;
            }

            if (_musicKitState?.Duration is { } duration && duration > TimeSpan.Zero)
            {
                return duration;
            }

            return CurrentItem?.DurationInMillis is int durationInMillis && durationInMillis > 0
                ? TimeSpan.FromMilliseconds(durationInMillis)
                : TimeSpan.Zero;
        }
    }
    public double Volume => _isMusicKitActive
        ? _musicKitState?.Volume ?? _volume
        : MediaPlayer.Volume;
    public bool IsMuted => Volume <= 0;

    public bool CanMoveQueueItem(int index, int destinationIndex) =>
        !_isMusicKitActive &&
        _playbackList is not null &&
        !_shuffleEnabled &&
        index > _currentQueueIndex &&
        destinationIndex > _currentQueueIndex &&
        index < _queue.Count &&
        destinationIndex < _queue.Count &&
        index != destinationIndex;

    public event EventHandler<TrackChangedEventArgs>? TrackChanged;
    public event EventHandler? PlaybackStateChanged;
    public event EventHandler? QueueChanged;
    public event EventHandler<PlaybackFailedEventArgs>? PlaybackFailed;
    public event EventHandler? MusicKitAuthorizationChanged;

    public void Attach(MediaPlayerElement element)
    {
        Attach(element, null, static (_, _) => { });
    }

    public void Attach(
        MediaPlayerElement element,
        Func<WebView2>? musicKitViewFactory,
        Action<bool, bool> setMusicKitPresentation)
    {
        _element = element;
        _dispatcherQueue = element.DispatcherQueue;
        _mediaPlayer = element.MediaPlayer;
        _volume = _mediaPlayer.Volume;
        _lastNonZeroVolume = _volume > 0 ? _volume : 0.5;
        var controls = MediaPlayer.SystemMediaTransportControls;
        controls.IsEnabled = true;
        controls.IsPlayEnabled = true;
        controls.IsPauseEnabled = true;
        controls.IsStopEnabled = true;
        controls.PlaybackPositionChangeRequested += OnTransportPositionChangeRequested;
        UpdateTransportNavigation();
        controls.ButtonPressed += OnTransportButtonPressed;
        MediaPlayer.PlaybackSession.PlaybackStateChanged += OnPlaybackStateChanged;
        MediaPlayer.MediaFailed += OnMediaFailed;

        _musicKitViewFactory = musicKitViewFactory;
        _setMusicKitPresentation = setMusicKitPresentation;
    }

    public async Task PlayQueueAsync(IEnumerable<MusicResource> items, MusicResource? startAt = null)
    {
        var sourceQueue = items.ToArray();
        var sourceStartIndex = FindStartIndex(sourceQueue, startAt);
        var requested = await PreparePlaybackItemsAsync(sourceQueue);
        var startIndex = MapResolvedQueueIndex(sourceQueue, requested, sourceStartIndex);
        await PlayResolvedQueueAsync(requested, startIndex);
    }

    private async Task<IReadOnlyList<MusicResource>> PreparePlaybackItemsAsync(
        IEnumerable<MusicResource> items)
    {
        var requested = items.ToArray();
        if (_api is null)
        {
            return requested.Where(static item => item.CanStartPlayback).ToArray();
        }

        if (requested.All(item => item.HasPlayablePreview ||
            (_credentials?.HasDeveloperToken == true && item.CanPlayInMusicKit)))
        {
            return requested;
        }

        return await _api.ResolvePlaybackItemsAsync(requested);
    }

    private async Task PlayResolvedQueueAsync(
        IReadOnlyList<MusicResource> requested,
        int startIndex)
    {
        if (requested.Count == 0)
        {
            throw new InvalidOperationException("This selection has no Apple Music tracks available for playback.");
        }

        if (CanStartMusicKitQueue(requested))
        {
            await StartMusicKitQueueAsync(requested, Math.Clamp(startIndex, 0, requested.Count - 1));
            return;
        }

        var queue = requested.Where(static item => TryGetPreviewUri(item, out _)).ToArray();
        if (queue.Length == 0)
        {
            if (_credentials?.HasDeveloperToken != true)
            {
                throw new InvalidOperationException(
                    "Add an Apple Music developer token in Settings and authorize MusicKit to play full tracks.");
            }

            throw new InvalidOperationException(
                "Apple Music could not prepare this selection for playback. Check that its tracks are available in your storefront.");
        }

        if (queue.Length != requested.Count)
        {
            if (_credentials?.HasDeveloperToken != true)
            {
                throw new InvalidOperationException(
                    "This selection includes full Apple Music tracks. Add a developer token and authorize Apple Music to play every item.");
            }

            throw new InvalidOperationException(
                "This selection mixes full-track and preview-only items. Play those selections separately.");
        }

        SetQueue(queue, MapResolvedQueueIndex(requested, queue, startIndex), autoplay: true);
    }

    private bool CanStartMusicKitQueue(IReadOnlyList<MusicResource> queue) =>
        queue.Count > 0 &&
        queue.All(static item => item.CanPlayInMusicKit) &&
        _credentials?.HasDeveloperToken == true &&
        GetOrCreateMusicKitPlayer() is not null;

    public void RestoreQueue(
        IEnumerable<MusicResource> items,
        int startIndex,
        bool shuffleEnabled,
        bool repeatEnabled,
        double volume)
    {
        var queue = items.Where(static item => item.CanStartPlayback).ToArray();
        _shuffleEnabled = shuffleEnabled;
        _repeatEnabled = repeatEnabled;
        SetVolume(double.IsFinite(volume) ? volume : 0.5);
        if (queue.Length == 0)
        {
            return;
        }

        var restoredIndex = Math.Clamp(startIndex, 0, queue.Length - 1);
        var canResolveMusicKitQueue = _credentials?.HasDeveloperToken == true &&
                                      queue.All(static item => item.CanPlayInMusicKit ||
                                          item.Type is "library-songs" or "library-music-videos");
        if (!canResolveMusicKitQueue && queue.All(static item => TryGetPreviewUri(item, out _)))
        {
            SetQueue(queue, restoredIndex, autoplay: false);
            return;
        }

        if (_playbackList is not null)
        {
            _playbackList.CurrentItemChanged -= OnCurrentItemChanged;
        }

        _playbackList = null;
        _isMusicKitActive = false;
        _musicKitState = null;
        _queue = queue;
        _currentQueueIndex = restoredIndex;
        MediaPlayer.Pause();
        if (_element is not null)
        {
            _element.Source = null;
        }
        else
        {
            MediaPlayer.Source = null;
        }

        UpdateTransportNavigation();
        QueueChanged?.Invoke(this, EventArgs.Empty);
        TrackChanged?.Invoke(this, new TrackChangedEventArgs(queue[restoredIndex]));
    }

    private void SetQueue(IReadOnlyList<MusicResource> queue, int startIndex, bool autoplay)
    {
        if (_isMusicKitActive)
        {
            _ = _musicKit?.StopAsync();
        }

        _isMusicKitActive = false;
        _musicKitState = null;
        MediaPlayer.Volume = _volume;
        var playbackList = new MediaPlaybackList();
        playbackList.ShuffleEnabled = _shuffleEnabled;
        playbackList.AutoRepeatEnabled = _repeatEnabled;
        foreach (var item in queue)
        {
            playbackList.Items.Add(CreatePlaybackItem(item));
        }

        playbackList.StartingItem = playbackList.Items.ElementAt(startIndex);
        if (_playbackList is not null)
        {
            _playbackList.CurrentItemChanged -= OnCurrentItemChanged;
        }

        _queue = queue;
        _currentQueueIndex = startIndex;
        _playbackList = playbackList;
        QueueChanged?.Invoke(this, EventArgs.Empty);
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

        if (autoplay)
        {
            MediaPlayer.Play();
        }

        TrackChanged?.Invoke(this, new TrackChangedEventArgs(queue[startIndex]));
    }

    public async Task EnqueuePlaybackItemsAsync(IEnumerable<MusicResource> items)
    {
        IReadOnlyList<MusicResource> queue = await PreparePlaybackItemsAsync(items);
        if (queue.Count == 0)
        {
            throw new InvalidOperationException("Apple Music did not provide playable items for this selection.");
        }

        if (_isMusicKitActive)
        {
            if (queue.Any(static item => !item.CanPlayInMusicKit))
            {
                throw new InvalidOperationException("Preview-only items cannot be added to an active full-track queue.");
            }

            await _musicKit!.AppendQueueItemsAsync(queue);
            _queue = _queue.Concat(queue).ToArray();
            UpdateTransportNavigation();
            QueueChanged?.Invoke(this, EventArgs.Empty);
            return;
        }

        if (_playbackList is null && _queue.Count > 0)
        {
            _queue = _queue.Concat(queue).ToArray();
            UpdateTransportNavigation();
            QueueChanged?.Invoke(this, EventArgs.Empty);
            return;
        }

        if (_playbackList is null && _queue.Count == 0)
        {
            await PlayResolvedQueueAsync(queue, startIndex: 0);
            return;
        }

        var canMigrateToMusicKit = _playbackList is not null &&
                                   _queue.Count > 0 &&
                                   _credentials?.HasDeveloperToken == true &&
                                   _queue.All(static item => item.CanPlayInMusicKit) &&
                                   queue.All(static item => item.CanPlayInMusicKit);
        if (canMigrateToMusicKit)
        {
            var musicKit = GetOrCreateMusicKitPlayer();
            var isAuthorized = false;
            Exception? authorizationError = null;
            if (musicKit is not null)
            {
                try
                {
                    isAuthorized = await musicKit.EnsureAuthorizationStateAsync();
                }
                catch (Exception exception)
                {
                    authorizationError = exception;
                }
            }

            if (isAuthorized)
            {
                var combinedQueue = _queue.Concat(queue).ToArray();
                var startIndex = Math.Clamp(_currentQueueIndex, 0, _queue.Count - 1);
                var positionSeconds = MediaPlayer.PlaybackSession.Position.TotalSeconds;
                var wasPlaying = IsPlaying;
                await StartMusicKitQueueAsync(combinedQueue, startIndex, positionSeconds, wasPlaying);
                if (!_isMusicKitActive)
                {
                    throw new InvalidOperationException(
                        "The full-track items could not be added while preview playback was active.");
                }

                return;
            }

            if (queue.Any(static item => !item.HasPlayablePreview) ||
                _queue.Any(static item => !item.HasPlayablePreview))
            {
                throw new InvalidOperationException(
                    "Authorize Apple Music playback before upgrading this preview queue to full-track playback.",
                    authorizationError);
            }
        }

        var previewQueue = queue.Where(static item => TryGetPreviewUri(item, out _)).ToArray();
        if (previewQueue.Length == 0)
        {
            throw new InvalidOperationException("Apple Music did not provide playable previews for this selection.");
        }

        if (previewQueue.Length != queue.Count)
        {
            if (_credentials?.HasDeveloperToken != true)
            {
                throw new InvalidOperationException(
                    "Full-track items need a developer token and Apple Music authorization before they can be queued.");
            }

            throw new InvalidOperationException(
                "Full-track items cannot be mixed into this preview queue. Start a MusicKit queue or add preview-capable items.");
        }

        queue = previewQueue;

        if (_playbackList is null || _queue.Count == 0)
        {
            await PlayQueueAsync(queue);
            return;
        }

        foreach (var item in queue)
        {
            _playbackList.Items.Add(CreatePlaybackItem(item));
        }

        _queue = _queue.Concat(queue).ToArray();
        UpdateTransportNavigation();
        QueueChanged?.Invoke(this, EventArgs.Empty);
    }

    public void RemoveQueueItem(int index)
    {
        if (index < 0 || index >= _queue.Count)
        {
            return;
        }

        if (_isMusicKitActive)
        {
            var wasPlaying = _musicKitState?.IsPlaying == true;
            var positionSeconds = _musicKitState?.Position.TotalSeconds ?? 0;
            var originalIndex = _currentQueueIndex;
            var remainingMusicKitItems = _queue.Where((_, itemIndex) => itemIndex != index).ToArray();
            if (remainingMusicKitItems.Length == 0)
            {
                ClearQueue();
                return;
            }

            _queue = remainingMusicKitItems;
            _currentQueueIndex = index == originalIndex
                ? Math.Min(index, remainingMusicKitItems.Length - 1)
                : index < originalIndex ? originalIndex - 1 : originalIndex;
            if (index == originalIndex)
            {
                TrackChanged?.Invoke(this, new TrackChangedEventArgs(CurrentItem));
            }

            UpdateTransportNavigation();
            QueueChanged?.Invoke(this, EventArgs.Empty);
            _ = ReplaceMusicKitQueueAsync(
                remainingMusicKitItems,
                _currentQueueIndex,
                index == originalIndex ? 0 : positionSeconds,
                wasPlaying);
            return;
        }

        if (_playbackList is null)
        {
            return;
        }

        var queue = _queue;
        if (index == _currentQueueIndex)
        {
            var remaining = queue.Where((_, itemIndex) => itemIndex != index).ToArray();
            if (remaining.Length == 0)
            {
                ClearQueue();
                return;
            }

            var nextIndex = Math.Min(index, remaining.Length - 1);
            _ = PlayQueueAsync(remaining, remaining[nextIndex]);
            return;
        }

        _playbackList.Items.RemoveAt(index);
        _queue = queue.Where((_, itemIndex) => itemIndex != index).ToArray();
        if (index < _currentQueueIndex)
        {
            _currentQueueIndex--;
        }

        UpdateTransportNavigation();
        QueueChanged?.Invoke(this, EventArgs.Empty);
    }

    public Task RequestMusicKitAuthorizationAsync() => GetOrCreateMusicKitPlayer() is { } player
        ? player.RequestAuthorizationAsync()
        : Task.FromException(new InvalidOperationException("The Apple Music playback view is not ready."));

    public void ResetMusicKitConfiguration()
    {
        _musicKit?.ResetConfiguration();
        _isMusicKitActive = false;
        _musicKitState = null;
        _transportTrackIdentity = "";
        PlaybackStateChanged?.Invoke(this, EventArgs.Empty);
        UpdateTransportNavigation();
    }

    public async Task ForgetMusicKitAuthorizationAsync()
    {
        try
        {
            if (GetOrCreateMusicKitPlayer() is { } player)
            {
                await player.ForgetAuthorizationAsync();
            }
        }
        finally
        {
            ResetMusicKitConfiguration();
        }
    }

    private async Task StartMusicKitQueueAsync(
        IReadOnlyList<MusicResource> queue,
        int startIndex,
        double positionSeconds = 0,
        bool isPlaying = true)
    {
        var musicKit = GetOrCreateMusicKitPlayer();
        if (musicKit is null)
        {
            throw new InvalidOperationException("The Apple Music playback view is not ready.");
        }

        if (_playbackList is not null)
        {
            _playbackList.CurrentItemChanged -= OnCurrentItemChanged;
        }

        _playbackList = null;
        MediaPlayer.Pause();
        if (_element is not null)
        {
            _element.Source = null;
        }
        else
        {
            MediaPlayer.Source = null;
        }

        _isMusicKitActive = true;
        _musicKitState = null;
        _queue = queue;
        _currentQueueIndex = startIndex;
        UpdateTransportNavigation();
        QueueChanged?.Invoke(this, EventArgs.Empty);
        TrackChanged?.Invoke(this, new TrackChangedEventArgs(queue[startIndex]));

        try
        {
            var accepted = await musicKit.TryPlayQueueAsync(
                queue,
                startIndex,
                _volume,
                _shuffleEnabled,
                _repeatEnabled,
                positionSeconds,
                isPlaying);
            if (!accepted)
            {
                throw new InvalidOperationException("MusicKit cannot play one or more items in this selection.");
            }
        }
        catch (Exception exception)
        {
            if (musicKit.RequiresUserGesture)
            {
                return;
            }

            _isMusicKitActive = false;
            _musicKitState = null;
            musicKit.ResetConfiguration();
            var resolvedDetails = Array.Empty<MusicResource>();
            var itemsNeedingPreviews = queue
                .Where(static item => !TryGetPreviewUri(item, out _))
                .ToArray();
            if (_api is not null && itemsNeedingPreviews.Length > 0)
            {
                try
                {
                    resolvedDetails = (await _api.ResolvePreviewsForPlaybackAsync(itemsNeedingPreviews)).ToArray();
                }
                catch
                {
                    // Keep the original MusicKit error if preview lookup is unavailable.
                }
            }

            var detailsById = resolvedDetails
                .GroupBy(static item => (item.Type, item.Id))
                .ToDictionary(static group => group.Key, static group => group.First());
            var previews = queue
                .Select(item => detailsById.TryGetValue((item.Type, item.Id), out var resolved) ? resolved : item)
                .Where(static item => TryGetPreviewUri(item, out _))
                .ToArray();
            if (previews.Length == queue.Count)
            {
                SetQueue(previews, startIndex, autoplay: true);
            }

            PlaybackFailed?.Invoke(this, new PlaybackFailedEventArgs(exception.Message));
            if (previews.Length != queue.Count)
            {
                throw;
            }
        }
    }

    private void OnMusicKitStateChanged(object? sender, MusicKitStateChangedEventArgs args)
    {
        if (!_isMusicKitActive)
        {
            return;
        }

        _musicKitState = args;
        _volume = args.Volume;
        if (_volume > 0)
        {
            _lastNonZeroVolume = _volume;
        }

        _shuffleEnabled = args.Shuffle;
        _repeatEnabled = args.RepeatMode == 2;

        var previousIndex = _currentQueueIndex;
        var itemIndex = -1;
        if (args.QueuePosition >= 0 && args.QueuePosition < _queue.Count)
        {
            var positionedItem = _queue[args.QueuePosition];
            if (string.Equals(positionedItem.Id, args.Id, StringComparison.Ordinal) ||
                string.Equals(positionedItem.PlayParametersId, args.Id, StringComparison.Ordinal) ||
                string.Equals(positionedItem.CatalogId, args.Id, StringComparison.Ordinal))
            {
                itemIndex = args.QueuePosition;
            }
        }

        if (itemIndex < 0)
        {
            for (var index = 0; index < _queue.Count; index++)
            {
                var item = _queue[index];
                if (string.Equals(item.Id, args.Id, StringComparison.Ordinal) ||
                    string.Equals(item.PlayParametersId, args.Id, StringComparison.Ordinal) ||
                    string.Equals(item.CatalogId, args.Id, StringComparison.Ordinal))
                {
                    itemIndex = index;
                    break;
                }
            }
        }

        if (itemIndex < 0 && args.QueuePosition >= 0 && args.QueuePosition < _queue.Count)
        {
            itemIndex = args.QueuePosition;
        }

        if (itemIndex >= 0)
        {
            _currentQueueIndex = itemIndex;
        }

        UpdateTransportNavigation();
        if (_currentQueueIndex != previousIndex && CurrentItem is { } currentItem)
        {
            TrackChanged?.Invoke(this, new TrackChangedEventArgs(currentItem));
            QueueChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    private void OnMusicKitAuthorizationDismissed(object? sender, EventArgs args)
    {
        if (!_isMusicKitActive || _musicKit?.IsAuthorized == true)
        {
            return;
        }

        var currentItem = CurrentItem;
        _isMusicKitActive = false;
        _musicKitState = null;
        var previews = _queue.Where(static item => TryGetPreviewUri(item, out _)).ToArray();
        if (previews.Length == _queue.Count)
        {
            SetQueue(previews, FindStartIndex(previews, currentItem), autoplay: true);
            return;
        }

        PlaybackStateChanged?.Invoke(this, EventArgs.Empty);
        UpdateTransportNavigation();
    }

    private async Task ReplaceMusicKitQueueAsync(
        IReadOnlyList<MusicResource> items,
        int startIndex,
        double positionSeconds,
        bool isPlaying)
    {
        try
        {
            if (_musicKit is not null)
            {
                await _musicKit.ReplaceQueueItemsAsync(
                    items,
                    startIndex,
                    positionSeconds,
                    isPlaying,
                    _volume,
                    _shuffleEnabled,
                    _repeatEnabled);
            }
        }
        catch (Exception exception)
        {
            PlaybackFailed?.Invoke(this, new PlaybackFailedEventArgs(exception.Message));
        }
    }

    private async Task RunMusicKitCommandAsync(Func<MusicKitWebPlayer, Task> command)
    {
        try
        {
            if (_musicKit is not null)
            {
                await command(_musicKit);
            }
        }
        catch (Exception exception)
        {
            if (_musicKit?.RequiresUserGesture != true)
            {
                PlaybackFailed?.Invoke(this, new PlaybackFailedEventArgs(exception.Message));
            }
        }
    }

    private static int FindStartIndex(IReadOnlyList<MusicResource> queue, MusicResource? startAt)
    {
        if (startAt is null)
        {
            return 0;
        }

        for (var index = 0; index < queue.Count; index++)
        {
            if (ReferenceEquals(queue[index], startAt) ||
                (queue[index].Id == startAt.Id && queue[index].Type == startAt.Type))
            {
                return index;
            }
        }

        return 0;
    }

    private static int MapResolvedQueueIndex(
        IReadOnlyList<MusicResource> sourceQueue,
        IReadOnlyList<MusicResource> resolvedQueue,
        int sourceIndex)
    {
        if (resolvedQueue.Count == 0 || sourceIndex < 0 || sourceIndex >= sourceQueue.Count)
        {
            return 0;
        }

        var selected = sourceQueue[sourceIndex];
        var occurrence = 0;
        for (var index = 0; index < sourceIndex; index++)
        {
            if (sourceQueue[index].Type == selected.Type && sourceQueue[index].Id == selected.Id)
            {
                occurrence++;
            }
        }

        for (var index = 0; index < resolvedQueue.Count; index++)
        {
            var candidate = resolvedQueue[index];
            if (candidate.Type != selected.Type || candidate.Id != selected.Id)
            {
                continue;
            }

            if (occurrence == 0)
            {
                return index;
            }

            occurrence--;
        }

        return FindStartIndex(resolvedQueue, selected);
    }

    private MusicKitWebPlayer? GetOrCreateMusicKitPlayer()
    {
        if (_musicKit is not null)
        {
            return _musicKit;
        }

        if (_credentials is null || _api is null || _musicKitViewFactory is null)
        {
            return null;
        }

        _musicKit = new MusicKitWebPlayer(
            _credentials,
            _api,
            _musicKitViewFactory(),
            _setMusicKitPresentation ?? (static (_, _) => { }));
        _musicKit.StateChanged += OnMusicKitStateChanged;
        _musicKit.PlaybackStateChanged += (_, _) => PlaybackStateChanged?.Invoke(this, EventArgs.Empty);
        _musicKit.PlaybackFailed += (_, args) => PlaybackFailed?.Invoke(this, args);
        _musicKit.AuthorizationDismissed += OnMusicKitAuthorizationDismissed;
        _musicKit.AuthorizationChanged += (_, _) => MusicKitAuthorizationChanged?.Invoke(this, EventArgs.Empty);
        return _musicKit;
    }

    public void MoveQueueItem(int index, int destinationIndex)
    {
        if (!CanMoveQueueItem(index, destinationIndex) || _playbackList is null)
        {
            return;
        }

        var playbackItem = _playbackList.Items[index];
        _playbackList.Items.RemoveAt(index);
        _playbackList.Items.Insert(destinationIndex, playbackItem);

        var queue = _queue.ToList();
        var item = queue[index];
        queue.RemoveAt(index);
        queue.Insert(destinationIndex, item);
        _queue = queue.ToArray();
        QueueChanged?.Invoke(this, EventArgs.Empty);
    }

    public void ClearQueue()
    {
        if (_queue.Count == 0 && !_isMusicKitActive)
        {
            return;
        }

        if (_playbackList is not null)
        {
            _playbackList.CurrentItemChanged -= OnCurrentItemChanged;
        }

        _playbackList = null;
        var wasMusicKitActive = _isMusicKitActive;
        _isMusicKitActive = false;
        _musicKitState = null;
        _queue = Array.Empty<MusicResource>();
        _currentQueueIndex = -1;
        if (wasMusicKitActive)
        {
            _ = _musicKit?.StopAsync();
        }

        MediaPlayer.Pause();
        if (_element is not null)
        {
            _element.Source = null;
        }
        else
        {
            MediaPlayer.Source = null;
        }

        UpdateTransportNavigation();
        QueueChanged?.Invoke(this, EventArgs.Empty);
        TrackChanged?.Invoke(this, new TrackChangedEventArgs(null));
    }

    public void SkipPrevious()
    {
        if (CanSkipPrevious)
        {
            if (_isMusicKitActive)
            {
                _ = RunMusicKitCommandAsync(static player => player.SkipPreviousAsync());
            }
            else
            {
                _playbackList?.MovePrevious();
            }
        }
    }

    public void SkipNext()
    {
        if (CanSkipNext)
        {
            if (_isMusicKitActive)
            {
                _ = RunMusicKitCommandAsync(static player => player.SkipNextAsync());
            }
            else
            {
                _playbackList?.MoveNext();
            }
        }
    }

    public void TogglePlayback()
    {
        if (_queue.Count == 0)
        {
            return;
        }

        if (_isMusicKitActive)
        {
            _ = RunMusicKitCommandAsync(IsPlaying
                ? static player => player.PauseAsync()
                : static player => player.ResumeAsync());
            return;
        }

        if (_playbackList is null)
        {
            _ = ResumeRestoredQueueAsync();
            return;
        }

        if (IsPlaying)
        {
            MediaPlayer.Pause();
        }
        else
        {
            MediaPlayer.Play();
        }
    }

    private async Task ResumeRestoredQueueAsync()
    {
        var sourceQueue = _queue;
        var sourceStartIndex = _currentQueueIndex;
        IReadOnlyList<MusicResource> resolvedItems;
        try
        {
            resolvedItems = _api is null
                ? sourceQueue.Where(static item => item.CanStartPlayback &&
                    (item.CanPlayInMusicKit || item.HasPlayablePreview)).ToArray()
                : await _api.ResolvePlaybackItemsAsync(sourceQueue);
        }
        catch (Exception exception)
        {
            PlaybackFailed?.Invoke(this, new PlaybackFailedEventArgs(exception.Message));
            return;
        }

        if (!ReferenceEquals(_queue, sourceQueue))
        {
            return;
        }

        if (resolvedItems.Count == 0)
        {
            PlaybackFailed?.Invoke(this, new PlaybackFailedEventArgs(
                "The saved queue no longer contains any tracks available for playback."));
            return;
        }

        var usesMusicKit = CanStartMusicKitQueue(resolvedItems);
        try
        {
            var startIndex = MapResolvedQueueIndex(sourceQueue, resolvedItems, sourceStartIndex);
            await PlayResolvedQueueAsync(resolvedItems, startIndex);
        }
        catch (Exception exception)
        {
            if (!usesMusicKit)
            {
                PlaybackFailed?.Invoke(this, new PlaybackFailedEventArgs(exception.Message));
            }
        }
    }

    public void Seek(TimeSpan position)
    {
        if (_isMusicKitActive)
        {
            var fullDuration = CurrentDuration;
            var clamped = fullDuration > TimeSpan.Zero
                ? TimeSpan.FromTicks(Math.Clamp(position.Ticks, 0, fullDuration.Ticks))
                : TimeSpan.FromTicks(Math.Max(0, position.Ticks));
            _ = RunMusicKitCommandAsync(player => player.SeekAsync(clamped.TotalSeconds));
            return;
        }

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
        _volume = clampedVolume;
        if (_isMusicKitActive)
        {
            _ = RunMusicKitCommandAsync(player => player.SetVolumeAsync(clampedVolume));
        }
        else
        {
            MediaPlayer.Volume = clampedVolume;
        }

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
        SetVolume(0);
    }

    public void ToggleShuffle()
    {
        if (!CanShuffle || (!_isMusicKitActive && _playbackList is null))
        {
            return;
        }

        _shuffleEnabled = !_shuffleEnabled;
        if (_isMusicKitActive)
        {
            _ = RunMusicKitCommandAsync(player => player.SetShuffleAsync(_shuffleEnabled));
        }
        else if (_playbackList is not null)
        {
            _playbackList.ShuffleEnabled = _shuffleEnabled;
        }

        UpdateTransportNavigation();
    }

    public void ToggleRepeat()
    {
        if (!CanRepeat || (!_isMusicKitActive && _playbackList is null))
        {
            return;
        }

        _repeatEnabled = !_repeatEnabled;
        if (_isMusicKitActive)
        {
            _ = RunMusicKitCommandAsync(player => player.SetRepeatAsync(_repeatEnabled));
        }
        else if (_playbackList is not null)
        {
            _playbackList.AutoRepeatEnabled = _repeatEnabled;
        }

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
            TrackChanged?.Invoke(this, new TrackChangedEventArgs(item));
            QueueChanged?.Invoke(this, EventArgs.Empty);
        });
    }

    private void UpdateTransportNavigation()
    {
        var controls = MediaPlayer.SystemMediaTransportControls;
        controls.IsPreviousEnabled = CanSkipPrevious;
        controls.IsNextEnabled = CanSkipNext;

        if (!_isMusicKitActive)
        {
            _transportTrackIdentity = "";
            return;
        }

        controls.PlaybackStatus = IsPlaying ? MediaPlaybackStatus.Playing : MediaPlaybackStatus.Paused;
        if (CurrentItem is { } item)
        {
            var identity = $"{item.Type}:{item.Id}";
            if (identity != _transportTrackIdentity)
            {
                _transportTrackIdentity = identity;
                var updater = controls.DisplayUpdater;
                updater.Type = item.IsMusicVideo ? MediaPlaybackType.Video : MediaPlaybackType.Music;
                if (item.IsMusicVideo)
                {
                    updater.VideoProperties.Title = item.Name;
                    updater.VideoProperties.Subtitle = item.ArtistName;
                }
                else
                {
                    updater.MusicProperties.Title = item.Name;
                    updater.MusicProperties.Artist = item.ArtistName;
                    updater.MusicProperties.AlbumArtist = item.ArtistName;
                    updater.MusicProperties.AlbumTitle = item.AlbumName;
                }

                var artworkUrl = item.ArtworkUrl?.Replace("{w}", "512", StringComparison.Ordinal)
                    .Replace("{h}", "512", StringComparison.Ordinal);
                if (Uri.TryCreate(artworkUrl, UriKind.Absolute, out var artworkUri) &&
                    artworkUri.Scheme == Uri.UriSchemeHttps)
                {
                    updater.Thumbnail = RandomAccessStreamReference.CreateFromUri(artworkUri);
                }

                updater.Update();
            }
        }

        var duration = CurrentDuration;
        controls.UpdateTimelineProperties(new SystemMediaTransportControlsTimelineProperties
        {
            StartTime = TimeSpan.Zero,
            EndTime = duration,
            MinSeekTime = TimeSpan.Zero,
            MaxSeekTime = duration,
            Position = CurrentPosition
        });
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

    private static MediaPlaybackItem CreatePlaybackItem(MusicResource item)
    {
        if (!TryGetPreviewUri(item, out var uri))
        {
            throw new InvalidOperationException("This item has no playable preview audio.");
        }

        var playbackItem = new MediaPlaybackItem(MediaSource.CreateFromUri(uri));
        var displayProperties = playbackItem.GetDisplayProperties();
        if (item.IsMusicVideo)
        {
            displayProperties.Type = MediaPlaybackType.Video;
            displayProperties.VideoProperties.Title = item.Name;
            displayProperties.VideoProperties.Subtitle = item.ArtistName;
        }
        else
        {
            displayProperties.Type = MediaPlaybackType.Music;
            displayProperties.MusicProperties.Title = item.Name;
            displayProperties.MusicProperties.Artist = item.ArtistName;
        }

        var artworkUrl = item.ArtworkUrl?.Replace("{w}", "512", StringComparison.Ordinal)
            .Replace("{h}", "512", StringComparison.Ordinal);
        if (Uri.TryCreate(artworkUrl, UriKind.Absolute, out var artworkUri) && artworkUri.Scheme == Uri.UriSchemeHttps)
        {
            displayProperties.Thumbnail = RandomAccessStreamReference.CreateFromUri(artworkUri);
        }

        playbackItem.ApplyDisplayProperties(displayProperties);
        return playbackItem;
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
                    if (!IsPlaying)
                    {
                        TogglePlayback();
                    }
                    break;
                case SystemMediaTransportControlsButton.Pause:
                case SystemMediaTransportControlsButton.Stop:
                    if (_isMusicKitActive)
                    {
                        _ = RunMusicKitCommandAsync(static player => player.PauseAsync());
                    }
                    else
                    {
                        MediaPlayer.Pause();
                    }
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

    private void OnTransportPositionChangeRequested(
        SystemMediaTransportControls sender,
        PlaybackPositionChangeRequestedEventArgs args)
    {
        var dispatcherQueue = _dispatcherQueue;
        if (dispatcherQueue is null)
        {
            return;
        }

        var requestedPosition = args.RequestedPlaybackPosition;
        dispatcherQueue.TryEnqueue(() => Seek(requestedPosition));
    }

    private void OnPlaybackStateChanged(MediaPlaybackSession sender, object args)
    {
        if (_isMusicKitActive)
        {
            return;
        }

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
            PlaybackStateChanged?.Invoke(this, EventArgs.Empty);
        });
    }

    private void OnMediaFailed(MediaPlayer sender, MediaPlayerFailedEventArgs args)
    {
        var dispatcherQueue = _dispatcherQueue;
        if (dispatcherQueue is null)
        {
            return;
        }

        var message = string.IsNullOrWhiteSpace(args.ErrorMessage)
            ? args.Error.ToString()
            : args.ErrorMessage;
        dispatcherQueue.TryEnqueue(() => PlaybackFailed?.Invoke(this, new PlaybackFailedEventArgs(message)));
    }
}

public sealed class TrackChangedEventArgs(MusicResource? resource) : EventArgs
{
    public MusicResource? Resource { get; } = resource;
    public string Title => Resource?.Name ?? "Nothing playing";
    public string Artist => Resource?.ArtistName ?? string.Empty;
}

public sealed class PlaybackFailedEventArgs(string message) : EventArgs
{
    public string Message { get; } = message;
}

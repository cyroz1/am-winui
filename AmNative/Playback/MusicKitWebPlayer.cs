using System.Text.Json;
using AmNative.Models;
using AmNative.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.Web.WebView2.Core;

namespace AmNative.Playback;

public sealed class MusicKitWebPlayer
{
    private static readonly Uri BridgeUri = new("https://appassets.amnative/MusicKitBridge.html");
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly CredentialStore _credentials;
    private readonly AppleMusicApiClient _api;
    private readonly WebView2 _view;
    private readonly Action<bool, bool> _setPresentation;
    private TaskCompletionSource _bridgeReady = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Dictionary<long, TaskCompletionSource<JsonElement>> _commands = new();
    private readonly string _userDataFolder;
    private CoreWebView2Environment? _environment;
    private Task? _coreInitializationTask;
    private Task? _initializationTask;
    private long _nextCommandId;
    private int _configurationGeneration;
    private bool _configured;
    private bool _isAuthorized;
    private bool _requiresUserGesture;
    private bool _showingAuthorizationOverlay;
    private bool _coreInitialized;
    private bool _videoDismissed;
    private string _currentIdentity = "";
    private PendingQueue? _pendingQueue;
    private Window? _authorizationWindow;

    public MusicKitWebPlayer(
        CredentialStore credentials,
        AppleMusicApiClient api,
        WebView2 view,
        Action<bool, bool> setPresentation)
    {
        _credentials = credentials;
        _api = api;
        _view = view;
        _setPresentation = setPresentation;
        _userDataFolder = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "AMNative",
            "MusicKitWebView2");
    }

    public bool IsConfigured => _configured;
    public bool IsAuthorized => _isAuthorized;
    public bool RequiresUserGesture => _requiresUserGesture;

    public event EventHandler<MusicKitStateChangedEventArgs>? StateChanged;
    public event EventHandler<PlaybackFailedEventArgs>? PlaybackFailed;
    public event EventHandler? PlaybackStateChanged;
    public event EventHandler? AuthorizationDismissed;
    public event EventHandler? AuthorizationChanged;

    public async Task<bool> TryPlayQueueAsync(
        IReadOnlyList<MusicResource> queue,
        int startIndex,
        double volume,
        bool shuffle,
        bool repeat,
        double positionSeconds = 0,
        bool isPlaying = true)
    {
        if (queue.Count == 0 || queue.Any(static item => !item.CanPlayInMusicKit))
        {
            return false;
        }

        await EnsureConfiguredAsync();
        var pending = new PendingQueue(
            queue,
            Math.Clamp(startIndex, 0, queue.Count - 1),
            volume,
            shuffle,
            repeat,
            Math.Max(0, positionSeconds),
            isPlaying);
        if (!_isAuthorized)
        {
            _pendingQueue = pending;
            SetAuthorizationOverlay(true);
            await SendCommandCoreAsync("showAuthorization", new { });
            return true;
        }

        await QueueAndPlayAsync(pending);
        return true;
    }

    public async Task RequestAuthorizationAsync()
    {
        await EnsureConfiguredAsync();
        SetAuthorizationOverlay(true);
        await SendCommandAsync(_isAuthorized ? "showStart" : "showAuthorization", new { });
    }

    public async Task<bool> EnsureAuthorizationStateAsync()
    {
        await EnsureConfiguredAsync();
        return _isAuthorized;
    }

    public async Task PauseAsync()
    {
        if (_configured)
        {
            await SendCommandAsync("pause", new { });
        }
    }

    public async Task ResumeAsync()
    {
        if (_configured)
        {
            await SendCommandAsync("resume", new { });
        }
    }

    public async Task StopAsync()
    {
        _pendingQueue = null;
        _showingAuthorizationOverlay = false;
        if (_configured)
        {
            await SendCommandAsync("stop", new { });
        }

        UpdatePresentation();
    }

    public async Task ForgetAuthorizationAsync()
    {
        try
        {
            await EnsureCoreWebViewAsync();
            if (_configured)
            {
                try
                {
                    await SendCommandCoreAsync("stop", new { });
                }
                catch
                {
                    // Clearing the isolated WebView profile still removes the saved sign-in.
                }
            }

            if (_view.CoreWebView2 is not null)
            {
                await _view.CoreWebView2.Profile.ClearBrowsingDataAsync(CoreWebView2BrowsingDataKinds.AllSite);
            }
        }
        finally
        {
            _pendingQueue = null;
            _showingAuthorizationOverlay = false;
            UpdatePresentation();
        }
    }

    public async Task SeekAsync(double positionSeconds)
    {
        if (_configured)
        {
            await SendCommandAsync("seek", new { positionSeconds = Math.Max(0, positionSeconds) });
        }
    }

    public async Task SkipNextAsync()
    {
        if (_configured)
        {
            await SendCommandAsync("next", new { });
        }
    }

    public async Task SkipPreviousAsync()
    {
        if (_configured)
        {
            await SendCommandAsync("previous", new { });
        }
    }

    public async Task SetVolumeAsync(double volume)
    {
        if (_configured)
        {
            await SendCommandAsync("setVolume", new { volume = Math.Clamp(volume, 0, 1) });
        }
    }

    public async Task SetShuffleAsync(bool enabled)
    {
        if (_configured)
        {
            await SendCommandAsync("setShuffle", new { enabled });
        }
    }

    public async Task SetRepeatAsync(bool enabled)
    {
        if (_configured)
        {
            await SendCommandAsync("setRepeat", new { enabled });
        }
    }

    public async Task ReplaceQueueItemsAsync(
        IReadOnlyList<MusicResource> items,
        int startIndex,
        double positionSeconds,
        bool isPlaying,
        double volume,
        bool shuffle,
        bool repeat)
    {
        if (items.Count == 0 || items.Any(static item => !item.CanPlayInMusicKit))
        {
            throw new InvalidOperationException("Only full Apple Music tracks can remain in an active queue.");
        }

        await SendCommandAsync("replaceQueue", new
        {
            items = CreateDescriptors(items),
            startIndex = Math.Clamp(startIndex, 0, items.Count - 1),
            positionSeconds = Math.Max(0, positionSeconds),
            isPlaying,
            volume = Math.Clamp(volume, 0, 1),
            shuffle,
            repeat
        });
    }

    public async Task AppendQueueItemsAsync(IReadOnlyList<MusicResource> items)
    {
        if (items.Count == 0 || items.Any(static item => !item.CanPlayInMusicKit))
        {
            throw new InvalidOperationException("Only full Apple Music tracks can be added to the active queue.");
        }

        await SendCommandAsync("appendQueue", new { items = CreateDescriptors(items) });
    }

    private Task EnsureConfiguredAsync()
    {
        if (string.IsNullOrWhiteSpace(_credentials.MusicKitDeveloperToken))
        {
            return Task.FromException(new InvalidOperationException(
                "Add an Apple Music developer token or a Team ID, Key ID, and .p8 key in Settings to enable full-track playback."));
        }

        return _initializationTask ??= InitializeWithRetryAsync(_configurationGeneration);
    }

    public void ResetConfiguration()
    {
        _configurationGeneration++;
        foreach (var command in _commands.Values)
        {
            command.TrySetException(new InvalidOperationException("Apple Music settings changed. Start playback again."));
        }

        _commands.Clear();
        _configured = false;
        var wasAuthorized = _isAuthorized;
        _isAuthorized = false;
        _requiresUserGesture = false;
        _pendingQueue = null;
        _videoDismissed = false;
        _currentIdentity = "";
        _showingAuthorizationOverlay = false;
        UpdatePresentation();
        _authorizationWindow?.Close();
        _authorizationWindow = null;
        _initializationTask = null;
        _bridgeReady.TrySetCanceled();
        _bridgeReady = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        if (_view.CoreWebView2 is not null)
        {
            _view.Source = new Uri("about:blank");
        }

        if (wasAuthorized)
        {
            AuthorizationChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    private async Task InitializeAsync(int generation)
    {
        var developerToken = _credentials.MusicKitDeveloperToken!;
        await EnsureCoreWebViewAsync();

        if (_view.Source is null || !IsBridgeUri(_view.Source))
        {
            _view.Source = BridgeUri;
        }

        await _bridgeReady.Task.WaitAsync(TimeSpan.FromSeconds(60));
        ThrowIfConfigurationChanged(generation);
        var result = await SendCommandCoreAsync("configure", new { developerToken });
        ThrowIfConfigurationChanged(generation);
        var wasAuthorized = _isAuthorized;
        _isAuthorized = ReadBoolean(result, "isAuthorized");
        if (_isAuthorized)
        {
            PersistMusicUserToken(ReadString(result, "userToken"));
        }

        if (wasAuthorized != _isAuthorized)
        {
            AuthorizationChanged?.Invoke(this, EventArgs.Empty);
        }

        _configured = true;
    }

    private Task EnsureCoreWebViewAsync() =>
        _coreInitializationTask ??= InitializeCoreWebViewWithRetryAsync();

    private async Task InitializeCoreWebViewWithRetryAsync()
    {
        try
        {
            await InitializeCoreWebViewAsync();
        }
        catch
        {
            _coreInitializationTask = null;
            throw;
        }
    }

    private async Task InitializeCoreWebViewAsync()
    {
        if (_environment is null)
        {
            Directory.CreateDirectory(_userDataFolder);
            _environment = await CoreWebView2Environment.CreateAsync(null, _userDataFolder, null);
        }

        await _view.EnsureCoreWebView2Async(_environment);
        if (!_coreInitialized)
        {
            var core = _view.CoreWebView2;
            core.SetVirtualHostNameToFolderMapping(
                BridgeUri.Host,
                Path.Combine(AppContext.BaseDirectory, "Playback"),
                CoreWebView2HostResourceAccessKind.DenyCors);
            core.Settings.AreDevToolsEnabled = false;
            core.Settings.AreDefaultContextMenusEnabled = false;
            core.Settings.IsStatusBarEnabled = false;
            core.WebMessageReceived += OnWebMessageReceived;
            core.NavigationStarting += (_, args) =>
            {
                args.Cancel = !Uri.TryCreate(args.Uri, UriKind.Absolute, out var uri) ||
                              (!IsBridgeUri(uri) && !string.Equals(uri.AbsoluteUri, "about:blank", StringComparison.OrdinalIgnoreCase));
            };
            core.NewWindowRequested += OnNewWindowRequested;
            _coreInitialized = true;
        }
    }

    private async Task InitializeWithRetryAsync(int generation)
    {
        try
        {
            await InitializeAsync(generation);
        }
        catch (Exception exception)
        {
            if (generation == _configurationGeneration)
            {
                _initializationTask = null;
                if (exception is TimeoutException || _bridgeReady.Task.IsFaulted || _bridgeReady.Task.IsCanceled)
                {
                    ReloadBridge();
                }
            }

            throw;
        }
    }

    private void ReloadBridge()
    {
        _bridgeReady.TrySetCanceled();
        _bridgeReady = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        if (_view.CoreWebView2 is not null)
        {
            _view.Source = new Uri($"{BridgeUri.AbsoluteUri}?retry={DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}");
        }
    }

    private void ThrowIfConfigurationChanged(int generation)
    {
        if (generation != _configurationGeneration)
        {
            throw new InvalidOperationException("Apple Music settings changed. Start playback again.");
        }
    }

    private async Task QueueAndPlayAsync(PendingQueue pending)
    {
        _pendingQueue = null;
        _showingAuthorizationOverlay = false;
        UpdatePresentation();
        await SendCommandAsync("playQueue", new
        {
            items = CreateDescriptors(pending.Items),
            startIndex = pending.StartIndex,
            volume = Math.Clamp(pending.Volume, 0, 1),
            shuffle = pending.Shuffle,
            repeat = pending.Repeat,
            positionSeconds = pending.PositionSeconds,
            isPlaying = pending.IsPlaying
        });
    }

    private static object[] CreateDescriptors(IReadOnlyList<MusicResource> items) => items.Select(item =>
    {
        if (item.IsMusicVideo)
        {
            var videoId = item.Type == "library-music-videos" && !string.IsNullOrWhiteSpace(item.CatalogId)
                ? item.CatalogId
                : item.Type == "library-music-videos" &&
                  string.Equals(item.PlayParametersKind, "musicVideo", StringComparison.OrdinalIgnoreCase) &&
                  !string.IsNullOrWhiteSpace(item.PlayParametersId)
                    ? item.PlayParametersId
                    : item.Id;
            return (object)new { kind = "musicVideos", id = videoId };
        }

        return (object)new { kind = "songs", id = item.Id };
    }).ToArray();

    private async Task<JsonElement> SendCommandAsync(string action, object payload)
    {
        await EnsureConfiguredAsync();
        return await SendCommandCoreAsync(action, payload);
    }

    private async Task<JsonElement> SendCommandCoreAsync(string action, object payload)
    {
        var commandId = Interlocked.Increment(ref _nextCommandId);
        var completion = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        _commands.Add(commandId, completion);
        try
        {
            var json = JsonSerializer.Serialize(new { commandId, action, payload }, JsonOptions);
            _view.CoreWebView2.PostWebMessageAsJson(json);
            return await completion.Task.WaitAsync(TimeSpan.FromSeconds(45));
        }
        finally
        {
            _commands.Remove(commandId);
        }
    }

    private void OnWebMessageReceived(object? sender, CoreWebView2WebMessageReceivedEventArgs args)
    {
        if (!Uri.TryCreate(args.Source, UriKind.Absolute, out var source) || !IsBridgeUri(source))
        {
            return;
        }

        try
        {
            using var document = JsonDocument.Parse(args.WebMessageAsJson);
            var root = document.RootElement;
            var type = ReadString(root, "type");
            switch (type)
            {
                case "bridgeReady":
                    _bridgeReady.TrySetResult();
                    break;
                case "bridgeError":
                    _bridgeReady.TrySetException(new InvalidOperationException(
                        ReadString(root, "message") ?? "MusicKit on the Web could not be loaded."));
                    break;
                case "commandResult":
                    CompleteCommand(root);
                    break;
                case "state":
                    ApplyPlaybackState(root);
                    break;
                case "authorized":
                    HandleAuthorized(ReadString(root, "userToken"));
                    break;
                case "started":
                    _requiresUserGesture = false;
                    _showingAuthorizationOverlay = false;
                    UpdatePresentation();
                    PlaybackStateChanged?.Invoke(this, EventArgs.Empty);
                    break;
                case "authorizationReady":
                    _showingAuthorizationOverlay = false;
                    UpdatePresentation();
                    break;
                case "userGestureRequired":
                    _requiresUserGesture = true;
                    SetAuthorizationOverlay(true);
                    break;
                case "cancel":
                    _pendingQueue = null;
                    _showingAuthorizationOverlay = false;
                    UpdatePresentation();
                    AuthorizationDismissed?.Invoke(this, EventArgs.Empty);
                    break;
                case "hideVideo":
                    _videoDismissed = true;
                    UpdatePresentation();
                    break;
                case "authorizationError":
                case "playbackError":
                    PlaybackFailed?.Invoke(this, new PlaybackFailedEventArgs(
                        ReadString(root, "message") ?? "Apple Music could not start playback."));
                    break;
            }
        }
        catch (JsonException)
        {
            PlaybackFailed?.Invoke(this, new PlaybackFailedEventArgs("Apple Music returned an unreadable player response."));
        }
    }

    private void CompleteCommand(JsonElement root)
    {
        if (!root.TryGetProperty("commandId", out var commandIdValue) ||
            !commandIdValue.TryGetInt64(out var commandId) ||
            !_commands.TryGetValue(commandId, out var completion))
        {
            return;
        }

        if (ReadBoolean(root, "success"))
        {
            var result = root.TryGetProperty("result", out var resultValue)
                ? resultValue.Clone()
                : default;
            completion.TrySetResult(result);
        }
        else
        {
            completion.TrySetException(new InvalidOperationException(
                ReadString(root, "error") ?? "Apple Music could not complete the playback request."));
        }
    }

    private void ApplyPlaybackState(JsonElement root)
    {
        var state = new MusicKitStateChangedEventArgs(
            ReadString(root, "id") ?? "",
            ReadString(root, "type") ?? "",
            ReadBoolean(root, "isPlaying"),
            ReadDouble(root, "positionSeconds"),
            ReadDouble(root, "durationSeconds"),
            ReadDouble(root, "volume", 0.5),
            ReadBoolean(root, "shuffle"),
            ReadInt(root, "repeatMode"),
            ReadInt(root, "queuePosition"),
            ReadBoolean(root, "isVideo"));
        var identity = $"{state.Type}:{state.Id}";
        if (state.IsPlaying)
        {
            _requiresUserGesture = false;
        }
        if (state.Id.Length > 0 && identity != _currentIdentity)
        {
            _currentIdentity = identity;
            _videoDismissed = false;
        }

        StateChanged?.Invoke(this, state);
        PlaybackStateChanged?.Invoke(this, EventArgs.Empty);
        UpdatePresentation(state.IsPlaying && state.IsVideo && !_videoDismissed);
    }

    private void SetAuthorizationOverlay(bool visible)
    {
        _showingAuthorizationOverlay = visible;
        UpdatePresentation();
    }

    private void UpdatePresentation(bool showVideo = false) =>
        _setPresentation(_showingAuthorizationOverlay, showVideo);

    private void HandleAuthorized(string? userToken)
    {
        var wasAuthorized = _isAuthorized;
        _isAuthorized = true;
        if (!wasAuthorized)
        {
            AuthorizationChanged?.Invoke(this, EventArgs.Empty);
        }
        _authorizationWindow?.Close();
        _authorizationWindow = null;
        PersistMusicUserToken(userToken);

        var pending = _pendingQueue;
        if (pending is not null)
        {
            _ = QueuePendingAfterAuthorizationAsync(pending);
        }
        else
        {
            _ = CompleteAuthorizationWithoutQueueAsync();
        }
    }

    private void PersistMusicUserToken(string? userToken)
    {
        if (string.IsNullOrWhiteSpace(userToken) ||
            string.Equals(_credentials.MusicUserToken, userToken, StringComparison.Ordinal))
        {
            return;
        }

        try
        {
            _credentials.Save(null, userToken, null);
            _api.ResetSession();
        }
        catch (Exception exception)
        {
            PlaybackFailed?.Invoke(this, new PlaybackFailedEventArgs(
                $"Apple Music is authorized, but the Music User Token could not be saved: {exception.Message}"));
        }
    }

    private async Task CompleteAuthorizationWithoutQueueAsync()
    {
        try
        {
            await SendCommandAsync("showStart", new { });
        }
        catch (Exception exception)
        {
            PlaybackFailed?.Invoke(this, new PlaybackFailedEventArgs(exception.Message));
        }
    }

    private async Task QueuePendingAfterAuthorizationAsync(PendingQueue pending)
    {
        try
        {
            await QueueAndPlayAsync(pending);
        }
        catch (Exception exception)
        {
            if (!_requiresUserGesture)
            {
                PlaybackFailed?.Invoke(this, new PlaybackFailedEventArgs(exception.Message));
            }
        }
    }

    private async void OnNewWindowRequested(object? sender, CoreWebView2NewWindowRequestedEventArgs args)
    {
        if (!IsAllowedAuthenticationUri(args.Uri))
        {
            args.Handled = true;
            return;
        }

        var deferral = args.GetDeferral();
        try
        {
            if (_environment is null)
            {
                args.Handled = true;
                return;
            }

            var popupView = new WebView2
            {
                HorizontalAlignment = HorizontalAlignment.Stretch,
                VerticalAlignment = VerticalAlignment.Stretch
            };
            var popupWindow = new Window
            {
                Title = "Sign in to Apple Music",
                Content = popupView
            };
            await popupView.EnsureCoreWebView2Async(_environment);
            popupView.CoreWebView2.SetVirtualHostNameToFolderMapping(
                BridgeUri.Host,
                Path.Combine(AppContext.BaseDirectory, "Playback"),
                CoreWebView2HostResourceAccessKind.DenyCors);
            popupView.CoreWebView2.Settings.AreDevToolsEnabled = false;
            popupView.CoreWebView2.Settings.AreDefaultContextMenusEnabled = false;
            popupView.CoreWebView2.NewWindowRequested += OnNewWindowRequested;
            popupView.CoreWebView2.NavigationStarting += (_, navigation) =>
            {
                navigation.Cancel = !IsAllowedAuthenticationUri(navigation.Uri);
            };
            popupView.CoreWebView2.WindowCloseRequested += (_, _) => popupWindow.Close();
            popupWindow.Closed += (_, _) =>
            {
                if (ReferenceEquals(_authorizationWindow, popupWindow))
                {
                    _authorizationWindow = null;
                }

                popupWindow.Content = null;
            };
            args.NewWindow = popupView.CoreWebView2;
            _authorizationWindow = popupWindow;
            popupWindow.Activate();
        }
        catch
        {
            args.Handled = true;
            PlaybackFailed?.Invoke(this, new PlaybackFailedEventArgs(
                "Apple Music could not open its sign-in window."));
        }
        finally
        {
            deferral.Complete();
        }
    }

    private static bool IsBridgeUri(Uri uri) =>
        string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) &&
        string.Equals(uri.Host, BridgeUri.Host, StringComparison.OrdinalIgnoreCase);

    private static bool IsAllowedAuthenticationUri(string value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri))
        {
            return false;
        }

        if (string.Equals(uri.AbsoluteUri, "about:blank", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) &&
               (uri.Host.Equals(BridgeUri.Host, StringComparison.OrdinalIgnoreCase) ||
                uri.Host.Equals("apple.com", StringComparison.OrdinalIgnoreCase) ||
                uri.Host.EndsWith(".apple.com", StringComparison.OrdinalIgnoreCase) ||
                uri.Host.Equals("icloud.com", StringComparison.OrdinalIgnoreCase) ||
                uri.Host.EndsWith(".icloud.com", StringComparison.OrdinalIgnoreCase));
    }

    private static string? ReadString(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) &&
        value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static bool ReadBoolean(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) &&
        (value.ValueKind is JsonValueKind.True or JsonValueKind.False) && value.GetBoolean();

    private static double ReadDouble(JsonElement element, string name, double fallback = 0) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) &&
        value.TryGetDouble(out var number) && double.IsFinite(number)
            ? number
            : fallback;

    private static int ReadInt(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) &&
        value.TryGetInt32(out var number)
            ? number
            : 0;

    private sealed record PendingQueue(
        IReadOnlyList<MusicResource> Items,
        int StartIndex,
        double Volume,
        bool Shuffle,
        bool Repeat,
        double PositionSeconds,
        bool IsPlaying);
}

public sealed class MusicKitStateChangedEventArgs(
    string id,
    string type,
    bool isPlaying,
    double positionSeconds,
    double durationSeconds,
    double volume,
    bool shuffle,
    int repeatMode,
    int queuePosition,
    bool isVideo) : EventArgs
{
    public string Id { get; } = id;
    public string Type { get; } = type;
    public bool IsPlaying { get; } = isPlaying;
    public TimeSpan Position { get; } = TimeSpan.FromSeconds(Math.Max(0, positionSeconds));
    public TimeSpan Duration { get; } = TimeSpan.FromSeconds(Math.Max(0, durationSeconds));
    public double Volume { get; } = Math.Clamp(volume, 0, 1);
    public bool Shuffle { get; } = shuffle;
    public int RepeatMode { get; } = repeatMode;
    public int QueuePosition { get; } = queuePosition;
    public bool IsVideo { get; } = isVideo;
}

using AmNative.Services;
using AmNative.Playback;
using AmNative.Models;
using Microsoft.UI.Xaml;

namespace AmNative;

public partial class App : Application
{
    private Window? _window;
    public static Window? WindowInstance { get; private set; }

    public static CredentialStore Credentials { get; } = new();
    public static AppleMusicApiClient MusicApi { get; } = new(Credentials);
    public static PreviewPlayer Player { get; } = new();

    public static async Task<int> EnqueuePreviewsAsync(IEnumerable<MusicResource> items)
    {
        var playableItems = await MusicApi.ResolvePreviewsForPlaybackAsync(
            items.Where(static item => item.CanPlayPreview).ToArray());
        await Player.EnqueuePreviewsAsync(playableItems);
        return playableItems.Count;
    }

    public App()
    {
        InitializeComponent();
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        _window = new MainWindow();
        WindowInstance = _window;
        _window.Activate();
    }
}

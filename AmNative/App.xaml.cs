using AmNative.Services;
using AmNative.Playback;
using Microsoft.UI.Xaml;

namespace AmNative;

public partial class App : Application
{
    private Window? _window;

    public static CredentialStore Credentials { get; } = new();
    public static AppleMusicApiClient MusicApi { get; } = new(Credentials);
    public static PreviewPlayer Player { get; } = new();

    public App()
    {
        InitializeComponent();
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        _window = new MainWindow();
        _window.Activate();
    }
}

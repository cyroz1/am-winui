using AmNative.Services;
using AmNative.Playback;
using AmNative.Models;
using Microsoft.UI.Xaml;
using Windows.Storage;

namespace AmNative;

public partial class App : Application
{
    private const string ThemeSettingName = "theme";
    private Window? _window;
    public static Window? WindowInstance { get; private set; }
    public static ElementTheme ThemePreference { get; private set; } = ElementTheme.Default;

    public static CredentialStore Credentials { get; } = new();
    public static AppleMusicApiClient MusicApi { get; } = new(Credentials);
    public static PreviewPlayer Player { get; } = new(Credentials, MusicApi);

    public static Task<IReadOnlyList<MusicResource>> ResolvePlaybackItemsAsync(
        IEnumerable<MusicResource> items,
        CancellationToken cancellationToken = default) =>
        MusicApi.ResolvePlaybackItemsAsync(items, cancellationToken);

    public static async Task<int> EnqueuePlaybackItemsAsync(IEnumerable<MusicResource> items)
    {
        var playableItems = await ResolvePlaybackItemsAsync(items);
        await Player.EnqueuePlaybackItemsAsync(playableItems);
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
        LoadThemePreference();
        _window.Activate();
    }

    public static void SetThemePreference(ElementTheme theme)
    {
        ThemePreference = theme is ElementTheme.Default or ElementTheme.Light or ElementTheme.Dark
            ? theme
            : ElementTheme.Default;
        ApplicationData.Current.LocalSettings.Values[ThemeSettingName] = ThemePreference.ToString();

        if (WindowInstance?.Content is FrameworkElement root)
        {
            root.RequestedTheme = ThemePreference;
        }
    }

    private static void LoadThemePreference()
    {
        var savedTheme = ApplicationData.Current.LocalSettings.Values.TryGetValue(ThemeSettingName, out var value) &&
                         value is string themeName &&
                         Enum.TryParse(themeName, ignoreCase: true, out ElementTheme parsedTheme) &&
                         parsedTheme is ElementTheme.Default or ElementTheme.Light or ElementTheme.Dark
            ? parsedTheme
            : ElementTheme.Default;
        ThemePreference = savedTheme;

        if (WindowInstance?.Content is FrameworkElement root)
        {
            root.RequestedTheme = savedTheme;
        }
    }
}

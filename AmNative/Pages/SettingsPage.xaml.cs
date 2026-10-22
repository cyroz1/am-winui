using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using AmNative.Services;
using Windows.Storage;
using Windows.Storage.Pickers;

namespace AmNative.Pages;

public sealed partial class SettingsPage : Page
{
    private string? _selectedDeveloperPrivateKeyPem;

    public SettingsPage()
    {
        InitializeComponent();
        ThemeBox.SelectedIndex = App.ThemePreference switch
        {
            ElementTheme.Light => 1,
            ElementTheme.Dark => 2,
            _ => 0
        };
        UpdateStatus();
        UpdateConnectionSummary();
        Loaded += OnPageLoaded;
        Unloaded += OnPageUnloaded;
    }

    private void OnPageLoaded(object sender, RoutedEventArgs args) =>
        App.Player.MusicKitAuthorizationChanged += OnMusicKitAuthorizationChanged;

    private void OnPageUnloaded(object sender, RoutedEventArgs args) =>
        App.Player.MusicKitAuthorizationChanged -= OnMusicKitAuthorizationChanged;

    private void OnMusicKitAuthorizationChanged(object? sender, EventArgs args) => UpdateConnectionSummary();

    private void OnThemeChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ThemeBox.SelectedItem is ComboBoxItem { Tag: string themeName } &&
            Enum.TryParse(themeName, ignoreCase: true, out ElementTheme theme))
        {
            App.SetThemePreference(theme);
        }
    }

    private void OnSaveClicked(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(DeveloperTokenBox.Password) &&
            !App.Credentials.HasDeveloperToken &&
            string.IsNullOrWhiteSpace(DeveloperTeamIdBox.Text) &&
            string.IsNullOrWhiteSpace(DeveloperKeyIdBox.Text) &&
            _selectedDeveloperPrivateKeyPem is null)
        {
            StatusText.Text = "A developer token is required for Apple Music API requests.";
            return;
        }

        try
        {
            var developerConfigurationChanged = !string.IsNullOrWhiteSpace(DeveloperTokenBox.Password) ||
                                                !string.IsNullOrWhiteSpace(DeveloperTeamIdBox.Text) ||
                                                !string.IsNullOrWhiteSpace(DeveloperKeyIdBox.Text) ||
                                                _selectedDeveloperPrivateKeyPem is not null;
            App.Credentials.Save(
                DeveloperTokenBox.Password,
                UserTokenBox.Password,
                CookieBox.Password,
                DeveloperTeamIdBox.Text,
                DeveloperKeyIdBox.Text,
                _selectedDeveloperPrivateKeyPem);
            App.MusicApi.ResetSession();
            if (developerConfigurationChanged)
            {
                App.Player.ResetMusicKitConfiguration();
            }
            DeveloperTokenBox.Password = "";
            UserTokenBox.Password = "";
            CookieBox.Password = "";
            DeveloperTeamIdBox.Text = "";
            DeveloperKeyIdBox.Text = "";
            _selectedDeveloperPrivateKeyPem = null;
            UpdateDeveloperKeyStatus();
            UpdateConnectionSummary();
            StatusText.Text = "Credentials were saved securely. A supplied web cookie is protected with Windows DPAPI. Blank fields kept their saved values.";
        }
        catch (ArgumentException exception)
        {
            StatusText.Text = exception.Message;
        }
        catch
        {
            StatusText.Text = "Windows could not securely save the credentials. Check that Credential Manager and local app data are available for this account.";
        }
    }

    private async void OnChooseDeveloperKeyClicked(object sender, RoutedEventArgs e)
    {
        var window = App.WindowInstance;
        if (window is null)
        {
            DeveloperKeyStatus.Text = "The app window is not ready to open a file picker.";
            return;
        }

        try
        {
            var picker = new FileOpenPicker();
            picker.FileTypeFilter.Add(".p8");
            WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(window));

            var file = await picker.PickSingleFileAsync();
            if (file is null)
            {
                return;
            }

            var fileProperties = await file.GetBasicPropertiesAsync();
            if (fileProperties.Size > 8192)
            {
                DeveloperKeyStatus.Text = "The selected .p8 file is unexpectedly large.";
                return;
            }

            var privateKey = await FileIO.ReadTextAsync(file);
            DeveloperTokenSigner.ValidatePrivateKey(privateKey);
            _selectedDeveloperPrivateKeyPem = privateKey;
            DeveloperKeyStatus.Text = $"{file.Name} selected; it will be stored when you save.";
        }
        catch (ArgumentException exception)
        {
            DeveloperKeyStatus.Text = exception.Message;
        }
        catch
        {
            DeveloperKeyStatus.Text = "Could not read the selected .p8 key file.";
        }
    }

    private async void OnTestConnectionClicked(object sender, RoutedEventArgs e)
    {
        TestConnectionButton.IsEnabled = false;
        StatusText.Text = "Checking the saved Apple Music credentials…";
        try
        {
            var storefront = await App.MusicApi.TestConnectionAsync();
            StatusText.Text = $"Connection verified. Apple Music storefront: {storefront}.";
        }
        catch (Exception exception)
        {
            StatusText.Text = exception.Message;
        }
        finally
        {
            TestConnectionButton.IsEnabled = true;
        }
    }

    private async void OnAuthorizeMusicKitClicked(object sender, RoutedEventArgs e)
    {
        AuthorizeMusicKitButton.IsEnabled = false;
        StatusText.Text = "Opening Apple Music authorization…";
        try
        {
            await App.Player.RequestMusicKitAuthorizationAsync();
            StatusText.Text = "Complete Apple Music authorization in the player window.";
        }
        catch (Exception exception)
        {
            StatusText.Text = exception.Message;
        }
        finally
        {
            AuthorizeMusicKitButton.IsEnabled = App.Credentials.HasDeveloperToken;
            UpdateConnectionSummary();
        }
    }

    private async void OnForgetClicked(object sender, RoutedEventArgs e)
    {
        string? webSignInError = null;
        try
        {
            await App.Player.ForgetMusicKitAuthorizationAsync();
        }
        catch
        {
            webSignInError = " The MusicKit WebView sign-in session could not be cleared.";
        }

        try
        {
            App.Credentials.Clear();
            App.MusicApi.ResetSession();
            DeveloperTokenBox.Password = "";
            UserTokenBox.Password = "";
            CookieBox.Password = "";
            DeveloperTeamIdBox.Text = "";
            DeveloperKeyIdBox.Text = "";
            _selectedDeveloperPrivateKeyPem = null;
            UpdateDeveloperKeyStatus();
            UpdateConnectionSummary();
            StatusText.Text = $"Saved Apple Music credentials were removed.{webSignInError}";
        }
        catch
        {
            StatusText.Text = "Windows could not remove the saved credentials.";
        }
    }

    private void OnForgetWebCookieClicked(object sender, RoutedEventArgs e)
    {
        try
        {
            App.Credentials.ClearWebCookie();
            UpdateConnectionSummary();
            StatusText.Text = "The saved full web cookie was removed. Its Music User Token remains saved for library API access.";
        }
        catch
        {
            StatusText.Text = "Windows could not remove the saved web cookie.";
        }
    }

    private void UpdateStatus()
    {
        UpdateDeveloperKeyStatus();
        StatusText.Text = App.Credentials.HasAnyCredential
            ? App.Credentials.HasWebCookie
                ? "Apple Music credentials are saved securely. The web cookie is protected with Windows DPAPI."
                : "Apple Music credentials are saved securely in Windows Credential Manager."
            : "Credentials have not been saved.";
    }

    private void UpdateConnectionSummary()
    {
        var hasDeveloperToken = App.Credentials.HasDeveloperToken;
        var hasUserToken = App.Credentials.HasUserCredential;
        var hasWebCookie = App.Credentials.HasWebCookie;
        ForgetWebCookieButton.IsEnabled = hasWebCookie;

        DeveloperTokenSummary.Text = hasDeveloperToken
            ? App.Credentials.HasSavedDeveloperPrivateKey
                ? "Developer token: Ready · signed locally from the saved key"
                : "Developer token: Ready"
            : "Developer token: Needed for Apple Music API access";

        UserTokenSummary.Text = hasUserToken
            ? "Music User Token: Ready for personal library requests"
            : "Music User Token: Needed for personal library requests";

        WebCookieSummary.Text = hasWebCookie
            ? "Web cookie: Saved securely; only its Music User Token is sent to the API"
            : "Web cookie: Not saved (optional)";

        AccessSummary.Text = hasDeveloperToken && hasUserToken
            ? "Catalog and personal library API access are configured."
            : hasDeveloperToken
                ? "Catalog API access is configured. Add a Music User Token for your library."
                : hasUserToken
                    ? "Music User Token is saved. Add a developer token to use the API."
                    : "Add a developer token for catalog access and a Music User Token for your library.";

        MusicKitPlaybackSummary.Text = !hasDeveloperToken
            ? "Full-track playback uses MusicKit on the Web. Add a developer token or a .p8 signing key, then authorize Apple Music."
            : App.Player.IsMusicKitAuthorized
                ? "Full-track playback is authorized. Catalog and library songs and supported music videos can stream through MusicKit."
                : "Full-track playback is ready to authorize. MusicKit on the Web stores its sign-in in the local Edge WebView2 profile.";
        AuthorizeMusicKitButton.IsEnabled = hasDeveloperToken;
    }

    private void UpdateDeveloperKeyStatus()
    {
        if (App.Credentials.HasSavedDeveloperPrivateKey)
        {
            var teamId = App.Credentials.DeveloperTeamId;
            var keyId = App.Credentials.DeveloperKeyId;
            DeveloperKeyStatus.Text = $"Saved key in Credential Manager · Team {teamId} · Key {keyId}. Blank fields keep it.";
        }
        else
        {
            DeveloperKeyStatus.Text = "No signing key is saved.";
        }
    }
}

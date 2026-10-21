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
        UpdateStatus();
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
            App.Credentials.Save(
                DeveloperTokenBox.Password,
                UserTokenBox.Password,
                CookieBox.Password,
                DeveloperTeamIdBox.Text,
                DeveloperKeyIdBox.Text,
                _selectedDeveloperPrivateKeyPem);
            App.MusicApi.ResetSession();
            DeveloperTokenBox.Password = "";
            UserTokenBox.Password = "";
            CookieBox.Password = "";
            DeveloperTeamIdBox.Text = "";
            DeveloperKeyIdBox.Text = "";
            _selectedDeveloperPrivateKeyPem = null;
            UpdateDeveloperKeyStatus();
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

    private void OnForgetClicked(object sender, RoutedEventArgs e)
    {
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
            StatusText.Text = "Saved Apple Music credentials were removed.";
        }
        catch
        {
            StatusText.Text = "Windows could not remove the saved credentials.";
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

using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace AmNative.Pages;

public sealed partial class SettingsPage : Page
{
    public SettingsPage()
    {
        InitializeComponent();
        UpdateStatus();
    }

    private void OnSaveClicked(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(DeveloperTokenBox.Password) &&
            string.IsNullOrWhiteSpace(App.Credentials.DeveloperToken))
        {
            StatusText.Text = "A developer token is required for Apple Music API requests.";
            return;
        }

        try
        {
            App.Credentials.Save(DeveloperTokenBox.Password, UserTokenBox.Password, CookieBox.Password);
            App.MusicApi.ResetSession();
            DeveloperTokenBox.Password = "";
            UserTokenBox.Password = "";
            CookieBox.Password = "";
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
            StatusText.Text = "Saved Apple Music credentials were removed.";
        }
        catch
        {
            StatusText.Text = "Windows could not remove the saved credentials.";
        }
    }

    private void UpdateStatus()
    {
        StatusText.Text = App.Credentials.HasAnyCredential
            ? App.Credentials.HasWebCookie
                ? "Apple Music credentials are saved securely. The web cookie is protected with Windows DPAPI."
                : "Apple Music credentials are saved securely in Windows Credential Manager."
            : "Credentials have not been saved.";
    }
}

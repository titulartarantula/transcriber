using System.Globalization;
using System.Windows;
using Microsoft.Win32;
using Transcriber.Core;
using Transcriber.Core.Output;
using Transcriber.Core.Output.Mcp;
using Transcriber.Core.Settings;
using Transcriber.Core.Stt;
using Wpf.Ui.Controls;

namespace Transcriber.App;

public partial class SettingsWindow : FluentWindow
{
    public SettingsWindow(AppSettings settings)
    {
        InitializeComponent();
        Result = settings;
        VersionText.Text = $"Transcriber for Windows {BuildInfo.From(typeof(SettingsWindow).Assembly)}";

        SttUrl.Text = settings.Stt.BaseUrl;
        SttKey.Password = settings.Stt.ApiKey;
        SttModel.Text = settings.Stt.Model;
        SttLanguage.Text = settings.Stt.Language;
        SttPrompt.Text = settings.Stt.Prompt;
        SttVad.IsChecked = settings.Stt.VadFilter;

        Diarize.IsChecked = settings.Speakers.Diarize;
        Threshold.Value = settings.Speakers.ClusterThreshold;
        ReviewNames.IsChecked = settings.Speakers.ReviewNames;
        SuppressEcho.IsChecked = settings.Speakers.SuppressEcho;

        Folder.Text = settings.Output.MarkdownFolder;
        ObsidianUrl.Text = settings.Output.ObsidianUrl;
        ObsidianKey.Password = settings.Output.ObsidianApiKey;
        ObsidianFolder.Text = settings.Output.ObsidianFolder;
        FileNameTemplate.Text = settings.Output.FileNameTemplate;
        Tags.Text = settings.Output.Tags;
        LinkSpeakers.IsChecked = settings.Output.LinkSpeakers;
        McpUrl.Text = settings.Output.McpUrl;
        (settings.Output.Destination switch
        {
            OutputKind.Obsidian => ToObsidian,
            OutputKind.ObsidianMcp => ToMcp,
            _ => ToFolder,
        }).IsChecked = true;
        ShowPin();
        UpdateMcpStatus();
        McpUrl.TextChanged += (_, _) => UpdateMcpStatus();
        OnDestinationChanged(this, new RoutedEventArgs());
    }

    public AppSettings Result { get; }

    private void Apply()
    {
        var s = Result;
        s.Stt.BaseUrl = SttUrl.Text.Trim();
        s.Stt.ApiKey = SttKey.Password;
        s.Stt.Model = SttModel.Text.Trim();
        s.Stt.Language = SttLanguage.Text.Trim();
        s.Stt.Prompt = SttPrompt.Text.Trim();
        s.Stt.VadFilter = SttVad.IsChecked == true;

        s.Speakers.Diarize = Diarize.IsChecked == true;
        s.Speakers.ClusterThreshold = (float)Math.Round(Threshold.Value, 2);
        s.Speakers.ReviewNames = ReviewNames.IsChecked == true;
        s.Speakers.SuppressEcho = SuppressEcho.IsChecked == true;

        s.Output.Destination = ToObsidian.IsChecked == true ? OutputKind.Obsidian
            : ToMcp.IsChecked == true ? OutputKind.ObsidianMcp
            : OutputKind.MarkdownFolder;
        s.Output.McpUrl = McpUrl.Text.Trim();
        s.Output.MarkdownFolder = Folder.Text.Trim();
        s.Output.ObsidianUrl = ObsidianUrl.Text.Trim();
        s.Output.ObsidianApiKey = ObsidianKey.Password;
        s.Output.ObsidianFolder = ObsidianFolder.Text.Trim();
        s.Output.FileNameTemplate = FileNameTemplate.Text.Trim();
        s.Output.Tags = Tags.Text.Trim();
        s.Output.LinkSpeakers = LinkSpeakers.IsChecked == true;
    }

    private void OnSave(object sender, RoutedEventArgs e)
    {
        Apply();
        if (string.IsNullOrWhiteSpace(Result.Stt.Model))
        {
            ShowResult(SttResult, "Choose a model.");
            return;
        }
        if (string.IsNullOrWhiteSpace(Result.Output.MarkdownFolder))
            Result.Output.MarkdownFolder = new OutputSettings().MarkdownFolder;
        DialogResult = true;
    }

    private void OnDestinationChanged(object sender, RoutedEventArgs e)
    {
        if (FolderPanel is null || ObsidianPanel is null || McpPanel is null || VaultPanel is null) return;
        static Visibility Show(bool on) => on ? Visibility.Visible : Visibility.Collapsed;
        bool rest = ToObsidian.IsChecked == true, mcp = ToMcp.IsChecked == true;
        FolderPanel.Visibility = Show(!rest && !mcp);
        ObsidianPanel.Visibility = Show(rest);
        McpPanel.Visibility = Show(mcp);
        VaultPanel.Visibility = Show(rest || mcp);
        ObsidianResult.Visibility = Visibility.Collapsed;
    }

    private void OnBrowse(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog { Title = "Where should transcripts be saved?" };
        if (Directory.Exists(Folder.Text)) dialog.InitialDirectory = Folder.Text;
        if (dialog.ShowDialog(this) == true) Folder.Text = dialog.FolderName;
    }

    private async void OnTestStt(object sender, RoutedEventArgs e)
    {
        Apply();
        ShowResult(SttResult, "Connecting…");
        try
        {
            using var client = new WhisperClient(Result.Stt);
            var models = await client.ListModelsAsync();
            var current = SttModel.Text;
            SttModel.ItemsSource = models;
            SttModel.Text = current;
            ShowResult(SttResult, models.Count == 0
                ? "Connected, but the server listed no models. Type a model name."
                : $"Connected. {models.Count.ToString(CultureInfo.CurrentCulture)} models available.");
        }
        catch (Exception ex)
        {
            ShowResult(SttResult, ex.Message);
        }
    }

    private async void OnTestObsidian(object sender, RoutedEventArgs e)
    {
        Apply();
        ShowResult(ObsidianResult, "Connecting…");
        try
        {
            ShowResult(ObsidianResult, await TestObsidianAsync());
        }
        catch (UntrustedCertificateException cert) when (ConfirmTrust(cert))
        {
            Result.Output.ObsidianCertificate = cert.Fingerprint;
            ShowPin();
            try
            {
                ShowResult(ObsidianResult, await TestObsidianAsync());
            }
            catch (Exception ex)
            {
                ShowResult(ObsidianResult, ex.Message);
            }
        }
        catch (Exception ex)
        {
            ShowResult(ObsidianResult, ex.Message);
        }
    }

    private async Task<string> TestObsidianAsync()
    {
        var o = Result.Output;
        if (o.Destination == OutputKind.ObsidianMcp) return await new McpDestination(o.McpUrl, o.ObsidianFolder).TestAsync();
        using var obsidian = new ObsidianDestination(o.ObsidianUrl, o.ObsidianApiKey, o.ObsidianFolder, o.ObsidianCertificate);
        return await obsidian.TestAsync();
    }

    /// <summary>Trust on first use: show the fingerprint and let the user decide.</summary>
    private bool ConfirmTrust(UntrustedCertificateException cert)
    {
        var intro = cert.PinChanged
            ? $"The certificate at {cert.Host} is DIFFERENT from the one you trusted before. That's expected only if you regenerated it in the Local REST API plugin settings."
            : $"{cert.Host} uses the Local REST API plugin's self-signed certificate, which Windows can't verify by itself.";
        var answer = System.Windows.MessageBox.Show(this,
            $"{intro}\n\nSHA-256 fingerprint:\n{ObsidianDestination.Format(cert.Fingerprint)}\n\n" +
            "Trust this certificate for Obsidian? Only this exact certificate will be accepted from now on.",
            cert.PinChanged ? "Certificate changed" : "Trust Obsidian's certificate?",
            System.Windows.MessageBoxButton.YesNo,
            cert.PinChanged ? System.Windows.MessageBoxImage.Warning : System.Windows.MessageBoxImage.Question);
        return answer == System.Windows.MessageBoxResult.Yes;
    }

    private async void OnMcpSignIn(object sender, RoutedEventArgs e)
    {
        var url = McpUrl.Text.Trim();
        McpSignIn.IsEnabled = false;
        McpStatus.Text = "Waiting for you to approve in the browser…";
        try
        {
            await McpAuth.SignInAsync(url, uri => System.Diagnostics.Process.Start(
                new System.Diagnostics.ProcessStartInfo(uri.ToString()) { UseShellExecute = true }), default);
            UpdateMcpStatus();
            Apply();
            ShowResult(ObsidianResult, await TestObsidianAsync());
        }
        catch (OperationCanceledException)
        {
            UpdateMcpStatus("Sign-in timed out. Try again.");
        }
        catch (Exception ex)
        {
            UpdateMcpStatus(ex.Message);
        }
        finally
        {
            McpSignIn.IsEnabled = true;
        }
    }

    private void OnMcpSignOut(object sender, RoutedEventArgs e)
    {
        McpCredentialStore.Clear();
        UpdateMcpStatus();
    }

    private void UpdateMcpStatus(string? problem = null)
    {
        var url = McpUrl.Text.Trim();
        McpCredentials? creds = null;
        try
        {
            if (url.Length > 0) creds = McpCredentialStore.Load(url);
        }
        catch (Exception)
        {
            // An unreadable credential file just means signing in again.
        }

        bool signedIn = creds is not null;
        McpStatus.Text = problem ?? (url.Length == 0 ? "Enter the server URL, then sign in."
            : signedIn ? $"Signed in to {new Uri(url).Host}."
            : "Not signed in.");
        McpStatusIcon.Symbol = signedIn ? SymbolRegular.CheckmarkCircle24 : SymbolRegular.PersonCircle24;
        McpSignIn.Content = signedIn ? "Sign in again" : "Sign in";
        McpSignIn.IsEnabled = Uri.TryCreate(url, UriKind.Absolute, out _);
        McpSignOut.Visibility = signedIn ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OnForgetCertificate(object sender, RoutedEventArgs e)
    {
        Result.Output.ObsidianCertificate = null;
        ShowPin();
    }

    private void ShowPin()
    {
        var pin = Result.Output.ObsidianCertificate;
        PinPanel.Visibility = string.IsNullOrEmpty(pin) ? Visibility.Collapsed : Visibility.Visible;
        PinText.Text = string.IsNullOrEmpty(pin) ? "" : "Trusted certificate  " + ObsidianDestination.Format(pin)[..23] + "…";
        PinText.ToolTip = string.IsNullOrEmpty(pin) ? null : ObsidianDestination.Format(pin);
    }

    private static void ShowResult(System.Windows.Controls.TextBlock target, string text)
    {
        target.Text = text;
        target.Visibility = string.IsNullOrEmpty(text) ? Visibility.Collapsed : Visibility.Visible;
    }
}

using Transcriber.Core;
using Transcriber.Core.Output;
using Transcriber.Core.Output.Mcp;
using Transcriber.Core.Settings;
using Transcriber.Core.Stt;

namespace Transcriber.Mobile;

public partial class SettingsPage : ContentPage
{
    private readonly AppSettings _settings = SettingsStore.Load();
    private string _folderUri;

    public SettingsPage()
    {
        InitializeComponent();
        var build = BuildInfo.From(typeof(SettingsPage).Assembly);
        VersionLabel.Text = $"Transcriber for Android {build.Version} · build {AppInfo.Current.BuildString}"
            + (build.Commit is null ? "" : $" · {build.Commit}");
        var s = _settings;
        SttUrl.Text = s.Stt.BaseUrl;
        SttKey.Text = s.Stt.ApiKey;
        SttModel.Text = s.Stt.Model;
        SttLanguage.Text = s.Stt.Language;
        SttPrompt.Text = s.Stt.Prompt;
        SttVad.IsToggled = s.Stt.VadFilter;

        Threshold.Value = s.Speakers.ClusterThreshold;
        ReviewNames.IsToggled = s.Speakers.ReviewNames;
        UseServer.IsToggled = s.Speakers.UseServer;

        McpUrl.Text = s.Output.McpUrl;
        RestUrl.Text = s.Output.ObsidianUrl;
        RestKey.Text = s.Output.ObsidianApiKey;
        VaultFolder.Text = s.Output.ObsidianFolder;
        _folderUri = s.Output.MarkdownFolder;
        FolderLabel.Text = PhoneFolder.Describe(_folderUri);
        FileNameTemplate.Text = s.Output.FileNameTemplate;
        Tags.Text = s.Output.Tags;
        LinkSpeakers.IsToggled = s.Output.LinkSpeakers;
        KeepAudio.IsToggled = s.Output.KeepAudio;

        (s.Output.Destination switch
        {
            OutputKind.ObsidianMcp => ToMcp,
            OutputKind.Obsidian => ToRest,
            _ => ToFolder,
        }).IsChecked = true;
        ShowPanels();
        ShowPin();
        UpdateMcpStatus();
    }

    protected override void OnAppearing()
    {
        base.OnAppearing();
        var exempt = BatteryOptimization.IsExempt;
        BatteryStatus.Text = exempt ? "✓ Allowed to run in the background without limits" : "Battery optimization is on for Transcriber";
        BatteryButton.IsVisible = !exempt;
    }

    private void OnBattery(object? sender, EventArgs e) => BatteryOptimization.RequestExemption();

    private void Apply()
    {
        var s = _settings;
        s.Stt.BaseUrl = SttUrl.Text?.Trim() ?? "";
        s.Stt.ApiKey = SttKey.Text ?? "";
        s.Stt.Model = SttModel.Text?.Trim() ?? "";
        s.Stt.Language = SttLanguage.Text?.Trim() ?? "";
        s.Stt.Prompt = SttPrompt.Text?.Trim() ?? "";
        s.Stt.VadFilter = SttVad.IsToggled;

        s.Speakers.ClusterThreshold = (float)Math.Round(Threshold.Value, 2);
        s.Speakers.ReviewNames = ReviewNames.IsToggled;
        s.Speakers.UseServer = UseServer.IsToggled;

        s.Output.Destination = ToMcp.IsChecked ? OutputKind.ObsidianMcp : ToRest.IsChecked ? OutputKind.Obsidian : OutputKind.MarkdownFolder;
        s.Output.McpUrl = McpUrl.Text?.Trim() ?? "";
        s.Output.ObsidianUrl = RestUrl.Text?.Trim() ?? "";
        s.Output.ObsidianApiKey = RestKey.Text ?? "";
        s.Output.ObsidianFolder = VaultFolder.Text?.Trim() ?? "";
        s.Output.MarkdownFolder = _folderUri;
        s.Output.FileNameTemplate = FileNameTemplate.Text?.Trim() ?? "";
        s.Output.Tags = Tags.Text?.Trim() ?? "";
        s.Output.LinkSpeakers = LinkSpeakers.IsToggled;
        s.Output.KeepAudio = KeepAudio.IsToggled;
    }

    private async void OnSave(object? sender, EventArgs e)
    {
        Apply();
        if (string.IsNullOrWhiteSpace(_settings.Stt.Model))
        {
            Show(SttResult, "Choose a model.");
            return;
        }
        SettingsStore.Save(_settings);
        await Navigation.PopAsync();
    }

    // ----- Speech to text ------------------------------------------------------------------

    private async void OnTestStt(object? sender, EventArgs e)
    {
        Apply();
        Show(SttResult, "Connecting…");
        try
        {
            using var client = new WhisperClient(_settings.Stt);
            var models = await client.ListModelsAsync();
            var voices = await client.SupportsDiarizationAsync()
                ? "It separates voices itself."
                : "It doesn't separate voices, so the phone will.";
            Show(SttResult, $"Connected. {models.Count} models available. {voices}");
            if (models.Count == 0) return;

            // Common general-purpose models first; the full catalogue can run to hundreds.
            var ordered = models.OrderBy(m => m.Contains("large-v3-turbo") ? 0 : m.StartsWith("Systran/") ? 1 : 2).ThenBy(m => m).ToArray();
            var pick = await DisplayActionSheetAsync("Model", "Keep current", null, ordered);
            if (pick is not null && pick != "Keep current") SttModel.Text = pick;
        }
        catch (Exception ex)
        {
            Show(SttResult, ex.Message);
        }
    }

    // ----- Output --------------------------------------------------------------------------

    private void OnDestinationChanged(object? sender, CheckedChangedEventArgs e)
    {
        if (e.Value) ShowPanels();
    }

    private void ShowPanels()
    {
        if (McpPanel is null) return;
        McpPanel.IsVisible = ToMcp.IsChecked;
        RestPanel.IsVisible = ToRest.IsChecked;
        VaultPanel.IsVisible = ToMcp.IsChecked || ToRest.IsChecked;
        FolderPanel.IsVisible = ToFolder.IsChecked;
        VaultResult.IsVisible = false;
    }

    private async void OnPickFolder(object? sender, EventArgs e)
    {
        var uri = await PhoneFolder.PickAsync();
        if (uri is null) return;
        _folderUri = uri;
        FolderLabel.Text = PhoneFolder.Describe(uri);
    }

    private async void OnTestVault(object? sender, EventArgs e)
    {
        Apply();
        Show(VaultResult, "Connecting…");
        try
        {
            Show(VaultResult, await TestVaultAsync());
        }
        catch (UntrustedCertificateException cert)
        {
            if (!await ConfirmTrustAsync(cert))
            {
                Show(VaultResult, cert.Message);
                return;
            }
            _settings.Output.ObsidianCertificate = cert.Fingerprint;
            ShowPin();
            try
            {
                Show(VaultResult, await TestVaultAsync());
            }
            catch (Exception ex)
            {
                Show(VaultResult, ex.Message);
            }
        }
        catch (Exception ex)
        {
            Show(VaultResult, ex.Message);
        }
    }

    private async Task<string> TestVaultAsync()
    {
        var o = _settings.Output;
        if (o.Destination == OutputKind.ObsidianMcp) return await new McpDestination(o.McpUrl, o.ObsidianFolder).TestAsync();
        using var rest = new ObsidianDestination(o.ObsidianUrl, o.ObsidianApiKey, o.ObsidianFolder, o.ObsidianCertificate);
        return await rest.TestAsync();
    }

    private Task<bool> ConfirmTrustAsync(UntrustedCertificateException cert)
    {
        var intro = cert.PinChanged
            ? $"The certificate at {cert.Host} is DIFFERENT from the one you trusted before. That's expected only if you regenerated it in the plugin settings."
            : $"{cert.Host} uses the Local REST API plugin's self-signed certificate, which Android can't verify by itself.";
        return DisplayAlertAsync(
            cert.PinChanged ? "Certificate changed" : "Trust Obsidian's certificate?",
            $"{intro}\n\nSHA-256:\n{ObsidianDestination.Format(cert.Fingerprint)}\n\nOnly this exact certificate will be accepted from now on.",
            "Trust", "Cancel");
    }

    private void OnForgetCertificate(object? sender, EventArgs e)
    {
        _settings.Output.ObsidianCertificate = null;
        ShowPin();
    }

    private void ShowPin()
    {
        var pin = _settings.Output.ObsidianCertificate;
        PinRow.IsVisible = !string.IsNullOrEmpty(pin);
        PinText.Text = string.IsNullOrEmpty(pin) ? "" : "Trusted certificate " + ObsidianDestination.Format(pin)[..23] + "…";
    }

    // ----- MCP sign-in ---------------------------------------------------------------------

    private void OnMcpUrlChanged(object? sender, TextChangedEventArgs e) => UpdateMcpStatus();

    private async void OnMcpSignIn(object? sender, EventArgs e)
    {
        var url = McpUrl.Text?.Trim() ?? "";
        McpSignIn.IsEnabled = false;
        McpStatus.Text = "Approve in the browser, then come back here…";
        try
        {
            await McpAuth.SignInAsync(url,
                uri => MainThread.BeginInvokeOnMainThread(async () => await Browser.Default.OpenAsync(uri, BrowserLaunchMode.SystemPreferred)),
                default);
            UpdateMcpStatus();
            Apply();
            Show(VaultResult, await TestVaultAsync());
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

    private void OnMcpSignOut(object? sender, EventArgs e)
    {
        McpCredentialStore.Clear();
        UpdateMcpStatus();
    }

    private void UpdateMcpStatus(string? problem = null)
    {
        var url = McpUrl.Text?.Trim() ?? "";
        McpCredentials? creds = null;
        try
        {
            if (url.Length > 0) creds = McpCredentialStore.Load(url);
        }
        catch (Exception)
        {
            // Unreadable credentials just mean signing in again.
        }

        bool valid = Uri.TryCreate(url, UriKind.Absolute, out var parsed);
        McpStatus.Text = problem ?? (url.Length == 0 ? "Enter the server URL, then sign in."
            : creds is not null ? $"✓ Signed in to {parsed!.Host}"
            : "Not signed in.");
        McpSignIn.Text = creds is not null ? "Sign in again" : "Sign in";
        McpSignIn.IsEnabled = valid;
        McpSignOut.IsVisible = creds is not null;
    }

    private static void Show(Label label, string text)
    {
        label.Text = text;
        label.IsVisible = text.Length > 0;
    }
}

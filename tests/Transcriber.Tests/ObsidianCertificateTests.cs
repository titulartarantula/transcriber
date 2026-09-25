using Transcriber.Core.Output;
using Xunit;

namespace Transcriber.Tests;

public class ObsidianCertificateTests
{
    [Fact]
    public void Formats_fingerprint_like_certificate_viewers() =>
        Assert.Equal("AE:D2:5E", ObsidianDestination.Format("AED25E"));

    /// <summary>
    /// TLS behaviour against a real Local REST API plugin reached by a non-loopback address. Set
    /// TRANSCRIBER_OBSIDIAN_URL (e.g. https://192.168.1.100:27124) to enable. No API key is needed:
    /// getting as far as "rejected the API key" proves the TLS handshake was accepted.
    /// </summary>
    [Fact]
    public async Task Remote_certificate_is_rejected_until_pinned()
    {
        var url = Environment.GetEnvironmentVariable("TRANSCRIBER_OBSIDIAN_URL");
        if (string.IsNullOrEmpty(url)) return;

        // Unpinned: refused, and the exception carries the fingerprint to show the user.
        var first = await Assert.ThrowsAsync<UntrustedCertificateException>(() => Test(url, pin: null));
        Assert.False(first.PinChanged);
        Assert.Equal(64, first.Fingerprint.Length);

        // Pinned (in either display form): TLS passes, and the fake key is what fails.
        var pinned = await Assert.ThrowsAsync<InvalidOperationException>(() => Test(url, ObsidianDestination.Format(first.Fingerprint)));
        Assert.Contains("rejected the API key", pinned.Message);

        // A different pin means the certificate changed: refuse and say so.
        var wrong = await Assert.ThrowsAsync<UntrustedCertificateException>(() => Test(url, new string('0', 64)));
        Assert.True(wrong.PinChanged);

        // Loopback is still accepted without any pin.
        var local = new UriBuilder(url) { Host = "127.0.0.1" }.Uri.ToString();
        var loopback = await Assert.ThrowsAsync<InvalidOperationException>(() => Test(local, pin: null));
        Assert.Contains("rejected the API key", loopback.Message);
    }

    private static async Task Test(string url, string? pin)
    {
        using var d = new ObsidianDestination(url, "not-a-real-key", "Transcripts", pin);
        await d.TestAsync();
    }
}

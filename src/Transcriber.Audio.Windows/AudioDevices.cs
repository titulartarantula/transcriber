using System.Runtime.InteropServices;
using NAudio.CoreAudioApi;

using Transcriber.Core.Audio;

namespace Transcriber.Audio.Windows;

public sealed record AudioDeviceInfo(string Id, string Name, SourceKind Kind, bool IsDefault);

public static class AudioDevices
{
    /// <summary>Active capture and render endpoints, defaults first within each kind.</summary>
    public static IReadOnlyList<AudioDeviceInfo> List()
    {
        using var enumerator = new MMDeviceEnumerator();
        var list = new List<AudioDeviceInfo>();
        Collect(enumerator, DataFlow.Capture, SourceKind.Microphone, list);
        Collect(enumerator, DataFlow.Render, SourceKind.SystemAudio, list);
        return list;
    }

    /// <summary>
    /// True inside a Remote Desktop session, where Windows replaces the host's audio hardware with
    /// whatever the RDP client redirects. Microphones only appear if the client redirects recording.
    /// </summary>
    public static bool IsRemoteDesktopSession => GetSystemMetrics(SM_REMOTESESSION) != 0;

    private const int SM_REMOTESESSION = 0x1000;

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int index);

    public static MMDevice Open(string id)
    {
        using var enumerator = new MMDeviceEnumerator();
        return enumerator.GetDevice(id);
    }

    private static void Collect(MMDeviceEnumerator enumerator, DataFlow flow, SourceKind kind, List<AudioDeviceInfo> into)
    {
        string? defaultId = null;
        try
        {
            if (enumerator.HasDefaultAudioEndpoint(flow, Role.Multimedia))
            {
                using var d = enumerator.GetDefaultAudioEndpoint(flow, Role.Multimedia);
                defaultId = d.ID;
            }
        }
        catch (COMException)
        {
            // No default endpoint for this flow; nothing is marked default.
        }

        var found = new List<AudioDeviceInfo>();
        foreach (var device in enumerator.EnumerateAudioEndPoints(flow, DeviceState.Active))
        {
            using (device)
                found.Add(new AudioDeviceInfo(device.ID, device.FriendlyName, kind, device.ID == defaultId));
        }

        into.AddRange(found.OrderByDescending(d => d.IsDefault).ThenBy(d => d.Name, StringComparer.CurrentCultureIgnoreCase));
    }
}

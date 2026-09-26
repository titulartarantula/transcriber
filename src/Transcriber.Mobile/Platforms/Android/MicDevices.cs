using Android.Content;
using Android.Media;

namespace Transcriber.Mobile;

public enum MicKind { Phone, Bluetooth, Wired, Usb }

/// <summary>A selectable input. <see cref="Key"/> is stable across reconnects; device ids are not.</summary>
public sealed record MicOption(string Key, string Name, MicKind Kind, int DeviceId)
{
    public string Display => Kind switch
    {
        MicKind.Bluetooth => $"{Name} · Bluetooth",
        MicKind.Usb => $"{Name} · USB",
        _ => Name,
    };
}

public static class MicDevices
{
    private static AudioManager Audio => (AudioManager)Platform.AppContext.GetSystemService(Context.AudioService)!;

    /// <summary>The phone's own mic first, then any headset mics that are connected right now.</summary>
    public static IReadOnlyList<MicOption> List()
    {
        var result = new List<MicOption>();
        foreach (var d in Audio.GetDevices(GetDevicesTargets.Inputs) ?? [])
        {
            var name = d.ProductNameFormatted?.ToString();
            MicOption? option = d.Type switch
            {
                AudioDeviceType.BuiltinMic => new MicOption("phone", "Phone microphone", MicKind.Phone, d.Id),
                AudioDeviceType.BluetoothSco or AudioDeviceType.BleHeadset =>
                    new MicOption("bt:" + name, string.IsNullOrWhiteSpace(name) ? "Bluetooth headset" : name, MicKind.Bluetooth, d.Id),
                AudioDeviceType.WiredHeadset => new MicOption("wired", "Wired headset", MicKind.Wired, d.Id),
                AudioDeviceType.UsbDevice or AudioDeviceType.UsbHeadset =>
                    new MicOption("usb:" + name, string.IsNullOrWhiteSpace(name) ? "USB microphone" : name, MicKind.Usb, d.Id),
                _ => null,
            };
            // Phones expose several built-in mics and a headset can appear as both SCO and BLE; keep one of each.
            if (option is not null && result.All(o => o.Key != option.Key)) result.Add(option);
        }
        return result.OrderBy(o => o.Kind == MicKind.Phone ? 0 : 1).ToList();
    }

    public static AudioDeviceInfo? Find(int deviceId) =>
        Audio.GetDevices(GetDevicesTargets.Inputs)?.FirstOrDefault(d => d.Id == deviceId);

    /// <summary>Raises <paramref name="changed"/> on the main thread when inputs are plugged in or removed.</summary>
    public static IDisposable Watch(Action changed)
    {
        var callback = new DeviceCallback(changed);
        Audio.RegisterAudioDeviceCallback(callback, null);
        return callback;
    }

    private sealed class DeviceCallback(Action changed) : AudioDeviceCallback, IDisposable
    {
        public override void OnAudioDevicesAdded(AudioDeviceInfo[]? addedDevices) => MainThread.BeginInvokeOnMainThread(changed);

        public override void OnAudioDevicesRemoved(AudioDeviceInfo[]? removedDevices) => MainThread.BeginInvokeOnMainThread(changed);

        void IDisposable.Dispose()
        {
            Audio.UnregisterAudioDeviceCallback(this);
            base.Dispose();
        }
    }
}

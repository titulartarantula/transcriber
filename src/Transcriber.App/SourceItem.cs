using System.ComponentModel;
using System.Runtime.CompilerServices;
using NAudio.CoreAudioApi;
using Transcriber.Core.Audio;
using Wpf.Ui.Controls;

namespace Transcriber.App;

/// <summary>One row in the source list: a device plus the user's choices for it.</summary>
public sealed class SourceItem(AudioDeviceInfo device, bool selected, string label, bool diarize)
    : INotifyPropertyChanged, IDisposable
{
    private MMDevice? _meter;
    private bool _meterFailed;
    private bool _selected = selected;
    private string _label = label;
    private bool _diarize = diarize;
    private double _level;

    public event PropertyChangedEventHandler? PropertyChanged;

    public AudioDeviceInfo Device { get; } = device;

    public string Name => Device.Name;

    public string Subtitle =>
        (Device.Kind == SourceKind.Microphone ? "Microphone" : "System audio: everything playing on this output")
        + (Device.IsDefault ? " · default" : "");

    public SymbolRegular Icon => Device.Kind == SourceKind.Microphone ? SymbolRegular.Mic24 : SymbolRegular.Speaker224;

    public string DefaultLabel { get; init; } = label;

    public bool Selected { get => _selected; set => Set(ref _selected, value); }

    public string Label { get => _label; set => Set(ref _label, value); }

    public bool Diarize { get => _diarize; set => Set(ref _diarize, value); }

    /// <summary>0..1, already scaled for display.</summary>
    public double Level { get => _level; set => Set(ref _level, value); }

    public string EffectiveLabel => string.IsNullOrWhiteSpace(Label) ? DefaultLabel : Label.Trim();

    /// <summary>
    /// Windows' own peak meter for the endpoint. Outputs report whatever is playing; microphones
    /// only report while some app has them open.
    /// </summary>
    public float ReadSystemMeter()
    {
        if (_meterFailed) return 0;
        try
        {
            _meter ??= AudioDevices.Open(Device.Id);
            return _meter.AudioMeterInformation.MasterPeakValue;
        }
        catch (Exception)
        {
            _meterFailed = true;
            return 0;
        }
    }

    public void Dispose() => _meter?.Dispose();

    private void Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}

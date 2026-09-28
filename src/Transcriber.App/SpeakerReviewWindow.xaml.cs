using System.Windows;
using Transcriber.Core.Output;
using Transcriber.Core.Transcript;
using Wpf.Ui.Controls;

namespace Transcriber.App;

public sealed class SpeakerRow(SpeakerSummary summary)
{
    public string Speaker => summary.Speaker;

    public string TalkTime => $"· {MarkdownRenderer.Clock(summary.TalkTime)} talking";

    public string Sample => $"“{summary.Sample}”";

    public string Hint => summary.SuggestedName is null ? "" : $"Suggested from the conversation: {summary.SuggestedName}";

    public string Name { get; set; } = summary.SuggestedName ?? "";
}

public partial class SpeakerReviewWindow : FluentWindow
{
    private readonly List<SpeakerRow> _rows;

    public SpeakerReviewWindow(IReadOnlyList<SpeakerSummary> speakers)
    {
        InitializeComponent();
        _rows = speakers.Select(s => new SpeakerRow(s)).ToList();
        SpeakerList.ItemsSource = _rows;
    }

    /// <summary>Label → name after Save; empty after Keep labels; null if the window was closed.</summary>
    public IReadOnlyDictionary<string, string>? Names { get; private set; }

    private void OnKeep(object sender, RoutedEventArgs e)
    {
        Names = new Dictionary<string, string>();
        DialogResult = true;
    }

    private void OnSave(object sender, RoutedEventArgs e)
    {
        Names = _rows
            .Where(r => !string.IsNullOrWhiteSpace(r.Name) && r.Name.Trim() != r.Speaker)
            .ToDictionary(r => r.Speaker, r => r.Name.Trim());
        DialogResult = true;
    }
}

using Transcriber.Core.Output;
using Transcriber.Core.Transcript;

namespace Transcriber.Mobile;

public sealed class SpeakerRow(SpeakerSummary summary)
{
    public string Speaker => summary.Speaker;

    public string TalkTime => $"  ·  {MarkdownRenderer.Clock(summary.TalkTime)} talking";

    public string Sample => $"“{summary.Sample}”";

    public bool HasHint => summary.SuggestedName is not null;

    public string Hint => $"Suggested from the conversation: {summary.SuggestedName}";

    public string Name { get; set; } = summary.SuggestedName ?? "";
}

public partial class SpeakerReviewPage : ContentPage
{
    private readonly List<SpeakerRow> _rows;
    private readonly TaskCompletionSource<IReadOnlyDictionary<string, string>?> _result = new();

    public SpeakerReviewPage(IReadOnlyList<SpeakerSummary> speakers, string? recordingTitle = null)
    {
        InitializeComponent();
        if (recordingTitle is not null) Heading.Text = $"Who was speaking in “{recordingTitle}”?";
        _rows = speakers.Select(s => new SpeakerRow(s)).ToList();
        Speakers.ItemsSource = _rows;
    }

    /// <summary>Speaker → chosen name; empty to keep the automatic labels; null if the user backed out to name them later.</summary>
    public Task<IReadOnlyDictionary<string, string>?> Result => _result.Task;

    private async void OnSave(object? sender, EventArgs e)
    {
        var names = _rows
            .Where(r => !string.IsNullOrWhiteSpace(r.Name) && r.Name.Trim() != r.Speaker)
            .ToDictionary(r => r.Speaker, r => r.Name.Trim());
        await Close(names);
    }

    private async void OnKeep(object? sender, EventArgs e) => await Close(new Dictionary<string, string>());

    protected override bool OnBackButtonPressed()
    {
        _ = Close(null);
        return true;
    }

    private async Task Close(IReadOnlyDictionary<string, string>? names)
    {
        if (_result.Task.IsCompleted) return;
        _result.TrySetResult(names);
        await Navigation.PopModalAsync();
    }
}

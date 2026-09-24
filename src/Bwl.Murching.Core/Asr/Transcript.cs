namespace Bwl.Murching.Asr;

/// <summary>One recognised word (or punctuation-bearing token group) with timing.</summary>
public sealed record TranscriptWord(string Text, TimeSpan Start, TimeSpan End, float Probability)
{
    public TimeSpan Duration => End - Start;

    public TranscriptWord Shift(TimeSpan by) => this with { Start = Start + by, End = End + by };

    public override string ToString() => $"{Text} [{Start:m\\:ss\\.fff}-{End:m\\:ss\\.fff}]";
}

/// <summary>A Whisper segment: roughly a sentence or clause.</summary>
public sealed record TranscriptSegment(
    string Text,
    TimeSpan Start,
    TimeSpan End,
    IReadOnlyList<TranscriptWord> Words,
    float Probability,
    float NoSpeechProbability)
{
    public TimeSpan Duration => End - Start;

    public TranscriptSegment Shift(TimeSpan by) => this with
    {
        Start = Start + by,
        End = End + by,
        Words = Words.Select(w => w.Shift(by)).ToList(),
    };

    public override string ToString() => $"[{Start:h\\:mm\\:ss\\.fff} -> {End:h\\:mm\\:ss\\.fff}] {Text}";
}

/// <summary>Full recognition result for one media file.</summary>
public sealed class Transcript
{
    public Transcript(string? language, IReadOnlyList<TranscriptSegment> segments)
    {
        Language = language;
        Segments = segments;
    }

    /// <summary>ISO 639-1 code (<c>en</c>, <c>ru</c>) or null when unknown.</summary>
    public string? Language { get; init; }

    public string? Model { get; init; }

    public TimeSpan? SourceDuration { get; init; }

    public IReadOnlyList<TranscriptSegment> Segments { get; }

    public IEnumerable<TranscriptWord> Words => Segments.SelectMany(s => s.Words);

    public string Text => string.Join(" ", Segments.Select(s => s.Text.Trim()).Where(t => t.Length > 0));

    public TimeSpan Start => Segments.Count == 0 ? TimeSpan.Zero : Segments[0].Start;

    public TimeSpan End => Segments.Count == 0 ? TimeSpan.Zero : Segments[^1].End;

    public static Transcript Merge(string? language, IEnumerable<Transcript> parts)
    {
        var list = parts.ToList();
        var segments = list.SelectMany(p => p.Segments).OrderBy(s => s.Start).ToList();
        return new Transcript(language ?? list.Select(p => p.Language).FirstOrDefault(l => l is not null), segments)
        {
            Model = list.Select(p => p.Model).FirstOrDefault(m => m is not null),
            SourceDuration = list.Select(p => p.SourceDuration).FirstOrDefault(d => d is not null),
        };
    }

    public Transcript WithSegments(IReadOnlyList<TranscriptSegment> segments) => new(Language, segments)
    {
        Model = Model,
        SourceDuration = SourceDuration,
    };
}

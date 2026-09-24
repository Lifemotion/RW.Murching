namespace Bwl.Murching.Subtitles;

/// <summary>One displayed subtitle: a time range and up to a few lines of text.</summary>
public sealed record SubtitleCue(int Index, TimeSpan Start, TimeSpan End, IReadOnlyList<string> Lines)
{
    public string Text => string.Join("\n", Lines);

    public TimeSpan Duration => End - Start;

    public int CharacterCount => Lines.Sum(l => l.Length);

    public double CharactersPerSecond => Duration > TimeSpan.Zero ? CharacterCount / Duration.TotalSeconds : 0;

    public override string ToString() => $"{Index}: [{Start:h\\:mm\\:ss\\.fff} -> {End:h\\:mm\\:ss\\.fff}] {string.Join(" | ", Lines)}";
}

public sealed class SubtitleDocument
{
    public SubtitleDocument(IReadOnlyList<SubtitleCue> cues)
    {
        Cues = cues;
    }

    public IReadOnlyList<SubtitleCue> Cues { get; }

    /// <summary>ISO 639-1 language of the text.</summary>
    public string? Language { get; init; }

    public string? Title { get; init; }

    public TimeSpan Duration => Cues.Count == 0 ? TimeSpan.Zero : Cues[^1].End;

    public SubtitleDocument Renumbered() => new(Cues.Select((c, i) => c with { Index = i + 1 }).ToList()) { Language = Language, Title = Title };
}

using Whisper.net;

namespace Bwl.Murching.Models;

public enum ModelKind
{
    Whisper,
    Vad,
}

/// <summary>A downloadable (or local) GGML model file.</summary>
public sealed record ModelSpec
{
    public required string Name { get; init; }

    public required ModelKind Kind { get; init; }

    public required string FileName { get; init; }

    public Uri? Url { get; init; }

    /// <summary>Set when the spec points at a user supplied file instead of a catalog entry.</summary>
    public string? LocalPath { get; init; }

    public long ApproxBytes { get; init; }

    public string Description { get; init; } = string.Empty;

    /// <summary>Alignment heads preset used for DTW token timestamps (Whisper models only).</summary>
    public WhisperAlignmentHeadsPreset HeadsPreset { get; init; } = WhisperAlignmentHeadsPreset.None;

    /// <summary>True for English-only Whisper checkpoints (<c>*.en</c>).</summary>
    public bool EnglishOnly { get; init; }

    public bool IsLocal => LocalPath is not null;

    public override string ToString() => Name;
}

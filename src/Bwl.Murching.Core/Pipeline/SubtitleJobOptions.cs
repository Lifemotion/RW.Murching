using Bwl.Murching.Asr;
using Bwl.Murching.Subtitles;
using Bwl.Murching.Vad;

namespace Bwl.Murching.Pipeline;

public sealed record SubtitleJobOptions
{
    public required string InputPath { get; init; }

    /// <summary>Explicit output path (single format only). Default: <c>&lt;input&gt;.&lt;lang&gt;.&lt;ext&gt;</c> next to the input.</summary>
    public string? OutputPath { get; init; }

    public IReadOnlyList<SubtitleFormat> Formats { get; init; } = [SubtitleFormat.Srt];

    public AsrOptions Asr { get; init; } = new();

    public bool UseVad { get; init; } = true;

    public VadOptions Vad { get; init; } = new();

    public ChunkingOptions Chunking { get; init; } = new();

    /// <summary>Send energetic non-speech regions (music, singing) to Whisper as tentative chunks with strict filtering.</summary>
    public FallbackOptions Fallback { get; init; } = new();

    public CueBuilderOptions Cues { get; init; } = new();

    public HallucinationFilterOptions Hallucinations { get; init; } = new();

    /// <summary>Zero-based audio stream index (among audio streams).</summary>
    public int? AudioStreamIndex { get; init; }

    public TimeSpan? Start { get; init; }

    public TimeSpan? End { get; init; }

    /// <summary>Write the full transcript (segments + words) as JSON next to the subtitles.</summary>
    public bool SaveTranscriptJson { get; init; }

    public string? TranscriptJsonPath { get; init; }

    /// <summary>Mux the subtitles into a copy of the video as a soft subtitle track.</summary>
    public bool EmbedIntoVideo { get; init; }

    public string? EmbedOutputPath { get; init; }

    /// <summary>Override the models directory (default <c>%LOCALAPPDATA%\Bwl.Murching\models</c>).</summary>
    public string? ModelsDir { get; init; }
}

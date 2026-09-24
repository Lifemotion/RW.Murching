using Bwl.Murching.Models;
using Bwl.Murching.Runtime;

namespace Bwl.Murching.Asr;

/// <summary>Whisper recognition settings.</summary>
public sealed record AsrOptions
{
    /// <summary>Catalog name (<c>large-v3-turbo</c>) or path to a ggml model file.</summary>
    public string Model { get; init; } = ModelCatalog.DefaultWhisperModel;

    /// <summary>ISO 639-1 code, or <c>auto</c> to detect once from the first speech chunks.</summary>
    public string Language { get; init; } = "auto";

    /// <summary>Use Whisper's built-in translation to English instead of transcription.</summary>
    public bool TranslateToEnglish { get; init; }

    public ComputeDevice Device { get; init; } = ComputeDevice.Auto;

    /// <summary>CPU threads for the decoder; null picks a sensible default per device.</summary>
    public int? Threads { get; init; }

    /// <summary>Beam search width; 1 selects greedy decoding.</summary>
    public int BeamSize { get; init; } = 5;

    /// <summary>Text prepended to the first decoding window (vocabulary hints, spelling, style).</summary>
    public string? InitialPrompt { get; init; }

    /// <summary>Feed the tail of the previous chunk as prompt to the next one (more consistent style, more risk of loops).</summary>
    public bool ContextAcrossChunks { get; init; }

    /// <summary>Request token-level timestamps (needed for word timing).</summary>
    public bool WordTimestamps { get; init; } = true;

    /// <summary>Use Dynamic Time Warping over cross-attention for token timing (better word alignment, disables flash attention).</summary>
    public bool DtwTimestamps { get; init; } = true;

    /// <summary>Flash attention on the GPU; ignored when DTW timestamps are enabled.</summary>
    public bool FlashAttention { get; init; } = true;

    /// <summary>Sampling temperature; null keeps whisper.cpp defaults (0 with fallback increments).</summary>
    public float? Temperature { get; init; }

    public float NoSpeechThreshold { get; init; } = 0.6f;

    /// <summary>Let whisper.cpp split segments at roughly this many characters (0 = off; the cue builder does its own splitting).</summary>
    public int MaxSegmentLength { get; init; }
}

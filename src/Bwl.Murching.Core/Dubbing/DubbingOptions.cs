namespace Bwl.Murching.Dubbing;

public sealed record DubbingOptions
{
    public required string InputPath { get; init; }

    /// <summary>Target language (ISO 639-1) of the new voice track.</summary>
    public required string TargetLanguage { get; init; }

    /// <summary>Translated subtitles (.srt/.vtt) to voice. When null the job transcribes and translates first.</summary>
    public string? SubtitlePath { get; init; }

    /// <summary>Output video; default <c>&lt;name&gt;.dub.&lt;lang&gt;.mp4</c> next to the input.</summary>
    public string? OutputPath { get; init; }

    /// <summary>TTS engine: <c>qwen</c> (Qwen3-TTS via ONNX Runtime, no Python), <c>xtts</c> (XTTS-v2) or <c>chatterbox</c> (Python sidecar).</summary>
    public string Engine { get; init; } = "qwen";

    /// <summary><c>cuda</c>, <c>dml</c> (DirectML, any Windows GPU; qwen only) or <c>cpu</c>.</summary>
    public string Device { get; init; } = "dml";

    /// <summary>Clone the original speaker (default) or use a preset voice (<see cref="Voice"/>).</summary>
    public bool CloneVoice { get; init; } = true;

    /// <summary>Preset voice name for engines/modes without cloning.</summary>
    public string? Voice { get; init; }

    /// <summary>Python interpreter of the TTS virtual environment; null = auto-detect (tools/tts-venv, %LOCALAPPDATA%).</summary>
    public string? PythonPath { get; init; }

    /// <summary>Consecutive cues closer than this are voiced as one utterance.</summary>
    public TimeSpan MergeGap { get; init; } = TimeSpan.FromMilliseconds(600);

    /// <summary>Complete sentences separated by a pause up to this long are voiced as one utterance.</summary>
    public TimeSpan SentenceJoinGap { get; init; } = TimeSpan.FromMilliseconds(200);

    /// <summary>Upper bound for one utterance; longer sentences are voiced in pieces.</summary>
    public TimeSpan MaxUnitDuration { get; init; } = TimeSpan.FromSeconds(14);

    /// <summary>An utterance may start at most this late because the previous one overran; beyond that it starts on time and overlaps.</summary>
    public TimeSpan MaxDelay { get; init; } = TimeSpan.FromMilliseconds(800);

    /// <summary>Speaker reference window around each utterance (the same voice, the same mood).</summary>
    public TimeSpan MinReference { get; init; } = TimeSpan.FromSeconds(6);

    public TimeSpan MaxReference { get; init; } = TimeSpan.FromSeconds(20);

    /// <summary>Synthesised speech may be sped up by at most this factor to fit its slot.</summary>
    public double MaxSpeedUp { get; init; } = 1.35;

    /// <summary>Above this required speed-up the utterance is re-synthesised with a faster speaking rate first.</summary>
    public double ResynthesizeAbove { get; init; } = 1.12;

    /// <summary>Gain applied to the original track while the dub speaks (0.2 ≈ −14 dB).</summary>
    public double Duck { get; init; } = 0.2;

    public TimeSpan DuckFade { get; init; } = TimeSpan.FromMilliseconds(250);

    /// <summary>Dub loudness relative to the original speech it replaces (RMS ratio).</summary>
    public double VoiceGain { get; init; } = 1.0;

    /// <summary>
    /// Also keep the original audio as a second track. Off by default: many players (browsers, Windows apps) ignore
    /// the "default" flag and play whichever track they like, so a shareable file should carry the dub alone.
    /// </summary>
    public bool KeepOriginalTrack { get; init; }

    /// <summary>Directory for per-utterance WAVs and the dubbing script; default a temp folder, removed unless <see cref="KeepWorkFiles"/>.</summary>
    public string? WorkDir { get; init; }

    public bool KeepWorkFiles { get; init; }

    public int SampleRate { get; init; } = 48_000;
}

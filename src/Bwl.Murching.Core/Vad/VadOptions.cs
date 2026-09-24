using Bwl.Murching.Models;

namespace Bwl.Murching.Vad;

/// <summary>Silero VAD parameters (mirrors whisper.cpp <c>whisper_vad_params</c>).</summary>
public sealed record VadOptions
{
    public string Model { get; init; } = ModelCatalog.DefaultVadModel;

    /// <summary>Speech probability threshold (0..1).</summary>
    public float Threshold { get; init; } = 0.5f;

    public TimeSpan MinSpeechDuration { get; init; } = TimeSpan.FromMilliseconds(250);

    public TimeSpan MinSilenceDuration { get; init; } = TimeSpan.FromMilliseconds(300);

    /// <summary>Long continuous speech is cut at the last short pause before this limit.</summary>
    public TimeSpan MaxSpeechDuration { get; init; } = TimeSpan.FromSeconds(28);

    public TimeSpan SpeechPadding { get; init; } = TimeSpan.FromMilliseconds(100);

    public TimeSpan SamplesOverlap { get; init; } = TimeSpan.FromMilliseconds(100);

    public int Threads { get; init; } = Math.Clamp(Environment.ProcessorCount / 2, 1, 8);
}

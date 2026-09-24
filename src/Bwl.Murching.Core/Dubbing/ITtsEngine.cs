namespace Bwl.Murching.Dubbing;

/// <summary>One synthesis request: what to say, in which language, whose voice.</summary>
public sealed record TtsRequest
{
    public required string Text { get; init; }

    /// <summary>ISO 639-1 code of <see cref="Text"/>.</summary>
    public required string Language { get; init; }

    /// <summary>WAV with the speaker to clone (engines without cloning ignore it).</summary>
    public string? ReferenceWav { get; init; }

    /// <summary>Transcript of the reference clip, when known (some cloning models want it).</summary>
    public string? ReferenceText { get; init; }

    /// <summary>Named preset voice for engines without cloning, or to override cloning.</summary>
    public string? Voice { get; init; }

    /// <summary>Speaking-rate hint, 1.0 = normal; engines that cannot vary rate ignore it.</summary>
    public double Speed { get; init; } = 1.0;

    public required string OutputWav { get; init; }
}

/// <summary>A text-to-speech backend. Implementations are used sequentially by the dubbing job.</summary>
public interface ITtsEngine : IAsyncDisposable
{
    /// <summary>Short id used in logs, file names and the CLI: <c>xtts</c>, <c>qwen</c>, ...</summary>
    string Name { get; }

    string Description { get; }

    /// <summary>Whether <see cref="TtsRequest.ReferenceWav"/> changes the voice.</summary>
    bool SupportsVoiceCloning { get; }

    /// <summary>Whether <see cref="TtsRequest.Speed"/> is honoured natively (otherwise the job time-stretches).</summary>
    bool SupportsSpeed { get; }

    Task<TtsResult> SynthesizeAsync(TtsRequest request, CancellationToken ct = default);
}

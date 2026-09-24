using Bwl.Murching.Audio;

namespace Bwl.Murching.Asr;

public sealed record LanguageDetection(string Language, float Probability);

public sealed record TranscribeRequest
{
    /// <summary>Added to every timestamp so results are expressed in source-file time.</summary>
    public TimeSpan Offset { get; init; }

    /// <summary>ISO 639-1 code; null or <c>auto</c> lets the model detect.</summary>
    public string? Language { get; init; }

    /// <summary>Initial prompt for this call (previous context, vocabulary hints).</summary>
    public string? Prompt { get; init; }

    /// <summary>Whisper progress 0..100 for this call.</summary>
    public IProgress<int>? Progress { get; init; }
}

public interface ISpeechRecognizer : IDisposable
{
    /// <summary>Human readable description of the backend that was loaded (CUDA / CPU, model).</summary>
    string RuntimeDescription { get; }

    Task<LanguageDetection> DetectLanguageAsync(PcmAudio audio, CancellationToken ct = default);

    Task<Transcript> TranscribeAsync(PcmAudio audio, TranscribeRequest request, CancellationToken ct = default);
}

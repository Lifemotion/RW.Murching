namespace Bwl.Murching.Pipeline;

public enum JobStage
{
    Setup,
    Probe,
    ExtractAudio,
    DownloadModel,
    DetectSpeech,
    LoadModel,
    DetectLanguage,
    Transcribe,
    BuildCues,
    Write,
    Embed,
    Done,
}

/// <summary>Progress within one pipeline stage. <see cref="Fraction"/> is 0..1 for the current stage.</summary>
public sealed record JobProgress(JobStage Stage, double Fraction, string? Message = null);

/// <summary>An <see cref="IProgress{T}"/> that invokes the callback synchronously on the reporting thread (no SynchronizationContext games).</summary>
public sealed class SyncProgress<T>(Action<T> handler) : IProgress<T>
{
    public void Report(T value) => handler(value);
}

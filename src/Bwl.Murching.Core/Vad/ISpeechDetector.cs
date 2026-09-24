using Bwl.Murching.Audio;

namespace Bwl.Murching.Vad;

public interface ISpeechDetector : IDisposable
{
    /// <summary>Returns speech regions in chronological order, relative to the start of <paramref name="audio"/>.</summary>
    Task<IReadOnlyList<SpeechSegment>> DetectAsync(PcmAudio audio, CancellationToken ct = default);
}

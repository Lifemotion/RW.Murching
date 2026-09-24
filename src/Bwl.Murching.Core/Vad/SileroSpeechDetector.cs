using Bwl.Murching.Audio;
using Bwl.Murching.Runtime;
using Microsoft.Extensions.Logging;
using Whisper.net;

namespace Bwl.Murching.Vad;

/// <summary>Silero VAD running through whisper.cpp (ggml). CPU only: the model is tiny and the GPU is better left to Whisper.</summary>
public sealed class SileroSpeechDetector : ISpeechDetector
{
    private readonly WhisperVadFactory _factory;
    private readonly WhisperVadProcessor _processor;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public SileroSpeechDetector(string modelPath, VadOptions? options = null, ILogger? logger = null)
    {
        options ??= new VadOptions();
        if (!File.Exists(modelPath))
        {
            throw new FileNotFoundException("VAD model not found.", modelPath);
        }

        // Creating the VAD is what loads the native whisper library for the whole process: make sure CUDA is discoverable first.
        WhisperRuntime.EnsureConfigured(logger);

        _factory = WhisperVadFactory.FromPath(modelPath, new WhisperFactoryOptions { UseGpu = false });
        _processor = _factory.CreateBuilder()
            .WithThreads(options.Threads)
            .WithUseGpu(false)
            .WithThreshold(options.Threshold)
            .WithMinSpeechDuration(options.MinSpeechDuration)
            .WithMinSilenceDuration(options.MinSilenceDuration)
            .WithMaxSpeechDuration(options.MaxSpeechDuration)
            .WithSpeechPadding(options.SpeechPadding)
            .WithSamplesOverlap(options.SamplesOverlap)
            .Build();
    }

    public async Task<IReadOnlyList<SpeechSegment>> DetectAsync(PcmAudio audio, CancellationToken ct = default)
    {
        if (audio.SampleRate != PcmAudio.WhisperSampleRate)
        {
            throw new ArgumentException($"VAD expects {PcmAudio.WhisperSampleRate} Hz audio, got {audio.SampleRate} Hz.", nameof(audio));
        }

        if (audio.Length == 0)
        {
            return [];
        }

        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            return await Task.Run(() =>
            {
                var duration = audio.Duration;
                var result = new List<SpeechSegment>();
                foreach (var seg in _processor.DetectSpeech(audio.Samples.Span))
                {
                    var start = seg.Start < TimeSpan.Zero ? TimeSpan.Zero : seg.Start;
                    var end = seg.End > duration ? duration : seg.End;
                    if (end > start)
                    {
                        result.Add(new SpeechSegment(start, end));
                    }
                }

                result.Sort((a, b) => a.Start.CompareTo(b.Start));
                return (IReadOnlyList<SpeechSegment>)result;
            }, ct).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public void Dispose()
    {
        _processor.Dispose();
        _factory.Dispose();
        _gate.Dispose();
    }
}

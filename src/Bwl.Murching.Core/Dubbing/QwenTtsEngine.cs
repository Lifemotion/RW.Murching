using System.Diagnostics;
using Bwl.Murching.Audio;
using Bwl.Murching.Runtime;
using ElBruno.QwenTTS.Pipeline;
using ElBruno.QwenTTS.VoiceCloning.Pipeline;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.ML.OnnxRuntime;

namespace Bwl.Murching.Dubbing;

/// <summary>
/// Qwen3-TTS (0.6B, 12 Hz codec) through ONNX Runtime — no Python. Two modes: zero-shot voice cloning from the
/// reference clip (Base model) and preset speakers (CustomVoice model). Models (~5.5 GB each) download from
/// Hugging Face on first use into <c>%LOCALAPPDATA%\ElBruno\QwenTTS</c>.
/// </summary>
public sealed class QwenTtsEngine : ITtsEngine
{
    private static readonly Dictionary<string, string> Languages = new(StringComparer.OrdinalIgnoreCase)
    {
        ["en"] = "english", ["ru"] = "russian", ["zh"] = "chinese", ["ja"] = "japanese", ["ko"] = "korean", ["es"] = "spanish",
        ["de"] = "german", ["fr"] = "french", ["it"] = "italian", ["pt"] = "portuguese",
    };

    private readonly VoiceClonePipeline? _cloner;
    private readonly ITtsPipeline? _presets;
    private readonly ILogger _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Dictionary<string, float[]> _embeddings = new(StringComparer.OrdinalIgnoreCase);

    private QwenTtsEngine(VoiceClonePipeline? cloner, ITtsPipeline? presets, string provider, ILogger logger)
    {
        _cloner = cloner;
        _presets = presets;
        _logger = logger;
        Provider = provider;
    }

    public string Name => "qwen";

    public string Provider { get; }

    public string Description => $"Qwen3-TTS 0.6B ({(_cloner is not null ? "voice cloning" : "preset voices")}) via ONNX Runtime {Provider} @ 24000 Hz";

    public bool SupportsVoiceCloning => _cloner is not null;

    public bool SupportsSpeed => false;

    public IReadOnlyCollection<string> PresetVoices => _presets?.Speakers ?? [];

    /// <summary>
    /// Loads the cloning model, or the preset-voice model when <paramref name="cloning"/> is false.
    /// <paramref name="device"/>: <c>cuda</c> (needs the CUDA 12 / cuDNN 9 libraries, see <c>murch setup --cuda</c>), <c>auto</c> or <c>cpu</c>.
    /// </summary>
    public static async Task<QwenTtsEngine> CreateAsync(bool cloning, string device, ILogger? logger = null, IProgress<string>? progress = null, CancellationToken ct = default)
    {
        logger ??= NullLogger.Instance;
        var wantCuda = device.Equals("cuda", StringComparison.OrdinalIgnoreCase) || device.Equals("auto", StringComparison.OrdinalIgnoreCase) || device.Equals("dml", StringComparison.OrdinalIgnoreCase);
        var provider = ExecutionProvider.Cpu;
        if (wantCuda)
        {
            if (CudaRuntime.HasNvidiaDriver() && CudaRuntime.PrepareForOnnxRuntime(logger))
            {
                provider = ExecutionProvider.Cuda;
            }
            else if (device.Equals("cuda", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("CUDA requested for Qwen3-TTS but the CUDA 12 / cuDNN 9 libraries were not found. Run 'murch setup --cuda' (downloads ~2.7 GB) or use --device cpu.");
            }
            else
            {
                logger.LogWarning("CUDA 12 / cuDNN 9 libraries not found; Qwen3-TTS runs on the CPU (slow). Run 'murch setup --cuda'.");
            }
        }

        Func<SessionOptions> factory = provider switch
        {
            ExecutionProvider.Cuda => () => OrtSessionHelper.CreateCudaOptions(),
            ExecutionProvider.DirectML => () => OrtSessionHelper.CreateDirectMlOptions(),
            _ => () => OrtSessionHelper.CreateCpuOptions(),
        };

        var clock = Stopwatch.StartNew();
        logger.LogInformation("Loading Qwen3-TTS ({Mode}) on {Provider}; first run downloads ~5.5 GB", cloning ? "voice cloning" : "preset voices", provider);
        try
        {
            if (cloning)
            {
                var relay = new Progress<ModelDownloadProgress>(p => progress?.Report($"{p.FileName} {p.BytePercentage:0}%"));
                var cloner = await VoiceClonePipeline.CreateAsync(modelDir: null, downloadProgress: relay, sessionOptionsFactory: factory, cancellationToken: ct).ConfigureAwait(false);
                logger.LogInformation("Qwen3-TTS cloning model ready in {Seconds:0.0}s", clock.Elapsed.TotalSeconds);
                return new QwenTtsEngine(cloner, null, provider.ToString(), logger);
            }

            var pipeline = await TtsPipeline.CreateAsync(sessionOptionsFactory: factory, cancellationToken: ct).ConfigureAwait(false);
            logger.LogInformation("Qwen3-TTS preset model ready in {Seconds:0.0}s; voices: {Voices}", clock.Elapsed.TotalSeconds, string.Join(", ", pipeline.Speakers));
            return new QwenTtsEngine(null, pipeline, provider.ToString(), logger);
        }
        catch (Exception ex) when (provider != ExecutionProvider.Cpu && ex is OnnxRuntimeException or DllNotFoundException or EntryPointNotFoundException)
        {
            throw new InvalidOperationException($"ONNX Runtime could not use the {provider} execution provider: {ex.Message}. Check 'murch doctor' or try --device cpu.", ex);
        }
    }

    public async Task<TtsResult> SynthesizeAsync(TtsRequest request, CancellationToken ct = default)
    {
        var language = Languages.TryGetValue(request.Language, out var name) ? name : "auto";
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var clock = Stopwatch.StartNew();
            if (_cloner is not null)
            {
                if (request.ReferenceWav is null)
                {
                    throw new ArgumentException("Voice cloning needs a reference clip.", nameof(request));
                }

                // Speaker embeddings are cached per reference file: the planner reuses references across utterances.
                if (!_embeddings.TryGetValue(request.ReferenceWav, out var embedding))
                {
                    embedding = _cloner.ExtractSpeakerEmbedding(request.ReferenceWav);
                    if (_embeddings.Count > 256)
                    {
                        _embeddings.Clear();
                    }

                    _embeddings[request.ReferenceWav] = embedding;
                }

                await _cloner.SynthesizeWithEmbeddingAsync(request.Text, embedding, request.OutputWav, refText: request.ReferenceText, refAudioCodes: null, language: language, progress: null, cancellationToken: ct).ConfigureAwait(false);
            }
            else
            {
                var speaker = request.Voice ?? _presets!.Speakers.FirstOrDefault() ?? "ryan";
                await _presets!.SynthesizeAsync(request.Text, speaker, request.OutputWav, language: language).ConfigureAwait(false);
            }

            var audio = WavFile.Read(request.OutputWav);
            return new TtsResult(true, audio.Duration, audio.SampleRate, null, clock.Elapsed);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogDebug(ex, "Qwen3-TTS synthesis failed");
            return new TtsResult(false, TimeSpan.Zero, 24000, ex.Message, TimeSpan.Zero);
        }
        finally
        {
            _gate.Release();
        }
    }

    public ValueTask DisposeAsync()
    {
        _cloner?.Dispose();
        (_presets as IDisposable)?.Dispose();
        _gate.Dispose();
        return ValueTask.CompletedTask;
    }
}

using Bwl.Murching.Audio;
using Bwl.Murching.Models;
using Bwl.Murching.Runtime;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Whisper.net;
using Whisper.net.LibraryLoader;

namespace Bwl.Murching.Asr;

/// <summary>Whisper.net (whisper.cpp) backed recogniser. One instance holds one loaded model; calls are serialised.</summary>
public sealed class WhisperRecognizer : ISpeechRecognizer
{
    private readonly WhisperFactory _factory;
    private readonly AsrOptions _options;
    private readonly ILogger _logger;
    private readonly int _threads;
    private readonly SemaphoreSlim _gate = new(1, 1);

    private WhisperProcessor? _shared;
    private string? _sharedLanguage;
    private CancellationToken _currentCt;
    private IProgress<int>? _currentProgress;

    private WhisperRecognizer(string modelPath, ModelSpec spec, AsrOptions options, ILogger logger)
    {
        _options = options;
        _logger = logger;
        ModelPath = modelPath;
        Model = spec;

        WhisperRuntime.Configure(options.Device, logger);

        var wantGpu = options.Device != ComputeDevice.Cpu;
        UsesDtw = options.WordTimestamps && options.DtwTimestamps && spec.HeadsPreset != WhisperAlignmentHeadsPreset.None;
        var factoryOptions = new WhisperFactoryOptions
        {
            UseGpu = wantGpu,
            GpuDevice = 0,
            UseFlashAttention = wantGpu && options.FlashAttention && !UsesDtw,
            UseDtwTimeStamps = UsesDtw,
            HeadsPreset = UsesDtw ? spec.HeadsPreset : WhisperAlignmentHeadsPreset.None,
            DtwMemSize = 128 * 1024 * 1024,
            DtwNTop = -1,
            DelayInitialization = false,
        };

        try
        {
            _factory = WhisperFactory.FromPath(modelPath, factoryOptions);
        }
        catch (Exception ex) when (options.Device == ComputeDevice.Cuda)
        {
            throw new InvalidOperationException(
                "CUDA was requested but the CUDA whisper runtime could not be loaded. " +
                "Check the NVIDIA driver and run 'murch setup --cuda' to download the CUDA 13 libraries.", ex);
        }

        LoadedRuntime = RuntimeOptions.LoadedLibrary;
        UsesGpu = wantGpu && WhisperRuntime.LoadedGpuRuntime;
        _threads = options.Threads ?? (UsesGpu ? Math.Clamp(Environment.ProcessorCount / 4, 2, 8) : Math.Max(1, Environment.ProcessorCount / 2));
        if (wantGpu && !UsesGpu)
        {
            logger.LogWarning("GPU inference requested but the {Runtime} runtime was loaded; falling back to CPU. Run 'murch doctor' for details.", LoadedRuntime);
        }

        logger.LogInformation("Whisper model {Model} loaded via {Runtime} (gpu={Gpu}, dtw={Dtw}, flash={Flash}, threads={Threads})",
            spec.Name, LoadedRuntime, UsesGpu, UsesDtw, factoryOptions.UseFlashAttention, _threads);
    }

    public string ModelPath { get; }

    public ModelSpec Model { get; }

    public RuntimeLibrary? LoadedRuntime { get; }

    public bool UsesGpu { get; }

    public bool UsesDtw { get; }

    public string RuntimeDescription => $"{Model.Name} on {LoadedRuntime?.ToString() ?? "unknown"}{(UsesGpu ? " (GPU)" : " (CPU)")}";

    /// <summary>Loads the model on a worker thread (native load can take seconds for large models).</summary>
    public static Task<WhisperRecognizer> CreateAsync(string modelPath, ModelSpec spec, AsrOptions options, ILogger? logger = null, CancellationToken ct = default) =>
        Task.Run(() => new WhisperRecognizer(modelPath, spec, options, logger ?? NullLogger.Instance), ct);

    public async Task<LanguageDetection> DetectLanguageAsync(PcmAudio audio, CancellationToken ct = default)
    {
        var window = audio.Slice(TimeSpan.Zero, TimeSpan.FromSeconds(30)).ToArray();
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var processor = GetSharedProcessor("auto");
            _currentCt = ct;
            _currentProgress = null;
            var (language, probability) = await Task.Run(() => processor.DetectLanguageWithProbability(window), ct).ConfigureAwait(false);
            return new LanguageDetection(language ?? "en", probability);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<Transcript> TranscribeAsync(PcmAudio audio, TranscribeRequest request, CancellationToken ct = default)
    {
        if (audio.SampleRate != PcmAudio.WhisperSampleRate)
        {
            throw new ArgumentException($"Whisper expects {PcmAudio.WhisperSampleRate} Hz audio, got {audio.SampleRate} Hz.", nameof(audio));
        }

        var language = string.IsNullOrWhiteSpace(request.Language) ? "auto" : request.Language.Trim().ToLowerInvariant();
        var prompt = CombinePrompt(_options.InitialPrompt, request.Prompt);

        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            _currentCt = ct;
            _currentProgress = request.Progress;
            WhisperProcessor processor;
            var own = false;
            if (prompt is null)
            {
                processor = GetSharedProcessor(language);
            }
            else
            {
                processor = BuildProcessor(language, prompt);
                own = true;
            }

            try
            {
                var segments = new List<TranscriptSegment>();
                var clipDuration = audio.Duration;
                await foreach (var segment in processor.ProcessAsync(audio.Samples, ct).ConfigureAwait(false))
                {
                    var converted = WordAssembler.Convert(segment, request.Offset, clipDuration, UsesDtw);
                    if (converted.Text.Length > 0)
                    {
                        segments.Add(converted);
                    }
                }

                return new Transcript(language == "auto" ? null : language, segments) { Model = Model.Name, SourceDuration = clipDuration };
            }
            finally
            {
                if (own)
                {
                    await processor.DisposeAsync().ConfigureAwait(false);
                }
            }
        }
        finally
        {
            _currentProgress = null;
            _gate.Release();
        }
    }

    public void Dispose()
    {
        _shared?.Dispose();
        _factory.Dispose();
        _gate.Dispose();
    }

    private static string? CombinePrompt(string? initial, string? context)
    {
        if (string.IsNullOrWhiteSpace(initial))
        {
            return string.IsNullOrWhiteSpace(context) ? null : context.Trim();
        }

        return string.IsNullOrWhiteSpace(context) ? initial.Trim() : initial.Trim() + " " + context.Trim();
    }

    private WhisperProcessor GetSharedProcessor(string language)
    {
        if (_shared is null)
        {
            _shared = BuildProcessor(language, prompt: null);
            _sharedLanguage = language;
        }
        else if (!string.Equals(_sharedLanguage, language, StringComparison.OrdinalIgnoreCase))
        {
            _shared.ChangeLanguage(language);
            _sharedLanguage = language;
        }

        return _shared;
    }

    private WhisperProcessor BuildProcessor(string language, string? prompt)
    {
        var builder = _factory.CreateBuilder()
            .WithThreads(_threads)
            .WithProbabilities()
            .WithNoSpeechThreshold(_options.NoSpeechThreshold)
            .WithEncoderBeginHandler(_ => !_currentCt.IsCancellationRequested)
            .WithProgressHandler(p => _currentProgress?.Report(p));

        builder = language == "auto" ? builder.WithLanguageDetection() : builder.WithLanguage(language);

        if (_options.TranslateToEnglish)
        {
            builder.WithTranslate();
        }

        if (_options.WordTimestamps)
        {
            builder.WithTokenTimestamps();
        }

        if (_options.BeamSize > 1)
        {
            builder.WithBeamSearchSamplingStrategy(b => b.WithBeamSize(_options.BeamSize));
        }
        else
        {
            builder.WithGreedySamplingStrategy(g => g.WithBestOf(1));
        }

        if (_options.Temperature is { } temperature)
        {
            builder.WithTemperature(temperature);
        }

        if (_options.MaxSegmentLength > 0)
        {
            builder.WithMaxSegmentLength(_options.MaxSegmentLength).SplitOnWord();
        }

        if (prompt is not null)
        {
            builder.WithPrompt(prompt);
        }

        return builder.Build();
    }
}

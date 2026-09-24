using System.Diagnostics;
using System.Text.Encodings.Web;
using System.Text.Json;
using Bwl.Murching.Audio;
using Bwl.Murching.Common;
using Bwl.Murching.Media;
using Bwl.Murching.Pipeline;
using Bwl.Murching.Runtime;
using Bwl.Murching.Subtitles;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Bwl.Murching.Dubbing;

public enum DubStage
{
    Prepare,
    Subtitles,
    DecodeAudio,
    StartTts,
    Synthesize,
    Mix,
    Mux,
    Done,
}

public sealed record DubProgress(DubStage Stage, double Fraction, string? Message = null);

public sealed record DubUnitReport(int Index, TimeSpan Start, TimeSpan End, string Text, TimeSpan Synthesized, double Tempo, TimeSpan Placed, bool Overrun, double Speed, string Clip);

public sealed record DubResult(
    string OutputPath,
    string ScriptPath,
    IReadOnlyList<DubUnitReport> Units,
    TimeSpan Elapsed,
    string Engine,
    int Overruns,
    TimeSpan TotalDrift);

/// <summary>
/// Re-voices a video: translated cues → utterances → voice-cloned TTS (reference = the original speaker at that
/// moment) → tempo fit → placement on a timeline → ducked mix with the original → mux.
/// </summary>
public sealed class DubJob(DubbingOptions options, ILogger? logger = null)
{
    private readonly ILogger _logger = logger ?? NullLogger.Instance;

    public async Task<DubResult> RunAsync(IProgress<DubProgress>? progress = null, CancellationToken ct = default)
    {
        var total = Stopwatch.StartNew();
        void Report(DubStage stage, double fraction, string? message = null) => progress?.Report(new DubProgress(stage, fraction, message));

        var input = Path.GetFullPath(options.InputPath);
        if (!File.Exists(input))
        {
            throw new FileNotFoundException("Input video not found.", input);
        }

        Report(DubStage.Prepare, 0);
        var ffmpeg = await FfmpegTools.EnsureAsync(ct: ct).ConfigureAwait(false);
        var media = await new MediaProbe(ffmpeg).ProbeAsync(input, ct).ConfigureAwait(false);
        if (!media.HasAudio)
        {
            throw new InvalidOperationException("The input has no audio track.");
        }

        var python = TtsSidecar.FindPython(options.PythonPath)
                     ?? throw new InvalidOperationException("TTS environment not found. Create it with scripts/setup-tts.ps1 (Python 3.10–3.12 venv with torch + coqui-tts) or point MURCH_TTS_PYTHON at its python.exe.");
        var worker = TtsSidecar.FindWorkerScript()
                     ?? throw new InvalidOperationException("scripts/tts_worker.py not found next to the application or in the repository.");

        var workDir = options.WorkDir ?? Path.Combine(AppPaths.EnsureDirectory(AppPaths.TempDir), "dub-" + Path.GetFileNameWithoutExtension(input) + "-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(workDir);
        _logger.LogInformation("Work directory: {Dir}", workDir);

        // 1. Translated subtitles ---------------------------------------------------------------------------------
        Report(DubStage.Subtitles, 0);
        SubtitleDocument translated;
        if (options.SubtitlePath is { } subtitlePath)
        {
            translated = SrtParser.ParseFile(subtitlePath);
            _logger.LogInformation("Using {Count} cues from {Path}", translated.Cues.Count, subtitlePath);
        }
        else
        {
            var job = new SubtitleJob(new SubtitleJobOptions
            {
                InputPath = input,
                OutputPath = workDir + Path.DirectorySeparatorChar,
                Formats = [SubtitleFormat.Srt],
                TranslateTo = options.TargetLanguage,
                SaveTranscriptJson = true,
            }, _logger);
            var result = await job.RunAsync(new SyncProgress<JobProgress>(p => Report(DubStage.Subtitles, p.Fraction, $"{p.Stage} {p.Message}")), ct).ConfigureAwait(false);
            var srt = result.TranslatedOutputPaths.FirstOrDefault(p => p.EndsWith(".srt", StringComparison.OrdinalIgnoreCase))
                      ?? throw new InvalidOperationException("Transcription produced no translated subtitles (is the spoken language already the target?).");
            translated = SrtParser.ParseFile(srt);
        }

        if (translated.Cues.Count == 0)
        {
            throw new InvalidOperationException("Nothing to voice: the subtitle document is empty.");
        }

        var units = DubPlanner.Plan(translated, media.Duration, options);
        _logger.LogInformation("{Cues} cues → {Units} utterances", translated.Cues.Count, units.Count);
        Report(DubStage.Subtitles, 1, $"{units.Count} utterances");

        // 2. Original audio -----------------------------------------------------------------------------------------
        Report(DubStage.DecodeAudio, 0);
        var mixer = new AudioMixer(ffmpeg);
        var original = await mixer.DecodeAsync(input, options.SampleRate, 2, ct).ConfigureAwait(false);
        _logger.LogInformation("Decoded original: {Duration} stereo @ {Rate} Hz", TimeFormat.Human(original.Duration), original.SampleRate);
        Report(DubStage.DecodeAudio, 1);

        // 3. TTS worker ---------------------------------------------------------------------------------------------
        Report(DubStage.StartTts, 0, options.Engine);
        await using var tts = await TtsSidecar.StartAsync(python, worker, options.Engine, options.Device, _logger, ct).ConfigureAwait(false);
        Report(DubStage.StartTts, 1, $"{tts.Engine} on {tts.Device}");

        // 4. Synthesise, fit, place ---------------------------------------------------------------------------------
        var voice = new float[original.Frames];
        var voiced = new List<(TimeSpan Start, TimeSpan End)>(units.Count);
        var reports = new List<DubUnitReport>(units.Count);
        var cursor = TimeSpan.Zero; // end of the previously placed clip
        var drift = TimeSpan.Zero;
        var overruns = 0;
        var minGap = TimeSpan.FromMilliseconds(60);

        for (var i = 0; i < units.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            var unit = units[i];
            Report(DubStage.Synthesize, i / (double)units.Count, Truncate(unit.Text, 60));

            var refPath = Path.Combine(workDir, $"ref-{unit.Index:0000}.wav");
            WavFile.Write(refPath, original.Mono(unit.ReferenceStart, unit.ReferenceEnd));

            var clipPath = Path.Combine(workDir, $"tts-{unit.Index:0000}.wav");
            var speed = 1.0;
            var result = await tts.SynthesizeAsync(unit.Text, options.TargetLanguage, refPath, clipPath, speed, ct).ConfigureAwait(false);
            if (!result.Ok)
            {
                _logger.LogWarning("Utterance {Index} failed: {Error}", unit.Index, result.Error);
                continue;
            }

            var fit = DurationFitter.Compute(result.Duration, unit, options);
            if (fit.Tempo > options.ResynthesizeAbove)
            {
                // Ask the model to speak faster first: natural rate change beats time-stretching.
                speed = Math.Min(1.3, fit.Tempo);
                var faster = await tts.SynthesizeAsync(unit.Text, options.TargetLanguage, refPath, clipPath, speed, ct).ConfigureAwait(false);
                if (faster.Ok)
                {
                    result = faster;
                    fit = DurationFitter.Compute(result.Duration, unit, options);
                }
            }

            var clip = await mixer.LoadMonoAsync(clipPath, options.SampleRate, fit.Tempo, ct).ConfigureAwait(false);

            // Loudness: match the RMS of the original speech in this slot.
            var originalRms = original.Rms(unit.Start, unit.End);
            var clipRms = clip.Rms();
            var gain = clipRms > 1e-4 && originalRms > 1e-4 ? Math.Clamp(originalRms / clipRms * options.VoiceGain, 0.3, 3.0) : options.VoiceGain;

            var start = unit.Start;
            if (start < cursor + minGap)
            {
                drift += cursor + minGap - start;
                start = cursor + minGap;
            }

            var startFrame = original.FrameAt(start);
            var samples = clip.Samples.Span;
            var frames = Math.Min(samples.Length, voice.Length - startFrame);
            for (var f = 0; f < frames; f++)
            {
                voice[startFrame + f] += samples[f] * (float)gain;
            }

            var end = start + clip.Duration;
            cursor = end;
            voiced.Add((start, end));
            if (fit.Overruns)
            {
                overruns++;
            }

            reports.Add(new DubUnitReport(unit.Index, start, end, unit.Text, result.Duration, fit.Tempo, clip.Duration, fit.Overruns, speed, Path.GetFileName(clipPath)));
            _logger.LogDebug("#{Index} {Start} slot {Slot} tts {Tts} tempo {Tempo:0.00} → {Placed} gain {Gain:0.00}{Overrun}",
                unit.Index, TimeFormat.Human(unit.Start), TimeFormat.Human(unit.Duration), TimeFormat.Human(result.Duration), fit.Tempo, TimeFormat.Human(clip.Duration), gain, fit.Overruns ? " OVERRUN" : string.Empty);
        }

        Report(DubStage.Synthesize, 1);

        // 5. Mix ----------------------------------------------------------------------------------------------------
        Report(DubStage.Mix, 0);
        var mixed = AudioMixer.Mix(original, voice, voiced, options.Duck, options.DuckFade);
        var mixedPath = Path.Combine(workDir, "mixed.wav");
        AudioMixer.WriteWav(mixedPath, mixed);
        Report(DubStage.Mix, 1);

        // 6. Mux ----------------------------------------------------------------------------------------------------
        Report(DubStage.Mux, 0);
        var output = options.OutputPath ?? Path.Combine(Path.GetDirectoryName(input) ?? string.Empty, $"{Path.GetFileNameWithoutExtension(input)}.dub.{options.TargetLanguage}{DefaultContainer(input)}");
        await mixer.MuxAsync(input, mixedPath, output, options.TargetLanguage, options.KeepOriginalTrack, ct).ConfigureAwait(false);
        Report(DubStage.Mux, 1);

        var scriptPath = Path.ChangeExtension(output, ".dub.json");
        await File.WriteAllTextAsync(scriptPath, JsonSerializer.Serialize(new
        {
            input,
            output,
            language = options.TargetLanguage,
            engine = tts.Engine,
            units = reports.Select(r => new { r.Index, start = r.Start.TotalSeconds, end = r.End.TotalSeconds, r.Text, tts = r.Synthesized.TotalSeconds, r.Tempo, r.Speed, r.Overrun }),
        }, new JsonSerializerOptions { WriteIndented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping }), ct).ConfigureAwait(false);

        if (!options.KeepWorkFiles && options.WorkDir is null)
        {
            try
            {
                Directory.Delete(workDir, recursive: true);
            }
            catch (IOException ex)
            {
                _logger.LogDebug(ex, "Could not delete work dir");
            }
        }

        Report(DubStage.Done, 1);
        _logger.LogInformation("Dubbed video: {Output} ({Units} utterances, {Overruns} overruns, drift {Drift})", output, reports.Count, overruns, TimeFormat.Human(drift));
        return new DubResult(Path.GetFullPath(output), scriptPath, reports, total.Elapsed, tts.Engine, overruns, drift);
    }

    private static string DefaultContainer(string input)
    {
        var ext = Path.GetExtension(input).ToLowerInvariant();
        return ext is ".mp4" or ".mov" or ".m4v" or ".mkv" or ".webm" ? (ext == ".webm" ? ".mkv" : ext) : ".mp4";
    }

    private static string Truncate(string text, int max) => text.Length <= max ? text : text[..(max - 1)] + "…";
}

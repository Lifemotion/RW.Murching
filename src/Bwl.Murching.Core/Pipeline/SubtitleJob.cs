using System.Diagnostics;
using Bwl.Murching.Asr;
using Bwl.Murching.Audio;
using Bwl.Murching.Common;
using Bwl.Murching.Media;
using Bwl.Murching.Models;
using Bwl.Murching.Runtime;
using Bwl.Murching.Subtitles;
using Bwl.Murching.Translation;
using Bwl.Murching.Vad;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Bwl.Murching.Pipeline;

public sealed record SubtitleJobResult(
    IReadOnlyList<string> OutputPaths,
    IReadOnlyList<string> TranslatedOutputPaths,
    string? TranslatorName,
    string? TranscriptJsonPath,
    string? EmbeddedVideoPath,
    Transcript Transcript,
    SubtitleDocument Document,
    MediaInfo Media,
    string Language,
    float? LanguageProbability,
    string RuntimeDescription,
    int ChunkCount,
    int TentativeChunkCount,
    TimeSpan SpeechDuration,
    TimeSpan TentativeDuration,
    TimeSpan Elapsed,
    IReadOnlyDictionary<JobStage, TimeSpan> StageTimings)
{
    /// <summary>Speed relative to real time: audio seconds per wall-clock second for the whole job.</summary>
    public double RealTimeFactor => Elapsed > TimeSpan.Zero ? Media.Duration.TotalSeconds / Elapsed.TotalSeconds : 0;
}

/// <summary>The full subtitle pipeline: probe → decode → VAD → Whisper → cues → files.</summary>
public sealed class SubtitleJob(SubtitleJobOptions options, ILogger? logger = null)
{
    private static readonly TimeSpan MinWhisperClip = TimeSpan.FromSeconds(1.5);

    private readonly ILogger _logger = logger ?? NullLogger.Instance;

    public async Task<SubtitleJobResult> RunAsync(IProgress<JobProgress>? progress = null, CancellationToken ct = default)
    {
        var total = Stopwatch.StartNew();
        var timings = new Dictionary<JobStage, TimeSpan>();
        var stageClock = Stopwatch.StartNew();
        JobStage current = JobStage.Setup;

        void Enter(JobStage stage, string? message = null)
        {
            timings[current] = timings.GetValueOrDefault(current) + stageClock.Elapsed;
            stageClock.Restart();
            current = stage;
            progress?.Report(new JobProgress(stage, 0, message));
        }

        void Report(double fraction, string? message = null) => progress?.Report(new JobProgress(current, fraction, message));

        var input = Path.GetFullPath(options.InputPath);
        if (!File.Exists(input))
        {
            throw new FileNotFoundException("Input file not found.", input);
        }

        // 0. Runtime: must happen before any Whisper.net object (VAD included) touches the native library.
        WhisperRuntime.Configure(options.Asr.Device, _logger);

        // 1. Tools --------------------------------------------------------------------------------------------------
        Enter(JobStage.Setup, "ffmpeg");
        var ffmpeg = await FfmpegTools.EnsureAsync(new SyncProgress<DownloadProgress>(p => Report(p.Fraction ?? 0, $"downloading ffmpeg {Describe(p)}")), ct).ConfigureAwait(false);
        _logger.LogDebug("ffmpeg: {Path}", ffmpeg.FfmpegPath);

        // 2. Probe --------------------------------------------------------------------------------------------------
        Enter(JobStage.Probe);
        var media = await new MediaProbe(ffmpeg).ProbeAsync(input, ct).ConfigureAwait(false);
        if (!media.HasAudio)
        {
            throw new InvalidOperationException($"No audio stream in {media.Path}.");
        }

        _logger.LogInformation("Input: {Format} {Duration}, streams: {Streams}", media.Format, TimeFormat.Human(media.Duration), string.Join("; ", media.Streams));
        Report(1);

        // 3. Decode audio -------------------------------------------------------------------------------------------
        Enter(JobStage.ExtractAudio);
        var audio = await new AudioExtractor(ffmpeg).ExtractAsync(input, new AudioExtractOptions
        {
            AudioStreamIndex = options.AudioStreamIndex,
            Start = options.Start,
            End = options.End,
        }, ct).ConfigureAwait(false);
        var timeOffset = options.Start ?? TimeSpan.Zero;
        _logger.LogInformation("Decoded {Duration} of audio ({Samples} samples @ {Rate} Hz)", TimeFormat.Human(audio.Duration), audio.Length, audio.SampleRate);
        Report(1);

        // 4. Models -------------------------------------------------------------------------------------------------
        var store = new ModelStore(options.ModelsDir);
        var whisperSpec = ModelCatalog.ResolveWhisper(options.Asr.Model);
        Enter(JobStage.DownloadModel, whisperSpec.Name);
        var whisperPath = await store.EnsureAsync(whisperSpec, new SyncProgress<DownloadProgress>(p => Report(p.Fraction ?? 0, $"{whisperSpec.Name} {Describe(p)}")), ct).ConfigureAwait(false);
        string? vadPath = null;
        if (options.UseVad)
        {
            var vadSpec = ModelCatalog.ResolveVad(options.Vad.Model);
            vadPath = await store.EnsureAsync(vadSpec, new SyncProgress<DownloadProgress>(p => Report(p.Fraction ?? 0, $"{vadSpec.Name} {Describe(p)}")), ct).ConfigureAwait(false);
        }

        Report(1);

        // 5. Speech detection ---------------------------------------------------------------------------------------
        IReadOnlyList<SpeechSegment> chunks;
        if (options.UseVad && vadPath is not null)
        {
            Enter(JobStage.DetectSpeech);
            using var vad = new SileroSpeechDetector(vadPath, options.Vad, _logger);
            var segments = await vad.DetectAsync(audio, ct).ConfigureAwait(false);
            chunks = SpeechChunker.Chunk(segments, audio.Duration, options.Chunking);
            var speech = TimeSpan.FromTicks(segments.Sum(s => s.Duration.Ticks));
            _logger.LogInformation("VAD: {Segments} speech segments ({Speech}) grouped into {Chunks} chunks", segments.Count, TimeFormat.Human(speech), chunks.Count);
            foreach (var chunk in chunks)
            {
                _logger.LogDebug("chunk {Chunk} ({Duration})", chunk, TimeFormat.Human(chunk.Duration));
            }

            Report(1, $"{segments.Count} segments → {chunks.Count} chunks");
        }
        else
        {
            chunks = audio.Length > 0 ? [new SpeechSegment(TimeSpan.Zero, audio.Duration)] : [];
        }

        var speechDuration = TimeSpan.FromTicks(chunks.Sum(c => c.Duration.Ticks));

        // 5b. Music / singing fallback: energetic gaps the VAD rejected go to Whisper as tentative chunks.
        IReadOnlyList<SpeechSegment> tentative = [];
        if (options.UseVad && vadPath is not null && options.Fallback.Enabled)
        {
            tentative = FallbackRegions.Find(audio, chunks, options.Fallback);
            if (tentative.Count > 0)
            {
                _logger.LogInformation("Fallback: {Count} energetic non-speech region(s) ({Duration}) will be transcribed tentatively", tentative.Count, TimeFormat.Human(TimeSpan.FromTicks(tentative.Sum(c => c.Duration.Ticks))));
                foreach (var region in tentative)
                {
                    _logger.LogDebug("tentative {Chunk} ({Duration})", region, TimeFormat.Human(region.Duration));
                }
            }
        }

        var tentativeDuration = TimeSpan.FromTicks(tentative.Sum(c => c.Duration.Ticks));
        var work = chunks.Select(c => (Chunk: c, Tentative: false))
            .Concat(tentative.Select(c => (Chunk: c, Tentative: true)))
            .OrderBy(w => w.Chunk.Start)
            .ToList();
        var workDuration = speechDuration + tentativeDuration;

        if (options.Asr.TranslateToEnglish && whisperSpec.Name.Contains("turbo", StringComparison.OrdinalIgnoreCase))
        {
            _logger.LogWarning("{Model} was distilled for transcription only and usually ignores the translate task; use large-v3 or medium for --translate.", whisperSpec.Name);
        }

        if (options.Asr.TranslateToEnglish && whisperSpec.EnglishOnly)
        {
            _logger.LogWarning("{Model} is an English-only model; --translate has no effect.", whisperSpec.Name);
        }

        // 6. Load Whisper ---------------------------------------------------------------------------------------------
        Enter(JobStage.LoadModel, whisperSpec.Name);
        using var recognizer = await WhisperRecognizer.CreateAsync(whisperPath, whisperSpec, options.Asr, _logger, ct).ConfigureAwait(false);
        Report(1, recognizer.RuntimeDescription);

        // 7. Language -------------------------------------------------------------------------------------------------
        var language = options.Asr.Language.Trim().ToLowerInvariant();
        float? languageProbability = null;
        if (language is "" or "auto")
        {
            if (whisperSpec.EnglishOnly)
            {
                language = "en";
            }
            else if (work.Count > 0)
            {
                Enter(JobStage.DetectLanguage);
                var probe = (chunks.Count > 0 ? chunks : tentative).OrderByDescending(c => c.Duration).First();
                var detection = await recognizer.DetectLanguageAsync(audio.Slice(probe.Start, probe.End), ct).ConfigureAwait(false);
                language = detection.Language;
                languageProbability = detection.Probability;
                _logger.LogInformation("Detected language: {Language} (p={Probability:0.00})", language, detection.Probability);
                Report(1, $"{language} ({detection.Probability:P0})");
            }
            else
            {
                language = "und";
            }
        }

        // 8. Transcribe -----------------------------------------------------------------------------------------------
        Enter(JobStage.Transcribe);
        var parts = new List<Transcript>(work.Count);
        var processed = TimeSpan.Zero;
        string? context = null;
        var strict = HallucinationFilterOptions.Strict with { ExtraPhrases = options.Hallucinations.ExtraPhrases };
        var skippedTentative = 0;
        if (options.Fallback.Enabled && languageProbability is { } fileLanguageProbability && fileLanguageProbability < options.Fallback.UncertainFileLanguageProbability)
        {
            _logger.LogWarning("Language detection is unsure ({Language}, p={Probability:0.00}): every chunk is treated as tentative and filtered strictly", language, fileLanguageProbability);
            work = work.Select(w => (w.Chunk, Tentative: true)).ToList();
            tentative = work.Select(w => w.Chunk).ToList();
        }

        for (var i = 0; i < work.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            var (chunk, isTentative) = work[i];
            var clip = EnsureMinimumLength(audio.Slice(chunk.Start, chunk.End), MinWhisperClip);
            var chunkIndex = i;

            if (isTentative && options.Fallback.RequireLanguageMatch && language != "und" && !whisperSpec.EnglishOnly)
            {
                var chunkLanguage = await recognizer.DetectLanguageAsync(clip, ct).ConfigureAwait(false);
                if (!string.Equals(chunkLanguage.Language, language, StringComparison.OrdinalIgnoreCase) || chunkLanguage.Probability < options.Fallback.MinLanguageProbability)
                {
                    _logger.LogDebug("Skipped tentative chunk {Chunk}: sounds like {Lang} (p={P:0.00}), file language is {FileLang}", chunk, chunkLanguage.Language, chunkLanguage.Probability, language);
                    skippedTentative++;
                    processed += chunk.Duration;
                    Report(workDuration > TimeSpan.Zero ? processed / workDuration : 1, $"chunk {i + 1}/{work.Count} (skipped: {chunkLanguage.Language})");
                    continue;
                }
            }

            var chunkProgress = new SyncProgress<int>(p => Report(
                (processed + chunk.Duration * (p / 100.0)) / workDuration,
                $"chunk {chunkIndex + 1}/{work.Count}{(isTentative ? " (music)" : string.Empty)}"));

            var part = await recognizer.TranscribeAsync(clip, new TranscribeRequest
            {
                Offset = timeOffset + chunk.Start,
                Language = language,
                Prompt = options.Asr.ContextAcrossChunks && !isTentative ? context : null,
                Progress = chunkProgress,
            }, ct).ConfigureAwait(false);

            if (isTentative && options.Hallucinations.Enabled)
            {
                var rawCount = part.Segments.Count;
                part = HallucinationFilter.Apply(part, strict, _logger);
                if (rawCount != part.Segments.Count)
                {
                    _logger.LogDebug("Tentative chunk {Chunk}: kept {Kept}/{Total} segment(s)", chunk, part.Segments.Count, rawCount);
                }
            }

            parts.Add(part);
            processed += chunk.Duration;
            var last = part.Segments.Count > 0 ? part.Segments[^1].Text : null;
            Report(workDuration > TimeSpan.Zero ? processed / workDuration : 1, last is null ? $"chunk {i + 1}/{work.Count}" : Truncate(last, 60));
            if (options.Asr.ContextAcrossChunks && !isTentative)
            {
                context = Tail(part.Text, 200);
            }

            foreach (var segment in part.Segments)
            {
                _logger.LogDebug("{Segment}", segment);
            }
        }

        if (skippedTentative > 0)
        {
            _logger.LogInformation("Skipped {Skipped}/{Total} tentative region(s) whose language did not match {Language}", skippedTentative, tentative.Count, language);
        }

        var transcriptLanguage = options.Asr.TranslateToEnglish ? "en" : language;
        var transcript = Transcript.Merge(transcriptLanguage, parts) is var merged
            ? new Transcript(transcriptLanguage, merged.Segments) { Model = whisperSpec.Name, SourceDuration = media.Duration }
            : throw new UnreachableException();

        // 9. Cues -----------------------------------------------------------------------------------------------------
        Enter(JobStage.BuildCues);
        var before = transcript.Segments.Count;
        transcript = HallucinationFilter.Apply(transcript, options.Hallucinations, _logger);
        if (transcript.Segments.Count != before)
        {
            _logger.LogInformation("Hallucination filter removed {Count} segment(s)", before - transcript.Segments.Count);
        }

        var document = CueBuilder.Build(transcript, options.Cues);
        document = new SubtitleDocument(document.Cues) { Language = transcriptLanguage, Title = Path.GetFileNameWithoutExtension(input) };
        _logger.LogInformation("Built {Cues} cues from {Words} words", document.Cues.Count, transcript.Words.Count());
        Report(1);

        // 10. Write ---------------------------------------------------------------------------------------------------
        Enter(JobStage.Write);
        var outputs = new List<string>();
        foreach (var format in options.Formats.Distinct())
        {
            var path = options.OutputPath is { } explicitPath && options.Formats.Count == 1 && !LooksLikeDirectory(explicitPath)
                ? explicitPath
                : DefaultOutputPath(input, transcriptLanguage, SubtitleWriters.Extension(format), options.OutputPath);
            SubtitleWriters.Write(path, document, format);
            outputs.Add(Path.GetFullPath(path));
            _logger.LogInformation("Wrote {Path}", path);
        }

        string? jsonPath = null;
        if (options.SaveTranscriptJson || options.TranscriptJsonPath is not null)
        {
            jsonPath = Path.GetFullPath(options.TranscriptJsonPath ?? DefaultOutputPath(input, transcriptLanguage, TranscriptJson.Extension, options.OutputPath));
            await TranscriptJson.WriteAsync(jsonPath, transcript, ct).ConfigureAwait(false);
            _logger.LogInformation("Wrote {Path}", jsonPath);
        }

        Report(1);

        // 10b. Translate ----------------------------------------------------------------------------------------------
        var translatedOutputs = new List<string>();
        string? translatorName = null;
        if (options.TranslateTo is { Length: > 0 } target && !string.Equals(target, transcriptLanguage, StringComparison.OrdinalIgnoreCase))
        {
            Enter(JobStage.Translate, target);
            var translationOptions = (options.Translation ?? new TranslationOptions { TargetLanguage = target }) with
            {
                TargetLanguage = target,
                SourceLanguage = transcriptLanguage,
            };
            using var translator = new OllamaTranslator(translationOptions, _logger);
            translatorName = translator.Name;
            if (!await translator.IsAvailableAsync(ct).ConfigureAwait(false))
            {
                throw new InvalidOperationException($"Translation requested but Ollama model '{translationOptions.Model}' is not available at {translationOptions.Endpoint}. Start Ollama and run 'ollama pull {translationOptions.Model}'.");
            }

            var translated = await SubtitleTranslator.TranslateAsync(
                document, translator, target, options.Cues, translationOptions.BatchSize, translationOptions.ContextLines,
                new SyncProgress<TranslationProgress>(p => Report(p.Fraction, p.LastLine is null ? null : Truncate(p.LastLine, 60))),
                _logger, ct).ConfigureAwait(false);

            foreach (var format in options.Formats.Distinct())
            {
                var path = DefaultOutputPath(input, target, SubtitleWriters.Extension(format), options.OutputPath);
                SubtitleWriters.Write(path, translated, format);
                translatedOutputs.Add(Path.GetFullPath(path));
                _logger.LogInformation("Wrote {Path}", path);
            }

            if (options.Bilingual)
            {
                var bilingual = SubtitleTranslator.Bilingual(document, translated);
                var path = DefaultOutputPath(input, $"{transcriptLanguage}-{target}", ".srt", options.OutputPath);
                SubtitleWriters.Write(path, bilingual, SubtitleFormat.Srt);
                translatedOutputs.Add(Path.GetFullPath(path));
                _logger.LogInformation("Wrote {Path}", path);
            }

            Report(1);
        }
        else if (options.TranslateTo is { Length: > 0 })
        {
            _logger.LogInformation("Translation target {Target} equals the spoken language; nothing to translate", options.TranslateTo);
        }

        // 11. Embed ---------------------------------------------------------------------------------------------------
        string? embedded = null;
        if (options.EmbedIntoVideo)
        {
            if (!media.HasVideo)
            {
                _logger.LogWarning("Input has no video stream; skipping embedding.");
            }
            else
            {
                Enter(JobStage.Embed);
                var srt = outputs.FirstOrDefault(o => o.EndsWith(".srt", StringComparison.OrdinalIgnoreCase));
                if (srt is null)
                {
                    srt = DefaultOutputPath(input, transcriptLanguage, ".srt", options.OutputPath);
                    SubtitleWriters.Write(srt, document, SubtitleFormat.Srt);
                }

                embedded = options.EmbedOutputPath ?? SubtitleMuxer.DefaultOutputPath(input);
                if (options.EmbedOutputPath is null && options.OutputPath is { } hint && LooksLikeDirectory(hint))
                {
                    embedded = Path.Combine(hint, Path.GetFileName(embedded));
                }

                await new SubtitleMuxer(ffmpeg).EmbedAsync(input, srt, transcriptLanguage, embedded, ct).ConfigureAwait(false);
                _logger.LogInformation("Embedded subtitles into {Path}", embedded);
                Report(1);
            }
        }

        Enter(JobStage.Done);
        return new SubtitleJobResult(
            outputs,
            translatedOutputs,
            translatorName,
            jsonPath,
            embedded,
            transcript,
            document,
            media,
            transcriptLanguage,
            languageProbability,
            recognizer.RuntimeDescription,
            chunks.Count,
            tentative.Count,
            speechDuration,
            tentativeDuration,
            total.Elapsed,
            timings);
    }

    /// <summary>True when the path names a directory: it exists as one, ends with a separator, or has no extension.</summary>
    public static bool LooksLikeDirectory(string path) =>
        Directory.Exists(path) ||
        path.EndsWith(Path.DirectorySeparatorChar) ||
        path.EndsWith(Path.AltDirectorySeparatorChar) ||
        string.IsNullOrEmpty(Path.GetExtension(path));

    /// <summary><c>dir/name.lang.ext</c>; when <paramref name="outputHint"/> is a directory the file goes there instead.</summary>
    public static string DefaultOutputPath(string inputPath, string? language, string extension, string? outputHint = null)
    {
        var dir = Path.GetDirectoryName(inputPath) ?? string.Empty;
        if (outputHint is not null && LooksLikeDirectory(outputHint))
        {
            dir = outputHint;
            Directory.CreateDirectory(dir);
        }

        var stem = Path.GetFileNameWithoutExtension(inputPath);
        var langPart = string.IsNullOrEmpty(language) || language == "und" ? string.Empty : "." + language;
        return Path.Combine(dir, stem + langPart + extension);
    }

    private static PcmAudio EnsureMinimumLength(PcmAudio clip, TimeSpan minimum)
    {
        if (clip.Duration >= minimum)
        {
            return clip;
        }

        var buffer = new float[(int)Math.Ceiling(minimum.TotalSeconds * clip.SampleRate)];
        clip.Samples.Span.CopyTo(buffer);
        return new PcmAudio(buffer, clip.SampleRate);
    }

    private static string Describe(DownloadProgress p)
    {
        var mb = p.BytesReceived / 1e6;
        var total = p.TotalBytes is { } t ? $"/{t / 1e6:0} MB" : " MB";
        var speed = p.BytesPerSecond > 0 ? $" @ {p.BytesPerSecond / 1e6:0.0} MB/s" : string.Empty;
        return $"{mb:0}{total}{speed}";
    }

    private static string Truncate(string text, int max) => text.Length <= max ? text : text[..(max - 1)] + "…";

    private static string? Tail(string text, int maxChars)
    {
        text = text.Trim();
        if (text.Length == 0)
        {
            return null;
        }

        if (text.Length <= maxChars)
        {
            return text;
        }

        var cut = text.LastIndexOf(' ', text.Length - maxChars) is var idx && idx >= 0 ? idx : text.Length - maxChars;
        return text[cut..].Trim();
    }
}

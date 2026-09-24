using System.CommandLine;
using System.Globalization;
using Bwl.Murching.Asr;
using Bwl.Murching.Common;
using Bwl.Murching.Models;
using Bwl.Murching.Pipeline;
using Bwl.Murching.Runtime;
using Bwl.Murching.Subtitles;
using Bwl.Murching.Vad;
using Microsoft.Extensions.Logging;
using Spectre.Console;

namespace Bwl.Murching.Cli.Commands;

internal static class SubsCommand
{
    public static Command Create()
    {
        var input = new Argument<FileInfo>("input") { Description = "Audio or video file to subtitle." }.AcceptExistingOnly();
        var output = new Option<string?>("--output", "-o") { Description = "Output file (single format) or directory. Default: next to the input as <name>.<lang>.<ext>." };
        var format = new Option<string[]>("--format", "-f")
        {
            Description = "Subtitle format(s): srt, vtt, ass, txt. Repeat or comma-separate for several.",
            DefaultValueFactory = _ => ["srt"],
            AllowMultipleArgumentsPerToken = true,
        };
        var model = new Option<string>("--model", "-m")
        {
            Description = $"Whisper model name (see 'murch models') or path to a ggml file. Default: {ModelCatalog.DefaultWhisperModel}.",
            DefaultValueFactory = _ => ModelCatalog.DefaultWhisperModel,
        };
        var language = new Option<string>("--language", "-l") { Description = "Spoken language as ISO 639-1 code (ru, en, ...) or 'auto'.", DefaultValueFactory = _ => "auto" };
        var translate = new Option<bool>("--translate") { Description = "Translate to English with Whisper instead of transcribing." };
        var device = new Option<string>("--device", "-d") { Description = "Compute device: auto, cuda, cpu.", DefaultValueFactory = _ => "auto" }.AcceptOnlyFromAmong("auto", "cuda", "cpu");
        var threads = new Option<int?>("--threads", "-t") { Description = "CPU threads for whisper." };
        var beam = new Option<int>("--beam") { Description = "Beam size (1 = greedy, faster).", DefaultValueFactory = _ => 5 };
        var prompt = new Option<string?>("--prompt") { Description = "Initial prompt: names, jargon, punctuation style hints." };
        var context = new Option<bool>("--context") { Description = "Feed the previous chunk's text as prompt to the next chunk." };
        var noVad = new Option<bool>("--no-vad") { Description = "Disable voice activity detection (feed the whole file to whisper)." };
        var vadThreshold = new Option<float>("--vad-threshold") { Description = "Speech probability threshold for the VAD (lower catches quiet speech over music).", DefaultValueFactory = _ => 0.5f };
        var vadMinSilence = new Option<int>("--vad-min-silence") { Description = "Silence (ms) that ends a speech segment.", DefaultValueFactory = _ => 300 };
        var noDtw = new Option<bool>("--no-dtw") { Description = "Disable DTW token alignment (faster, coarser word timing)." };
        var noFilter = new Option<bool>("--no-filter") { Description = "Disable the hallucination filter." };
        var maxLineLength = new Option<int>("--max-line-length") { Description = "Characters per subtitle line.", DefaultValueFactory = _ => 42 };
        var maxLines = new Option<int>("--max-lines") { Description = "Lines per subtitle.", DefaultValueFactory = _ => 2 };
        var maxDuration = new Option<double>("--max-duration") { Description = "Maximum subtitle duration in seconds.", DefaultValueFactory = _ => 7 };
        var cps = new Option<double>("--cps") { Description = "Reading speed cap, characters per second.", DefaultValueFactory = _ => 21 };
        var json = new Option<bool>("--json") { Description = "Also save the transcript with word timings as <name>.<lang>.murch.json (needed for dubbing)." };
        var embed = new Option<bool>("--embed") { Description = "Mux the subtitles into a copy of the video (<name>.subbed.mkv/mp4)." };
        var stream = new Option<int?>("--audio-stream") { Description = "Audio stream index (0-based among audio streams)." };
        var from = new Option<string?>("--from") { Description = "Start position (e.g. 90, 1:30, 00:01:30.5)." };
        var to = new Option<string?>("--to") { Description = "End position." };
        var preview = new Option<int>("--preview") { Description = "Print the first N cues after finishing.", DefaultValueFactory = _ => 6 };

        var command = new Command("subs", "Transcribe speech and write subtitles.")
        {
            input, output, format, model, language, translate, device, threads, beam, prompt, context,
            noVad, vadThreshold, vadMinSilence, noDtw, noFilter, maxLineLength, maxLines, maxDuration, cps, json, embed, stream, from, to, preview,
        };

        command.SetAction(async (parseResult, ct) =>
        {
            var verbosity = GlobalOptions.VerbosityOf(parseResult);
            var quiet = parseResult.GetValue(GlobalOptions.Quiet);
            using var loggerFactory = GlobalOptions.CreateLoggerFactory(verbosity);
            var logger = loggerFactory.CreateLogger("murch");

            var formats = new List<SubtitleFormat>();
            foreach (var token in parseResult.GetValue(format)!.SelectMany(f => f.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)))
            {
                if (!SubtitleWriters.TryParseFormat(token, out var parsed))
                {
                    AnsiConsole.MarkupLineInterpolated($"[red]Unknown subtitle format '{token}'. Use srt, vtt, ass or txt.[/]");
                    return 2;
                }

                formats.Add(parsed);
            }

            var options = new SubtitleJobOptions
            {
                InputPath = parseResult.GetValue(input)!.FullName,
                OutputPath = parseResult.GetValue(output),
                Formats = formats,
                Asr = new AsrOptions
                {
                    Model = parseResult.GetValue(model)!,
                    Language = parseResult.GetValue(language)!,
                    TranslateToEnglish = parseResult.GetValue(translate),
                    Device = Enum.Parse<ComputeDevice>(parseResult.GetValue(device)!, ignoreCase: true),
                    Threads = parseResult.GetValue(threads),
                    BeamSize = Math.Max(1, parseResult.GetValue(beam)),
                    InitialPrompt = parseResult.GetValue(prompt),
                    ContextAcrossChunks = parseResult.GetValue(context),
                    DtwTimestamps = !parseResult.GetValue(noDtw),
                },
                UseVad = !parseResult.GetValue(noVad),
                Vad = new VadOptions
                {
                    Threshold = parseResult.GetValue(vadThreshold),
                    MinSilenceDuration = TimeSpan.FromMilliseconds(parseResult.GetValue(vadMinSilence)),
                },
                Cues = new CueBuilderOptions
                {
                    MaxLineLength = parseResult.GetValue(maxLineLength),
                    MaxLines = parseResult.GetValue(maxLines),
                    MaxDuration = TimeSpan.FromSeconds(parseResult.GetValue(maxDuration)),
                    MaxCharsPerSecond = parseResult.GetValue(cps),
                },
                Hallucinations = new HallucinationFilterOptions { Enabled = !parseResult.GetValue(noFilter) },
                AudioStreamIndex = parseResult.GetValue(stream),
                Start = ParseTime(parseResult.GetValue(from)),
                End = ParseTime(parseResult.GetValue(to)),
                SaveTranscriptJson = parseResult.GetValue(json),
                EmbedIntoVideo = parseResult.GetValue(embed),
            };

            var job = new SubtitleJob(options, logger);
            SubtitleJobResult result;
            try
            {
                result = quiet || verbosity > 0
                    ? await job.RunAsync(new SyncProgress<JobProgress>(p => { }), ct).ConfigureAwait(false)
                    : await RunWithProgressAsync(job, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "Subtitle job failed");
                AnsiConsole.MarkupLineInterpolated($"[red]Error:[/] {ex.Message}");
                if (ex.InnerException is not null)
                {
                    AnsiConsole.MarkupLineInterpolated($"[grey]{ex.InnerException.Message}[/]");
                }

                return 1;
            }

            PrintSummary(result, parseResult.GetValue(preview));
            return 0;
        });

        return command;
    }

    private static async Task<SubtitleJobResult> RunWithProgressAsync(SubtitleJob job, CancellationToken ct)
    {
        SubtitleJobResult? result = null;
        await AnsiConsole.Progress()
            .AutoClear(false)
            .HideCompleted(false)
            .Columns(new TaskDescriptionColumn { Alignment = Justify.Left }, new ProgressBarColumn(), new PercentageColumn(), new ElapsedTimeColumn(), new SpinnerColumn())
            .StartAsync(async ctx =>
            {
                var tasks = new Dictionary<JobStage, ProgressTask>();
                var gate = new Lock();
                var progress = new SyncProgress<JobProgress>(p =>
                {
                    lock (gate)
                    {
                        if (p.Stage == JobStage.Done)
                        {
                            foreach (var t in tasks.Values)
                            {
                                t.Value = t.MaxValue;
                                t.StopTask();
                            }

                            return;
                        }

                        if (!tasks.TryGetValue(p.Stage, out var task))
                        {
                            foreach (var t in tasks.Values.Where(t => !t.IsFinished))
                            {
                                t.Value = t.MaxValue;
                                t.StopTask();
                            }

                            task = ctx.AddTask(Label(p.Stage), maxValue: 100);
                            tasks[p.Stage] = task;
                        }

                        task.Value = Math.Clamp(p.Fraction, 0, 1) * 100;
                        task.Description = p.Message is null ? Label(p.Stage) : $"{Label(p.Stage)} [grey]{Markup.Escape(p.Message)}[/]";
                    }
                });

                result = await job.RunAsync(progress, ct).ConfigureAwait(false);
            }).ConfigureAwait(false);

        return result!;
    }

    private static string Label(JobStage stage) => stage switch
    {
        JobStage.Setup => "Tools",
        JobStage.Probe => "Probe",
        JobStage.ExtractAudio => "Decode audio",
        JobStage.DownloadModel => "Models",
        JobStage.DetectSpeech => "Voice activity",
        JobStage.LoadModel => "Load model",
        JobStage.DetectLanguage => "Language",
        JobStage.Transcribe => "Transcribe",
        JobStage.BuildCues => "Build cues",
        JobStage.Write => "Write",
        JobStage.Embed => "Embed",
        _ => stage.ToString(),
    };

    private static void PrintSummary(SubtitleJobResult r, int previewCount)
    {
        var table = new Table().Border(TableBorder.Rounded).HideHeaders().AddColumn("k").AddColumn("v");
        table.AddRow("Input", Markup.Escape(Path.GetFileName(r.Media.Path)) + $" [grey]({TimeFormat.Human(r.Media.Duration)}, {r.Media.Format})[/]");
        table.AddRow("Language", r.Language + (r.LanguageProbability is { } p ? $" [grey]({p:P0})[/]" : string.Empty));
        table.AddRow("Engine", Markup.Escape(r.RuntimeDescription));
        table.AddRow("Speech", $"{TimeFormat.Human(r.SpeechDuration)} in {r.ChunkCount} chunk(s)");
        table.AddRow("Result", $"{r.Transcript.Segments.Count} segments, {r.Transcript.Words.Count()} words → {r.Document.Cues.Count} cues");
        table.AddRow("Time", $"{TimeFormat.Human(r.Elapsed)} [grey]({r.RealTimeFactor:0.0}× real time; {StageBreakdown(r)})[/]");
        foreach (var path in r.OutputPaths)
        {
            table.AddRow("Output", $"[green]{Markup.Escape(path)}[/]");
        }

        if (r.TranscriptJsonPath is not null)
        {
            table.AddRow("Transcript", $"[green]{Markup.Escape(r.TranscriptJsonPath)}[/]");
        }

        if (r.EmbeddedVideoPath is not null)
        {
            table.AddRow("Video", $"[green]{Markup.Escape(r.EmbeddedVideoPath)}[/]");
        }

        AnsiConsole.Write(table);

        if (previewCount > 0 && r.Document.Cues.Count > 0)
        {
            AnsiConsole.WriteLine();
            foreach (var cue in r.Document.Cues.Take(previewCount))
            {
                AnsiConsole.MarkupLineInterpolated($"[grey]{TimeFormat.Srt(cue.Start)} --> {TimeFormat.Srt(cue.End)}[/]");
                foreach (var line in cue.Lines)
                {
                    AnsiConsole.WriteLine("  " + line);
                }
            }

            if (r.Document.Cues.Count > previewCount)
            {
                AnsiConsole.MarkupLineInterpolated($"[grey]… {r.Document.Cues.Count - previewCount} more[/]");
            }
        }
    }

    private static string StageBreakdown(SubtitleJobResult r)
    {
        var interesting = new[] { JobStage.ExtractAudio, JobStage.DetectSpeech, JobStage.LoadModel, JobStage.Transcribe };
        return string.Join(", ", interesting
            .Where(s => r.StageTimings.TryGetValue(s, out var t) && t > TimeSpan.FromMilliseconds(50))
            .Select(s => $"{Label(s).ToLowerInvariant()} {r.StageTimings[s].TotalSeconds:0.0}s"));
    }

    /// <summary>Accepts seconds (<c>95.5</c>), <c>m:ss</c>, <c>h:mm:ss(.fff)</c>.</summary>
    internal static TimeSpan? ParseTime(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        text = text.Trim();
        if (double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds))
        {
            return TimeSpan.FromSeconds(seconds);
        }

        var parts = text.Split(':');
        if (parts.Length is 2 or 3 && parts.All(p => double.TryParse(p, NumberStyles.Float, CultureInfo.InvariantCulture, out _)))
        {
            var values = parts.Select(p => double.Parse(p, CultureInfo.InvariantCulture)).ToArray();
            return parts.Length == 2
                ? TimeSpan.FromSeconds(values[0] * 60 + values[1])
                : TimeSpan.FromSeconds(values[0] * 3600 + values[1] * 60 + values[2]);
        }

        throw new ArgumentException($"Cannot parse time '{text}'. Use seconds, m:ss or h:mm:ss.");
    }
}

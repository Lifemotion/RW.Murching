using System.CommandLine;
using Bwl.Murching.Common;
using Bwl.Murching.Dubbing;
using Bwl.Murching.Pipeline;
using Microsoft.Extensions.Logging;
using Spectre.Console;

namespace Bwl.Murching.Cli.Commands;

internal static class DubCommand
{
    public static Command Create()
    {
        var input = new Argument<FileInfo>("input") { Description = "Video to re-voice." }.AcceptExistingOnly();
        var to = new Option<string>("--to") { Description = "Target language (ISO 639-1), e.g. ru.", Required = true };
        var subs = new Option<FileInfo?>("--subs") { Description = "Translated subtitles (.srt/.vtt) to voice. Default: transcribe + translate first." };
        var output = new Option<string?>("--output", "-o") { Description = "Output video. Default: <name>.dub.<lang>.<ext>." };
        var engine = new Option<string>("--engine") { Description = "TTS engine: xtts (XTTS-v2, voice cloning, 17 languages) or chatterbox.", DefaultValueFactory = _ => "xtts" }.AcceptOnlyFromAmong("xtts", "chatterbox");
        var device = new Option<string>("--device", "-d") { Description = "cuda or cpu for the TTS model.", DefaultValueFactory = _ => "cuda" };
        var python = new Option<string?>("--python") { Description = "python.exe of the TTS venv (default: tools/tts-venv or MURCH_TTS_PYTHON)." };
        var duck = new Option<double>("--duck") { Description = "Gain of the original while the dub speaks (0..1).", DefaultValueFactory = _ => 0.2 };
        var voiceGain = new Option<double>("--voice-gain") { Description = "Dub loudness relative to the original speech.", DefaultValueFactory = _ => 1.0 };
        var maxSpeed = new Option<double>("--max-speedup") { Description = "Maximum tempo factor to fit a slot.", DefaultValueFactory = _ => 1.35 };
        var noOriginal = new Option<bool>("--no-original-track") { Description = "Do not keep the original audio as a second track." };
        var workDir = new Option<string?>("--work-dir") { Description = "Keep intermediate WAVs here." };

        var command = new Command("dub", "Re-voice a video in another language with the original speaker's cloned voice.")
        {
            input, to, subs, output, engine, device, python, duck, voiceGain, maxSpeed, noOriginal, workDir,
        };

        command.SetAction(async (parseResult, ct) =>
        {
            var verbosity = GlobalOptions.VerbosityOf(parseResult);
            var quiet = parseResult.GetValue(GlobalOptions.Quiet);
            using var loggerFactory = GlobalOptions.CreateLoggerFactory(verbosity);
            var logger = loggerFactory.CreateLogger("murch");

            var options = new DubbingOptions
            {
                InputPath = parseResult.GetValue(input)!.FullName,
                TargetLanguage = parseResult.GetValue(to)!.Trim().ToLowerInvariant(),
                SubtitlePath = parseResult.GetValue(subs)?.FullName,
                OutputPath = parseResult.GetValue(output),
                Engine = parseResult.GetValue(engine)!,
                Device = parseResult.GetValue(device)!,
                PythonPath = parseResult.GetValue(python),
                Duck = Math.Clamp(parseResult.GetValue(duck), 0, 1),
                VoiceGain = parseResult.GetValue(voiceGain),
                MaxSpeedUp = Math.Max(1.0, parseResult.GetValue(maxSpeed)),
                KeepOriginalTrack = !parseResult.GetValue(noOriginal),
                WorkDir = parseResult.GetValue(workDir),
                KeepWorkFiles = parseResult.GetValue(workDir) is not null,
            };

            var job = new DubJob(options, logger);
            DubResult result;
            try
            {
                result = quiet || verbosity > 0
                    ? await job.RunAsync(null, ct).ConfigureAwait(false)
                    : await RunWithProgressAsync(job, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "Dubbing failed");
                AnsiConsole.MarkupLineInterpolated($"[red]Error:[/] {ex.Message}");
                if (ex.InnerException is not null)
                {
                    AnsiConsole.MarkupLineInterpolated($"[grey]{ex.InnerException.Message}[/]");
                }

                return 1;
            }

            var table = new Table().Border(TableBorder.Rounded).HideHeaders().AddColumn("k").AddColumn("v");
            table.AddRow("Output", $"[green]{Markup.Escape(result.OutputPath)}[/]");
            table.AddRow("Script", Markup.Escape(result.ScriptPath));
            table.AddRow("Engine", Markup.Escape(result.Engine));
            table.AddRow("Utterances", $"{result.Units.Count}, {result.Overruns} overrun(s), drift {TimeFormat.Human(result.TotalDrift)}");
            var tempos = result.Units.Where(u => u.Tempo > 1.001).ToList();
            table.AddRow("Tempo", tempos.Count == 0 ? "no time-stretching needed" : $"{tempos.Count} stretched, max ×{tempos.Max(u => u.Tempo):0.00}, mean ×{tempos.Average(u => u.Tempo):0.00}");
            table.AddRow("Time", TimeFormat.Human(result.Elapsed));
            AnsiConsole.Write(table);
            return 0;
        });

        return command;
    }

    private static async Task<DubResult> RunWithProgressAsync(DubJob job, CancellationToken ct)
    {
        DubResult? result = null;
        await AnsiConsole.Progress()
            .AutoClear(false)
            .HideCompleted(false)
            .Columns(new TaskDescriptionColumn { Alignment = Justify.Left }, new ProgressBarColumn(), new PercentageColumn(), new ElapsedTimeColumn(), new SpinnerColumn())
            .StartAsync(async ctx =>
            {
                var tasks = new Dictionary<DubStage, ProgressTask>();
                var gate = new Lock();
                var progress = new SyncProgress<DubProgress>(p =>
                {
                    lock (gate)
                    {
                        if (p.Stage == DubStage.Done)
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

                            task = ctx.AddTask(p.Stage.ToString(), maxValue: 100);
                            tasks[p.Stage] = task;
                        }

                        task.Value = Math.Clamp(p.Fraction, 0, 1) * 100;
                        task.Description = p.Message is null ? p.Stage.ToString() : $"{p.Stage} [grey]{Markup.Escape(p.Message)}[/]";
                    }
                });
                result = await job.RunAsync(progress, ct).ConfigureAwait(false);
            }).ConfigureAwait(false);
        return result!;
    }
}

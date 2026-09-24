using System.CommandLine;
using Bwl.Murching.Asr;
using Bwl.Murching.Common;
using Bwl.Murching.Pipeline;
using Bwl.Murching.Subtitles;
using Spectre.Console;

namespace Bwl.Murching.Cli.Commands;

/// <summary>Rebuilds subtitles from a saved transcript (<c>*.murch.json</c>) — lets you tune layout options without re-running ASR.</summary>
internal static class CuesCommand
{
    public static Command Create()
    {
        var input = new Argument<FileInfo>("transcript") { Description = "Transcript JSON written by 'murch subs --json'." }.AcceptExistingOnly();
        var output = new Option<string?>("--output", "-o") { Description = "Output file or directory. Default: next to the transcript." };
        var format = new Option<string[]>("--format", "-f") { Description = "srt, vtt, ass, txt.", DefaultValueFactory = _ => ["srt"], AllowMultipleArgumentsPerToken = true };
        var maxLineLength = new Option<int>("--max-line-length") { DefaultValueFactory = _ => 42 };
        var maxLines = new Option<int>("--max-lines") { DefaultValueFactory = _ => 2 };
        var maxDuration = new Option<double>("--max-duration") { Description = "Seconds.", DefaultValueFactory = _ => 7 };
        var cps = new Option<double>("--cps") { DefaultValueFactory = _ => 21 };
        var pauseSplit = new Option<double>("--pause-split") { Description = "Pause (seconds) that always starts a new cue.", DefaultValueFactory = _ => 1.0 };
        var noFilter = new Option<bool>("--no-filter") { Description = "Disable the hallucination filter." };
        var preview = new Option<int>("--preview") { Description = "Print the first N cues (0 = none, -1 = all).", DefaultValueFactory = _ => 12 };

        var command = new Command("cues", "Rebuild subtitle cues from a saved transcript JSON (fast layout tuning, no ASR).")
        {
            input, output, format, maxLineLength, maxLines, maxDuration, cps, pauseSplit, noFilter, preview,
        };

        command.SetAction(async (parseResult, ct) =>
        {
            var path = parseResult.GetValue(input)!.FullName;
            var transcript = await TranscriptJson.ReadAsync(path, ct).ConfigureAwait(false);
            if (!parseResult.GetValue(noFilter))
            {
                transcript = HallucinationFilter.Apply(transcript);
            }

            var options = new CueBuilderOptions
            {
                MaxLineLength = parseResult.GetValue(maxLineLength),
                MaxLines = parseResult.GetValue(maxLines),
                MaxDuration = TimeSpan.FromSeconds(parseResult.GetValue(maxDuration)),
                MaxCharsPerSecond = parseResult.GetValue(cps),
                PauseSplit = TimeSpan.FromSeconds(parseResult.GetValue(pauseSplit)),
            };
            var document = CueBuilder.Build(transcript, options);
            document = new SubtitleDocument(document.Cues) { Language = transcript.Language, Title = Path.GetFileName(path) };

            var stem = path.EndsWith(TranscriptJson.Extension, StringComparison.OrdinalIgnoreCase) ? path[..^TranscriptJson.Extension.Length] : Path.ChangeExtension(path, null);
            foreach (var token in parseResult.GetValue(format)!.SelectMany(f => f.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)))
            {
                if (!SubtitleWriters.TryParseFormat(token, out var fmt))
                {
                    AnsiConsole.MarkupLineInterpolated($"[red]Unknown subtitle format '{token}'.[/]");
                    return 2;
                }

                var hint = parseResult.GetValue(output);
                var target = hint is not null && !SubtitleJob.LooksLikeDirectory(hint)
                    ? hint
                    : SubtitleJob.DefaultOutputPath(stem + ".x", null, SubtitleWriters.Extension(fmt), hint);
                SubtitleWriters.Write(target, document, fmt);
                AnsiConsole.MarkupLineInterpolated($"[green]{target}[/]  ({document.Cues.Count} cues from {transcript.Words.Count()} words)");
            }

            var n = parseResult.GetValue(preview);
            var shown = n < 0 ? document.Cues : document.Cues.Take(n);
            foreach (var cue in shown)
            {
                AnsiConsole.MarkupLineInterpolated($"[grey]{cue.Index,4} {TimeFormat.Srt(cue.Start)} --> {TimeFormat.Srt(cue.End)}  {cue.CharactersPerSecond,4:0.0} cps[/]");
                foreach (var line in cue.Lines)
                {
                    AnsiConsole.WriteLine("       " + line);
                }
            }

            return 0;
        });

        return command;
    }
}

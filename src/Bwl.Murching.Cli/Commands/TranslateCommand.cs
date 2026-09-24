using System.CommandLine;
using Bwl.Murching.Asr;
using Bwl.Murching.Pipeline;
using Bwl.Murching.Subtitles;
using Bwl.Murching.Translation;
using Microsoft.Extensions.Logging;
using Spectre.Console;

namespace Bwl.Murching.Cli.Commands;

/// <summary>Translates an existing subtitle file (.srt/.vtt) or a saved transcript (.murch.json) with a local Ollama model.</summary>
internal static class TranslateCommand
{
    public static Command Create()
    {
        var input = new Argument<FileInfo[]>("input") { Description = "Subtitle files (.srt, .vtt) or transcripts (.murch.json).", Arity = ArgumentArity.OneOrMore };
        var to = new Option<string>("--to") { Description = "Target language, ISO 639-1.", Required = true };
        var from = new Option<string?>("--from") { Description = "Source language when the file name does not tell (e.g. talk.en.srt does)." };
        var model = new Option<string>("--model", "-m") { Description = "Ollama model.", DefaultValueFactory = _ => TranslationOptions.DefaultModel };
        var endpoint = new Option<string>("--endpoint") { Description = "Ollama base URL.", DefaultValueFactory = _ => "http://localhost:11434" };
        var glossary = new Option<string?>("--glossary") { Description = "Hints for the translator: names, terminology, register." };
        var bilingual = new Option<bool>("--bilingual") { Description = "Also write <name>.<from>-<to>.srt with both languages." };
        var format = new Option<string[]>("--format", "-f") { Description = "Output formats: srt, vtt, ass, txt.", DefaultValueFactory = _ => ["srt", "txt"], AllowMultipleArgumentsPerToken = true };
        var output = new Option<string?>("--output", "-o") { Description = "Output directory. Default: next to the input." };
        var batch = new Option<int>("--batch") { Description = "Cues per request.", DefaultValueFactory = _ => 24 };
        var maxLineLength = new Option<int>("--max-line-length") { DefaultValueFactory = _ => 42 };
        var preview = new Option<int>("--preview") { Description = "Print the first N translated cues.", DefaultValueFactory = _ => 6 };

        var command = new Command("translate", "Translate subtitles with a local LLM (Ollama).")
        {
            input, to, from, model, endpoint, glossary, bilingual, format, output, batch, maxLineLength, preview,
        };

        command.SetAction(async (parseResult, ct) =>
        {
            var verbosity = GlobalOptions.VerbosityOf(parseResult);
            using var loggerFactory = GlobalOptions.CreateLoggerFactory(verbosity);
            var logger = loggerFactory.CreateLogger("murch");
            var target = parseResult.GetValue(to)!.Trim().ToLowerInvariant();

            var formats = new List<SubtitleFormat>();
            foreach (var token in parseResult.GetValue(format)!.SelectMany(f => f.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)))
            {
                if (!SubtitleWriters.TryParseFormat(token, out var parsed))
                {
                    AnsiConsole.MarkupLineInterpolated($"[red]Unknown subtitle format '{token}'.[/]");
                    return 2;
                }

                formats.Add(parsed);
            }

            var layout = new CueBuilderOptions { MaxLineLength = parseResult.GetValue(maxLineLength) };
            var options = new TranslationOptions
            {
                TargetLanguage = target,
                SourceLanguage = parseResult.GetValue(from),
                Model = parseResult.GetValue(model)!,
                Endpoint = new Uri(parseResult.GetValue(endpoint)!),
                Glossary = parseResult.GetValue(glossary),
                BatchSize = parseResult.GetValue(batch),
            };

            foreach (var file in parseResult.GetValue(input)!)
            {
                if (!file.Exists)
                {
                    AnsiConsole.MarkupLineInterpolated($"[red]Not found:[/] {file.FullName}");
                    return 2;
                }

                SubtitleDocument document;
                string stem;
                if (file.Name.EndsWith(TranscriptJson.Extension, StringComparison.OrdinalIgnoreCase))
                {
                    var transcript = HallucinationFilter.Apply(await TranscriptJson.ReadAsync(file.FullName, ct).ConfigureAwait(false));
                    document = CueBuilder.Build(transcript, layout);
                    document = new SubtitleDocument(document.Cues) { Language = transcript.Language, Title = file.Name };
                    stem = file.Name[..^TranscriptJson.Extension.Length];
                }
                else
                {
                    document = SrtParser.ParseFile(file.FullName);
                    stem = Path.GetFileNameWithoutExtension(file.Name);
                }

                // Strip a trailing language tag from the stem (talk.en -> talk) so the outputs become talk.<to>.srt.
                var source = options.SourceLanguage ?? document.Language;
                if (stem.Split('.') is { Length: > 1 } parts && parts[^1].Length is 2 or 3 && parts[^1].All(char.IsLetter))
                {
                    source ??= parts[^1];
                    stem = string.Join('.', parts[..^1]);
                }

                var perFile = options with { SourceLanguage = source };
                using var translator = new OllamaTranslator(perFile, logger);
                if (!await translator.IsAvailableAsync(ct).ConfigureAwait(false))
                {
                    AnsiConsole.MarkupLineInterpolated($"[red]Ollama model {perFile.Model} is not available at {perFile.Endpoint}.[/] Start Ollama and run: ollama pull {perFile.Model}");
                    return 1;
                }

                var dir = parseResult.GetValue(output) ?? file.DirectoryName ?? ".";
                Directory.CreateDirectory(dir);
                SubtitleDocument translated = null!;
                await AnsiConsole.Progress()
                    .AutoClear(false)
                    .Columns(new TaskDescriptionColumn { Alignment = Justify.Left }, new ProgressBarColumn(), new PercentageColumn(), new ElapsedTimeColumn())
                    .StartAsync(async ctx =>
                    {
                        var task = ctx.AddTask($"{Markup.Escape(stem)} → {target}", maxValue: Math.Max(1, document.Cues.Count));
                        var progress = new SyncProgress<TranslationProgress>(p =>
                        {
                            task.Value = p.Done;
                            task.Description = $"{Markup.Escape(stem)} → {target} [grey]{Markup.Escape(p.LastLine is { } l && l.Length > 50 ? l[..50] + "…" : p.LastLine ?? string.Empty)}[/]";
                        });
                        translated = await SubtitleTranslator.TranslateAsync(document, translator, target, layout, perFile.BatchSize, perFile.ContextLines, progress, logger, ct).ConfigureAwait(false);
                    }).ConfigureAwait(false);

                foreach (var fmt in formats)
                {
                    var path = Path.Combine(dir, $"{stem}.{target}{SubtitleWriters.Extension(fmt)}");
                    SubtitleWriters.Write(path, translated, fmt);
                    AnsiConsole.MarkupLineInterpolated($"[green]{path}[/]");
                }

                if (parseResult.GetValue(bilingual))
                {
                    var path = Path.Combine(dir, $"{stem}.{source ?? "src"}-{target}.srt");
                    SubtitleWriters.Write(path, SubtitleTranslator.Bilingual(document, translated), SubtitleFormat.Srt);
                    AnsiConsole.MarkupLineInterpolated($"[green]{path}[/]");
                }

                var n = parseResult.GetValue(preview);
                foreach (var (o, t) in document.Cues.Zip(translated.Cues).Take(n))
                {
                    AnsiConsole.MarkupLineInterpolated($"[grey]{string.Join(' ', o.Lines)}[/]");
                    AnsiConsole.WriteLine("  " + string.Join(' ', t.Lines));
                }
            }

            return 0;
        });

        return command;
    }
}

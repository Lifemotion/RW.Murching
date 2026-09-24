using System.CommandLine;
using Spectre.Console;

namespace Bwl.Murching.Cli.Commands;

internal static class DubCommand
{
    public static Command Create()
    {
        var input = new Argument<FileInfo>("input") { Description = "Video to dub." };
        var target = new Option<string>("--to") { Description = "Target language (ISO 639-1).", Required = true };
        var command = new Command("dub", "Re-voice a video in another language (translation + TTS). Coming next.") { input, target };
        command.SetAction((parseResult, _) =>
        {
            AnsiConsole.MarkupLine("[yellow]Dubbing is the next milestone.[/] The plan: transcript with word timings ('murch subs --json') → local translation (Ollama) → local TTS (sherpa-onnx) → timing fit → mix with the original track.");
            return Task.FromResult(2);
        });
        return command;
    }
}

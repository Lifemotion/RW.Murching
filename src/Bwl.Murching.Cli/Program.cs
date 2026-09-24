using System.CommandLine;
using Bwl.Murching.Cli.Commands;
using Spectre.Console;

namespace Bwl.Murching.Cli;

internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        Console.OutputEncoding = System.Text.Encoding.UTF8;

        var root = new RootCommand("murch — local subtitles (and soon dubbing) for audio and video files, powered by whisper.cpp.")
        {
            SubsCommand.Create(),
            CuesCommand.Create(),
            TranslateCommand.Create(),
            ProbeCommand.Create(),
            ModelsCommand.Create(),
            SetupCommand.Create(),
            DoctorCommand.Create(),
            DubCommand.Create(),
        };
        root.Options.Add(GlobalOptions.Verbose);
        root.Options.Add(GlobalOptions.Quiet);

        try
        {
            return await root.Parse(args).InvokeAsync().ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            AnsiConsole.MarkupLine("[yellow]Cancelled.[/]");
            return 130;
        }
    }
}

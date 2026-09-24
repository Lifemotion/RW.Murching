using System.CommandLine;
using Bwl.Murching.Media;
using Bwl.Murching.Models;
using Bwl.Murching.Runtime;
using Spectre.Console;

namespace Bwl.Murching.Cli.Commands;

internal static class DoctorCommand
{
    public static Command Create()
    {
        var command = new Command("doctor", "Check the environment: ffmpeg, GPU, CUDA libraries, models.");
        command.SetAction(async (parseResult, ct) =>
        {
            var table = new Table().Border(TableBorder.Rounded).AddColumns("Component", "Status", "Details");

            var ffmpeg = FfmpegTools.TryLocate();
            if (ffmpeg is null)
            {
                table.AddRow("ffmpeg", "[red]missing[/]", "run 'murch setup --ffmpeg'");
            }
            else
            {
                string version;
                try
                {
                    version = await ffmpeg.GetVersionAsync(ct).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    version = "error: " + ex.Message;
                }

                table.AddRow("ffmpeg", "[green]ok[/]", Markup.Escape($"{version}  {ffmpeg.FfmpegPath}"));
            }

            var driver = CudaRuntime.HasNvidiaDriver();
            table.AddRow("NVIDIA driver", driver ? "[green]ok[/]" : "[yellow]not found[/]", driver ? "nvcuda.dll loads" : "CPU inference only");

            var cudaDir = CudaRuntime.FindDirectory();
            table.AddRow("CUDA 13 libraries", cudaDir is null ? (driver ? "[yellow]missing[/]" : "[grey]n/a[/]") : "[green]ok[/]", Markup.Escape(cudaDir ?? "run 'murch setup --cuda'"));

            string runtime;
            try
            {
                runtime = WhisperRuntime.GetRuntimeInfo(ComputeDevice.Auto);
                var loaded = Whisper.net.LibraryLoader.RuntimeOptions.LoadedLibrary;
                table.AddRow("whisper.cpp runtime", "[green]ok[/]", Markup.Escape($"{loaded}: {runtime}"));
            }
            catch (Exception ex)
            {
                table.AddRow("whisper.cpp runtime", "[red]failed[/]", Markup.Escape(ex.Message));
            }

            var store = new ModelStore();
            var downloaded = store.List().Where(s => s.Downloaded).Select(s => s.Spec.Name).ToList();
            table.AddRow("Models", downloaded.Count > 0 ? "[green]ok[/]" : "[yellow]none[/]", Markup.Escape(downloaded.Count > 0 ? string.Join(", ", downloaded) : $"run 'murch setup --models' ({store.Directory})"));

            table.AddRow("Data directory", "[grey]info[/]", Markup.Escape(AppPaths.DataRoot));
            table.AddRow("CPU", "[grey]info[/]", $"{Environment.ProcessorCount} logical processors");

            AnsiConsole.Write(table);
            return 0;
        });
        return command;
    }
}

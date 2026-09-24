using System.CommandLine;
using Bwl.Murching.Common;
using Bwl.Murching.Media;
using Bwl.Murching.Models;
using Bwl.Murching.Pipeline;
using Bwl.Murching.Runtime;
using Spectre.Console;

namespace Bwl.Murching.Cli.Commands;

internal static class SetupCommand
{
    public static Command Create()
    {
        var ffmpeg = new Option<bool>("--ffmpeg") { Description = "Download a portable ffmpeg build." };
        var cuda = new Option<bool>("--cuda") { Description = "Download the CUDA libraries: CUDA 13 set for whisper.cpp (~440 MB) and CUDA 12 + cuDNN 9 set for ONNX Runtime / Qwen3-TTS (~2.7 GB)." };
        var models = new Option<bool>("--models") { Description = $"Download the default models ({ModelCatalog.DefaultWhisperModel}, {ModelCatalog.DefaultVadModel})." };
        var model = new Option<string?>("--model") { Description = "Whisper model to download instead of the default." };

        var command = new Command("setup", "Download the external pieces murch needs: ffmpeg, CUDA libraries, models. With no flags, everything that is missing.")
        {
            ffmpeg, cuda, models, model,
        };

        command.SetAction(async (parseResult, ct) =>
        {
            var doFfmpeg = parseResult.GetValue(ffmpeg);
            var doCuda = parseResult.GetValue(cuda);
            var doModels = parseResult.GetValue(models) || parseResult.GetValue(model) is not null;
            if (!doFfmpeg && !doCuda && !doModels)
            {
                doFfmpeg = doModels = true;
                doCuda = CudaRuntime.HasNvidiaDriver();
            }

            if (doFfmpeg)
            {
                if (FfmpegTools.TryLocate() is { } found)
                {
                    AnsiConsole.MarkupLineInterpolated($"[green]ffmpeg[/] found: {found.FfmpegPath}");
                }
                else
                {
                    await WithBarAsync("Downloading ffmpeg", async report => await FfmpegTools.DownloadAsync(report, ct: ct).ConfigureAwait(false), ct).ConfigureAwait(false);
                    AnsiConsole.MarkupLineInterpolated($"[green]ffmpeg[/] → {FfmpegTools.TryLocate()?.FfmpegPath}");
                }
            }

            if (doCuda)
            {
                if (!CudaRuntime.HasNvidiaDriver())
                {
                    AnsiConsole.MarkupLine("[yellow]No NVIDIA driver detected; skipping CUDA libraries.[/]");
                }
                else
                {
                    if (CudaRuntime.FindDirectory() is { } dir13)
                    {
                        AnsiConsole.MarkupLineInterpolated($"[green]CUDA 13 libraries (whisper)[/] found: {dir13}");
                    }
                    else
                    {
                        await InstallCudaSetAsync((p, token) => CudaSetup.InstallAsync(progress: p, ct: token), "CUDA 13 libraries (whisper)", ct).ConfigureAwait(false);
                    }

                    if (CudaRuntime.FindOnnxDirectory() is { } dir12)
                    {
                        AnsiConsole.MarkupLineInterpolated($"[green]CUDA 12 + cuDNN 9 libraries (Qwen3-TTS)[/] found: {dir12}");
                    }
                    else
                    {
                        await InstallCudaSetAsync((p, token) => CudaSetup.InstallForOnnxRuntimeAsync(progress: p, ct: token), "CUDA 12 + cuDNN 9 libraries (Qwen3-TTS)", ct).ConfigureAwait(false);
                    }
                }
            }

            if (doModels)
            {
                var store = new ModelStore();
                var whisper = ModelCatalog.ResolveWhisper(parseResult.GetValue(model) ?? ModelCatalog.DefaultWhisperModel);
                foreach (var spec in new[] { ModelCatalog.ResolveVad(ModelCatalog.DefaultVadModel), whisper })
                {
                    if (store.IsAvailable(spec))
                    {
                        AnsiConsole.MarkupLineInterpolated($"[green]{spec.Name}[/] found: {store.GetPath(spec)}");
                    }
                    else
                    {
                        await ModelsCommand.DownloadWithBarAsync(spec, store, ct).ConfigureAwait(false);
                    }
                }
            }

            return 0;
        });

        return command;
    }

    private static async Task InstallCudaSetAsync(Func<IProgress<CudaSetup.SetupProgress>, CancellationToken, Task<string>> install, string title, CancellationToken ct)
    {
        await AnsiConsole.Progress()
            .AutoClear(false)
            .Columns(new TaskDescriptionColumn(), new ProgressBarColumn(), new PercentageColumn(), new DownloadedColumn(), new TransferSpeedColumn())
            .StartAsync(async ctx =>
            {
                var tasks = new Dictionary<string, ProgressTask>();
                var progress = new SyncProgress<CudaSetup.SetupProgress>(p =>
                {
                    if (!tasks.TryGetValue(p.Package.Name, out var task))
                    {
                        task = ctx.AddTask(p.Package.Name, maxValue: Math.Max(1, p.Package.ApproxBytes));
                        tasks[p.Package.Name] = task;
                    }

                    if (p.Download.TotalBytes is { } total && total > 0)
                    {
                        task.MaxValue = total;
                    }

                    task.Value = p.Download.BytesReceived;
                });
                var target = await install(progress, ct).ConfigureAwait(false);
                AnsiConsole.MarkupLineInterpolated($"[green]{title}[/] → {target}");
            }).ConfigureAwait(false);
    }

    private static async Task WithBarAsync(string title, Func<IProgress<DownloadProgress>, Task> action, CancellationToken ct)
    {
        await AnsiConsole.Progress()
            .AutoClear(false)
            .Columns(new TaskDescriptionColumn(), new ProgressBarColumn(), new PercentageColumn(), new DownloadedColumn(), new TransferSpeedColumn())
            .StartAsync(async ctx =>
            {
                var task = ctx.AddTask(title, maxValue: 100);
                var progress = new SyncProgress<DownloadProgress>(p =>
                {
                    if (p.TotalBytes is { } total && total > 0)
                    {
                        task.MaxValue = total;
                    }

                    task.Value = p.BytesReceived;
                });
                await action(progress).ConfigureAwait(false);
                task.Value = task.MaxValue;
            }).ConfigureAwait(false);
    }
}

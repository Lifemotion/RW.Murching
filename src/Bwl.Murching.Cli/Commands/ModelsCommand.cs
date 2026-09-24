using System.CommandLine;
using Bwl.Murching.Common;
using Bwl.Murching.Models;
using Spectre.Console;

namespace Bwl.Murching.Cli.Commands;

internal static class ModelsCommand
{
    public static Command Create()
    {
        var command = new Command("models", "List, download or remove Whisper / VAD models.");

        var list = new Command("list", "Show the model catalog and what is downloaded.");
        list.SetAction((_, _) =>
        {
            var store = new ModelStore();
            var table = new Table().Border(TableBorder.Rounded);
            table.AddColumns("Name", "Kind", "Size", "Status", "Description");
            foreach (var status in store.List())
            {
                var size = status.Bytes is { } b ? $"{b / 1e6:0} MB" : $"~{status.Spec.ApproxBytes / 1e6:0} MB";
                table.AddRow(
                    status.Spec.Name == ModelCatalog.DefaultWhisperModel || status.Spec.Name == ModelCatalog.DefaultVadModel ? $"[bold]{status.Spec.Name}[/] [grey](default)[/]" : status.Spec.Name,
                    status.Spec.Kind.ToString().ToLowerInvariant(),
                    size,
                    status.Downloaded ? "[green]downloaded[/]" : "[grey]-[/]",
                    Markup.Escape(status.Spec.Description));
            }

            AnsiConsole.Write(table);
            AnsiConsole.MarkupLineInterpolated($"[grey]Models directory: {store.Directory}[/]");
            return Task.FromResult(0);
        });

        var nameArg = new Argument<string[]>("name") { Description = "Model name(s), e.g. large-v3-turbo, silero-v5.1.2.", Arity = ArgumentArity.OneOrMore };
        var pull = new Command("pull", "Download model(s).") { nameArg };
        pull.SetAction(async (parseResult, ct) =>
        {
            var store = new ModelStore();
            foreach (var name in parseResult.GetValue(nameArg)!)
            {
                var spec = ResolveAny(name);
                if (store.IsAvailable(spec))
                {
                    AnsiConsole.MarkupLineInterpolated($"[green]{spec.Name}[/] already present at {store.GetPath(spec)}");
                    continue;
                }

                await DownloadWithBarAsync(spec, store, ct).ConfigureAwait(false);
            }

            return 0;
        });

        var rm = new Command("rm", "Delete downloaded model(s).") { nameArg };
        rm.SetAction((parseResult, _) =>
        {
            var store = new ModelStore();
            foreach (var name in parseResult.GetValue(nameArg)!)
            {
                var spec = ResolveAny(name);
                if (store.Delete(spec))
                {
                    AnsiConsole.MarkupLineInterpolated($"Deleted {spec.Name}");
                }
                else
                {
                    AnsiConsole.MarkupLineInterpolated($"{spec.Name} was not downloaded");
                }
            }

            return Task.FromResult(0);
        });

        var dir = new Command("dir", "Print the models directory.");
        dir.SetAction((_, _) =>
        {
            Console.WriteLine(new ModelStore().Directory);
            return Task.FromResult(0);
        });

        command.Subcommands.Add(list);
        command.Subcommands.Add(pull);
        command.Subcommands.Add(rm);
        command.Subcommands.Add(dir);
        command.SetAction((parseResult, ct) => list.Parse([]).InvokeAsync(cancellationToken: ct));
        return command;
    }

    internal static ModelSpec ResolveAny(string name)
    {
        try
        {
            return ModelCatalog.ResolveWhisper(name);
        }
        catch (ArgumentException)
        {
            return ModelCatalog.ResolveVad(name);
        }
    }

    internal static async Task<string> DownloadWithBarAsync(ModelSpec spec, ModelStore store, CancellationToken ct)
    {
        string path = string.Empty;
        await AnsiConsole.Progress()
            .AutoClear(false)
            .Columns(new TaskDescriptionColumn(), new ProgressBarColumn(), new PercentageColumn(), new DownloadedColumn(), new TransferSpeedColumn(), new RemainingTimeColumn())
            .StartAsync(async ctx =>
            {
                var task = ctx.AddTask($"Downloading {spec.Name}", maxValue: Math.Max(1, spec.ApproxBytes));
                var progress = new Bwl.Murching.Pipeline.SyncProgress<DownloadProgress>(p =>
                {
                    if (p.TotalBytes is { } total && total > 0)
                    {
                        task.MaxValue = total;
                    }

                    task.Value = p.BytesReceived;
                });
                path = await store.EnsureAsync(spec, progress, ct).ConfigureAwait(false);
                task.Value = task.MaxValue;
            }).ConfigureAwait(false);

        AnsiConsole.MarkupLineInterpolated($"[green]{spec.Name}[/] → {path}");
        return path;
    }
}

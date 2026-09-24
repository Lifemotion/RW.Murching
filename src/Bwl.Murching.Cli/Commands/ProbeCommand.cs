using System.CommandLine;
using Bwl.Murching.Common;
using Bwl.Murching.Media;
using Spectre.Console;

namespace Bwl.Murching.Cli.Commands;

internal static class ProbeCommand
{
    public static Command Create()
    {
        var input = new Argument<FileInfo>("input") { Description = "Media file to inspect." }.AcceptExistingOnly();
        var command = new Command("probe", "Show streams and duration of a media file (ffprobe).") { input };
        command.SetAction(async (parseResult, ct) =>
        {
            var tools = await FfmpegTools.EnsureAsync(ct: ct).ConfigureAwait(false);
            var info = await new MediaProbe(tools).ProbeAsync(parseResult.GetValue(input)!.FullName, ct).ConfigureAwait(false);

            AnsiConsole.MarkupLineInterpolated($"[bold]{Path.GetFileName(info.Path)}[/]  {info.FormatLongName ?? info.Format}  {TimeFormat.Human(info.Duration)}  {(info.SizeBytes ?? 0) / 1e6:0.0} MB  {(info.BitRate ?? 0) / 1000} kb/s");
            var table = new Table().Border(TableBorder.Rounded);
            table.AddColumns("#", "Type", "Codec", "Details", "Lang", "Title", "Default");
            foreach (var s in info.Streams)
            {
                var details = s.Type switch
                {
                    MediaStreamType.Audio => $"{s.SampleRate} Hz, {s.ChannelLayout ?? s.Channels + " ch"}",
                    MediaStreamType.Video => $"{s.Width}x{s.Height}" + (s.FrameRate is { } f ? $", {f:0.###} fps" : string.Empty),
                    _ => string.Empty,
                };
                table.AddRow(s.Index.ToString(), s.Type.ToString().ToLowerInvariant(), s.Codec ?? "?", details, s.Language ?? string.Empty, Markup.Escape(s.Title ?? string.Empty), s.IsDefault ? "yes" : string.Empty);
            }

            AnsiConsole.Write(table);
            return 0;
        });
        return command;
    }
}

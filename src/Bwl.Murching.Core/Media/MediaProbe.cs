using System.Globalization;
using System.Text.Json;
using Bwl.Murching.Common;

namespace Bwl.Murching.Media;

/// <summary>Wraps <c>ffprobe</c> to describe a media file.</summary>
public sealed class MediaProbe(FfmpegTools tools)
{
    public async Task<MediaInfo> ProbeAsync(string path, CancellationToken ct = default)
    {
        if (!File.Exists(path))
        {
            throw new FileNotFoundException("Input file not found.", path);
        }

        var result = await ProcessRunner.RunAsync(
            tools.FfprobePath,
            ["-v", "error", "-print_format", "json", "-show_format", "-show_streams", "-show_error", path],
            ct).ConfigureAwait(false);

        return Parse(path, result.StdOut);
    }

    public static MediaInfo Parse(string path, string ffprobeJson)
    {
        using var doc = JsonDocument.Parse(ffprobeJson);
        var root = doc.RootElement;
        if (root.TryGetProperty("error", out var error))
        {
            throw new InvalidDataException($"ffprobe: {GetString(error, "string") ?? "unknown error"}");
        }

        var format = root.TryGetProperty("format", out var f) ? f : default;
        var streams = new List<MediaStreamInfo>();
        if (root.TryGetProperty("streams", out var arr))
        {
            foreach (var s in arr.EnumerateArray())
            {
                var tags = s.TryGetProperty("tags", out var t) ? t : default;
                var disposition = s.TryGetProperty("disposition", out var d) ? d : default;
                var codec = GetString(s, "codec_name");
                var avgFrameRate = ParseRational(GetString(s, "avg_frame_rate"));
                // Cover art in audio files shows up as a video stream with a single frame.
                var attachedPicture = (disposition.ValueKind == JsonValueKind.Object && GetInt(disposition, "attached_pic") == 1)
                                      || (codec is "mjpeg" or "png" or "bmp" or "gif" && avgFrameRate is null);
                streams.Add(new MediaStreamInfo(
                    Index: GetInt(s, "index") ?? streams.Count,
                    Type: GetString(s, "codec_type") switch
                    {
                        "audio" => MediaStreamType.Audio,
                        "video" => MediaStreamType.Video,
                        "subtitle" => MediaStreamType.Subtitle,
                        "data" => MediaStreamType.Data,
                        "attachment" => MediaStreamType.Attachment,
                        _ => MediaStreamType.Unknown,
                    },
                    Codec: codec,
                    SampleRate: GetInt(s, "sample_rate"),
                    Channels: GetInt(s, "channels"),
                    ChannelLayout: GetString(s, "channel_layout"),
                    Width: GetInt(s, "width"),
                    Height: GetInt(s, "height"),
                    FrameRate: avgFrameRate ?? ParseRational(GetString(s, "r_frame_rate")),
                    Duration: GetDouble(s, "duration") is { } sd ? TimeSpan.FromSeconds(sd) : null,
                    Language: tags.ValueKind == JsonValueKind.Object ? GetString(tags, "language") : null,
                    Title: tags.ValueKind == JsonValueKind.Object ? GetString(tags, "title") : null,
                    IsDefault: disposition.ValueKind == JsonValueKind.Object && GetInt(disposition, "default") == 1,
                    IsAttachedPicture: attachedPicture));
            }
        }

        var duration = format.ValueKind == JsonValueKind.Object && GetDouble(format, "duration") is { } fd
            ? TimeSpan.FromSeconds(fd)
            : streams.Select(s => s.Duration ?? TimeSpan.Zero).DefaultIfEmpty(TimeSpan.Zero).Max();

        return new MediaInfo(
            Path: path,
            Format: format.ValueKind == JsonValueKind.Object ? GetString(format, "format_name") : null,
            FormatLongName: format.ValueKind == JsonValueKind.Object ? GetString(format, "format_long_name") : null,
            Duration: duration,
            SizeBytes: format.ValueKind == JsonValueKind.Object ? GetLong(format, "size") : null,
            BitRate: format.ValueKind == JsonValueKind.Object ? GetLong(format, "bit_rate") : null,
            Streams: streams);
    }

    private static string? GetString(JsonElement el, string name) =>
        el.ValueKind == JsonValueKind.Object && el.TryGetProperty(name, out var v)
            ? v.ValueKind switch
            {
                JsonValueKind.String => v.GetString(),
                JsonValueKind.Number => v.GetRawText(),
                _ => null,
            }
            : null;

    private static int? GetInt(JsonElement el, string name) =>
        GetString(el, name) is { } s && int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var i) ? i : null;

    private static long? GetLong(JsonElement el, string name) =>
        GetString(el, name) is { } s && long.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var l) ? l : null;

    private static double? GetDouble(JsonElement el, string name) =>
        GetString(el, name) is { } s && double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : null;

    /// <summary>Parses ffprobe rationals such as <c>24000/1001</c>; returns null for <c>0/0</c>.</summary>
    internal static double? ParseRational(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return null;
        }

        var slash = text.IndexOf('/', StringComparison.Ordinal);
        if (slash < 0)
        {
            return double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) && v > 0 ? v : null;
        }

        if (double.TryParse(text.AsSpan(0, slash), NumberStyles.Float, CultureInfo.InvariantCulture, out var num) &&
            double.TryParse(text.AsSpan(slash + 1), NumberStyles.Float, CultureInfo.InvariantCulture, out var den) &&
            den > 0 && num > 0)
        {
            return num / den;
        }

        return null;
    }
}

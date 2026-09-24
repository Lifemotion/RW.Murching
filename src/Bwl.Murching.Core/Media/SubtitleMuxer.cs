using Bwl.Murching.Common;

namespace Bwl.Murching.Media;

/// <summary>Embeds a subtitle file into a video container (soft subs) or burns it into the picture.</summary>
public sealed class SubtitleMuxer(FfmpegTools tools)
{
    private static readonly Dictionary<string, string> Iso639Part2 = new(StringComparer.OrdinalIgnoreCase)
    {
        ["en"] = "eng", ["ru"] = "rus", ["uk"] = "ukr", ["de"] = "deu", ["fr"] = "fra", ["es"] = "spa", ["it"] = "ita",
        ["pt"] = "por", ["pl"] = "pol", ["nl"] = "nld", ["sv"] = "swe", ["no"] = "nor", ["da"] = "dan", ["fi"] = "fin",
        ["cs"] = "ces", ["sk"] = "slk", ["hu"] = "hun", ["ro"] = "ron", ["bg"] = "bul", ["el"] = "ell", ["tr"] = "tur",
        ["he"] = "heb", ["ar"] = "ara", ["fa"] = "fas", ["hi"] = "hin", ["ja"] = "jpn", ["ko"] = "kor", ["zh"] = "zho",
        ["vi"] = "vie", ["th"] = "tha", ["id"] = "ind", ["ms"] = "msa", ["et"] = "est", ["lv"] = "lav", ["lt"] = "lit",
        ["ka"] = "kat", ["hy"] = "hye", ["kk"] = "kaz", ["be"] = "bel", ["sr"] = "srp", ["hr"] = "hrv", ["sl"] = "slv",
    };

    public static string ToIso639Part2(string? code) =>
        code is { Length: > 0 } && Iso639Part2.TryGetValue(code, out var three) ? three : code ?? "und";

    /// <summary>Default output name for a muxed file: <c>video.subbed.mkv</c> (mp4 inputs keep mp4).</summary>
    public static string DefaultOutputPath(string videoPath)
    {
        var ext = Path.GetExtension(videoPath).ToLowerInvariant();
        var container = ext is ".mp4" or ".m4v" or ".mov" ? ext : ".mkv";
        return Path.Combine(Path.GetDirectoryName(videoPath) ?? string.Empty, Path.GetFileNameWithoutExtension(videoPath) + ".subbed" + container);
    }

    /// <summary>Adds the subtitle file as a new default subtitle stream; audio and video are copied.</summary>
    public async Task EmbedAsync(string videoPath, string subtitlePath, string? language, string outputPath, CancellationToken ct = default)
    {
        var ext = Path.GetExtension(outputPath).ToLowerInvariant();
        var codec = ext is ".mp4" or ".m4v" or ".mov" ? "mov_text" : "srt";
        var args = new List<string>
        {
            "-hide_banner", "-nostdin", "-loglevel", "error", "-y",
            "-i", videoPath,
            "-i", subtitlePath,
            "-map", "0", "-map", "1",
            "-c", "copy",
            "-c:s", codec,
            "-metadata:s:s:0", $"language={ToIso639Part2(language)}",
            "-disposition:s:0", "default",
        };
        if (ext is ".mp4" or ".m4v" or ".mov")
        {
            args.AddRange(["-movflags", "+faststart"]);
        }

        args.Add(outputPath);
        await ProcessRunner.RunAsync(tools.FfmpegPath, args, ct).ConfigureAwait(false);
    }

    /// <summary>Re-encodes the video with the subtitles rendered into the picture (libx264, audio copied).</summary>
    public async Task BurnInAsync(string videoPath, string subtitlePath, string outputPath, int crf = 20, CancellationToken ct = default)
    {
        var filter = $"subtitles=filename='{EscapeFilterPath(subtitlePath)}'";
        var args = new List<string>
        {
            "-hide_banner", "-nostdin", "-loglevel", "error", "-y",
            "-i", videoPath,
            "-vf", filter,
            "-c:v", "libx264", "-preset", "medium", "-crf", crf.ToString(System.Globalization.CultureInfo.InvariantCulture),
            "-c:a", "copy",
            outputPath,
        };
        await ProcessRunner.RunAsync(tools.FfmpegPath, args, ct).ConfigureAwait(false);
    }

    /// <summary>ffmpeg filter graph escaping: backslashes become forward slashes, colons and quotes are escaped.</summary>
    internal static string EscapeFilterPath(string path) =>
        Path.GetFullPath(path)
            .Replace('\\', '/')
            .Replace(":", "\\:", StringComparison.Ordinal)
            .Replace("'", "\\'", StringComparison.Ordinal);
}

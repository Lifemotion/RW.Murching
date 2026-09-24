using Bwl.Murching.Common;
using Bwl.Murching.Runtime;

namespace Bwl.Murching.Media;

/// <summary>
/// Locates (or downloads) the ffmpeg / ffprobe executables. Search order: <c>MURCH_FFMPEG_DIR</c>, next to the
/// application, <c>%LOCALAPPDATA%\Bwl.Murching\ffmpeg\bin</c>, the repository <c>tools/ffmpeg/bin</c>, then <c>PATH</c>.
/// </summary>
public sealed class FfmpegTools
{
    public const string DefaultDownloadUrl = "https://github.com/BtbN/FFmpeg-Builds/releases/download/latest/ffmpeg-master-latest-win64-gpl.zip";

    private static readonly string Ext = OperatingSystem.IsWindows() ? ".exe" : string.Empty;

    private FfmpegTools(string ffmpegPath, string ffprobePath)
    {
        FfmpegPath = ffmpegPath;
        FfprobePath = ffprobePath;
    }

    public string FfmpegPath { get; }

    public string FfprobePath { get; }

    public string Directory => Path.GetDirectoryName(FfmpegPath)!;

    public static IEnumerable<string> CandidateDirectories()
    {
        if (Environment.GetEnvironmentVariable("MURCH_FFMPEG_DIR") is { Length: > 0 } env)
        {
            yield return env;
            yield return Path.Combine(env, "bin");
        }

        yield return AppContext.BaseDirectory;
        yield return Path.Combine(AppContext.BaseDirectory, "ffmpeg");
        yield return Path.Combine(AppContext.BaseDirectory, "ffmpeg", "bin");
        yield return Path.Combine(AppPaths.FfmpegDir, "bin");
        yield return AppPaths.FfmpegDir;

        if (AppPaths.ToolsDir is { } tools)
        {
            yield return Path.Combine(tools, "ffmpeg", "bin");
            yield return Path.Combine(tools, "ffmpeg");
        }

        var path = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        foreach (var dir in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            yield return dir;
        }
    }

    public static FfmpegTools? TryLocate()
    {
        foreach (var dir in CandidateDirectories())
        {
            var ffmpeg = Path.Combine(dir, "ffmpeg" + Ext);
            var ffprobe = Path.Combine(dir, "ffprobe" + Ext);
            if (File.Exists(ffmpeg) && File.Exists(ffprobe))
            {
                return new FfmpegTools(Path.GetFullPath(ffmpeg), Path.GetFullPath(ffprobe));
            }
        }

        return null;
    }

    public static FfmpegTools Locate() =>
        TryLocate() ?? throw new FileNotFoundException(
            "ffmpeg/ffprobe were not found. Run 'murch setup --ffmpeg' to download a portable build, or install ffmpeg and add it to PATH.");

    /// <summary>Returns the located tools, downloading a portable build into the application data folder when missing.</summary>
    public static async Task<FfmpegTools> EnsureAsync(IProgress<DownloadProgress>? progress = null, CancellationToken ct = default) =>
        TryLocate() ?? await DownloadAsync(progress, ct: ct).ConfigureAwait(false);

    /// <summary>Downloads the BtbN portable Windows build and extracts ffmpeg/ffprobe into <see cref="AppPaths.FfmpegDir"/>.</summary>
    public static async Task<FfmpegTools> DownloadAsync(IProgress<DownloadProgress>? progress = null, string? url = null, CancellationToken ct = default)
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("Automatic ffmpeg download is only implemented for Windows. Install ffmpeg with your package manager.");
        }

        var tmp = AppPaths.EnsureDirectory(AppPaths.TempDir);
        var zip = Path.Combine(tmp, "ffmpeg.zip");
        await Downloader.Default.DownloadFileAsync(new Uri(url ?? DefaultDownloadUrl), zip, progress, ct: ct).ConfigureAwait(false);
        var binDir = Path.Combine(AppPaths.FfmpegDir, "bin");
        ZipExtractor.ExtractFlat(zip, binDir, name => name.Contains("/bin/", StringComparison.Ordinal) && name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase));
        File.Delete(zip);
        return TryLocate() ?? throw new InvalidOperationException($"ffmpeg was downloaded to {binDir} but could not be located afterwards.");
    }

    /// <summary>First line of <c>ffmpeg -version</c>.</summary>
    public async Task<string> GetVersionAsync(CancellationToken ct = default)
    {
        var result = await ProcessRunner.RunAsync(FfmpegPath, ["-version"], ct).ConfigureAwait(false);
        var firstLine = result.StdOut.Split('\n', 2)[0].Trim();
        return firstLine.StartsWith("ffmpeg version ", StringComparison.Ordinal) ? firstLine["ffmpeg version ".Length..].Split(' ')[0] : firstLine;
    }
}

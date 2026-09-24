namespace Bwl.Murching.Runtime;

/// <summary>
/// Well-known directories used by Murching: downloaded models, CUDA libraries, ffmpeg, caches.
/// Everything lives under <c>%LOCALAPPDATA%\Bwl.Murching</c> unless <c>MURCH_HOME</c> overrides it.
/// In a development checkout the repository <c>tools/</c> folder is also consulted.
/// </summary>
public static class AppPaths
{
    public const string AppFolderName = "Bwl.Murching";

    public static string DataRoot =>
        Environment.GetEnvironmentVariable("MURCH_HOME") is { Length: > 0 } home
            ? Path.GetFullPath(home)
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), AppFolderName);

    public static string ModelsDir => Path.Combine(DataRoot, "models");
    public static string CudaDir => Path.Combine(DataRoot, "cuda");
    public static string FfmpegDir => Path.Combine(DataRoot, "ffmpeg");
    public static string CacheDir => Path.Combine(DataRoot, "cache");
    public static string TempDir => Path.Combine(DataRoot, "tmp");

    /// <summary>Repository root when running from a source checkout (bin/Debug/...), otherwise null.</summary>
    public static string? RepoRoot { get; } = FindRepoRoot(AppContext.BaseDirectory);

    /// <summary>Repository <c>tools/</c> folder (dev mode only).</summary>
    public static string? ToolsDir => RepoRoot is null ? null : Path.Combine(RepoRoot, "tools");

    public static string EnsureDirectory(string path)
    {
        Directory.CreateDirectory(path);
        return path;
    }

    internal static string? FindRepoRoot(string start)
    {
        var dir = new DirectoryInfo(start);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "Bwl.Murching.slnx")) ||
                File.Exists(Path.Combine(dir.FullName, "Bwl.Murching.sln")) ||
                Directory.Exists(Path.Combine(dir.FullName, ".git")))
            {
                return dir.FullName;
            }

            dir = dir.Parent;
        }

        return null;
    }
}

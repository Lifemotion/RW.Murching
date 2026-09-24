using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;

namespace Bwl.Murching.Runtime;

/// <summary>
/// Makes the NVIDIA CUDA 13 user-mode libraries discoverable for whisper.cpp.
/// <para>
/// The <c>Whisper.net.Runtime.Cuda</c> package ships <c>ggml-cuda-whisper.dll</c> with the CUDA runtime statically
/// linked, but it still needs <c>cublas64_13.dll</c> / <c>cublasLt64_13.dll</c>, and the Whisper.net loader probes for
/// <c>cudart64_13.dll</c> <b>by name</b> before it even tries the CUDA build. None of these ship in the NuGet package,
/// so we keep them in a private folder and add it to the DLL search path at start-up.
/// </para>
/// </summary>
public static partial class CudaRuntime
{
    public static readonly string[] RequiredLibraries = ["cudart64_13.dll", "cublas64_13.dll", "cublasLt64_13.dll"];

    private static readonly Lock Gate = new();
    private static bool _prepared;
    private static string? _directory;

    /// <summary>Directory that <see cref="Prepare"/> registered, or null when CUDA libraries were not found.</summary>
    public static string? ResolvedDirectory => _directory;

    public static IEnumerable<string> CandidateDirectories()
    {
        if (Environment.GetEnvironmentVariable("MURCH_CUDA_DIR") is { Length: > 0 } env)
        {
            yield return env;
        }

        yield return Path.Combine(AppContext.BaseDirectory, "cuda");
        yield return AppContext.BaseDirectory;
        yield return AppPaths.CudaDir;

        if (AppPaths.ToolsDir is { } tools)
        {
            yield return Path.Combine(tools, "cuda");
        }

        if (Environment.GetEnvironmentVariable("CUDA_PATH") is { Length: > 0 } cudaPath)
        {
            yield return Path.Combine(cudaPath, "bin", "x64");
            yield return Path.Combine(cudaPath, "bin");
        }
    }

    /// <summary>First candidate directory that contains every required library.</summary>
    public static string? FindDirectory() =>
        CandidateDirectories().FirstOrDefault(dir => RequiredLibraries.All(lib => File.Exists(Path.Combine(dir, lib))));

    /// <summary>True when an NVIDIA driver is installed (nvcuda.dll loads).</summary>
    public static bool HasNvidiaDriver()
    {
        if (!OperatingSystem.IsWindows())
        {
            return false;
        }

        if (NativeLibrary.TryLoad("nvcuda.dll", out var handle))
        {
            NativeLibrary.Free(handle);
            return true;
        }

        return false;
    }

    /// <summary>True when both the driver and the CUDA 13 libraries are present.</summary>
    public static bool IsAvailable => HasNvidiaDriver() && FindDirectory() is not null;

    /// <summary>
    /// Registers the CUDA library folder for the current process. Must run before the first Whisper.net call.
    /// Returns true when CUDA libraries were found.
    /// </summary>
    public static bool Prepare(ILogger? logger = null)
    {
        lock (Gate)
        {
            if (_prepared)
            {
                return _directory is not null;
            }

            _prepared = true;
            if (!OperatingSystem.IsWindows())
            {
                return false;
            }

            _directory = FindDirectory();
            if (_directory is null)
            {
                logger?.LogDebug("CUDA libraries ({Libs}) not found in: {Dirs}", string.Join(", ", RequiredLibraries), string.Join("; ", CandidateDirectories()));
                return false;
            }

            _directory = Path.GetFullPath(_directory);
            var path = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
            if (!path.Split(Path.PathSeparator).Contains(_directory, StringComparer.OrdinalIgnoreCase))
            {
                Environment.SetEnvironmentVariable("PATH", _directory + Path.PathSeparator + path);
            }

            try
            {
                _ = AddDllDirectory(_directory);
            }
            catch (Exception ex)
            {
                logger?.LogDebug(ex, "AddDllDirectory failed");
            }

            logger?.LogDebug("CUDA libraries directory: {Dir}", _directory);
            return true;
        }
    }

    [LibraryImport("kernel32", EntryPoint = "AddDllDirectory", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    private static partial IntPtr AddDllDirectory(string path);
}

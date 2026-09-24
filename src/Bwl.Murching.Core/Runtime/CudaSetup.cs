using Bwl.Murching.Common;

namespace Bwl.Murching.Runtime;

/// <summary>
/// Downloads the redistributable CUDA 13 libraries that whisper.cpp needs (cudart + cuBLAS) from NVIDIA's
/// redist server and drops the DLLs into <see cref="AppPaths.CudaDir"/>. About 440 MB on disk.
/// </summary>
public static class CudaSetup
{
    public sealed record RedistPackage(string Name, Uri Url, string Sha256, long ApproxBytes);

    private const string RedistBase = "https://developer.download.nvidia.com/compute/cuda/redist/";

    public static readonly RedistPackage[] Packages =
    [
        new(
            "cuda_cudart 13.1.80",
            new Uri(RedistBase + "cuda_cudart/windows-x86_64/cuda_cudart-windows-x86_64-13.1.80-archive.zip"),
            "e6f76dba2be1850a08168e90d868b12368aacb6c7d71871efb019f2d9648e877",
            3_000_000),
        new(
            "libcublas 13.2.2.2",
            new Uri(RedistBase + "libcublas/windows-x86_64/libcublas-windows-x86_64-13.2.2.2-archive.zip"),
            "98db841fedf0ce2f723ec559fdf78bf3704d2691c5f427d2f112d223b28d3782",
            384_700_000),
    ];

    public sealed record SetupProgress(RedistPackage Package, int PackageIndex, int PackageCount, DownloadProgress Download);

    /// <summary>Downloads and extracts all packages. Returns the target directory.</summary>
    public static async Task<string> InstallAsync(string? targetDir = null, IProgress<SetupProgress>? progress = null, CancellationToken ct = default)
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("CUDA setup is only implemented for Windows x64.");
        }

        targetDir = AppPaths.EnsureDirectory(targetDir ?? AppPaths.CudaDir);
        var tmp = AppPaths.EnsureDirectory(AppPaths.TempDir);
        for (var i = 0; i < Packages.Length; i++)
        {
            var package = Packages[i];
            var zip = Path.Combine(tmp, Path.GetFileName(package.Url.LocalPath));
            var relay = progress is null ? null : new Progress<DownloadProgress>(p => progress.Report(new SetupProgress(package, i, Packages.Length, p)));
            await Downloader.Default.DownloadFileAsync(package.Url, zip, relay, package.Sha256, ct).ConfigureAwait(false);
            ZipExtractor.ExtractFlat(zip, targetDir, name => name.Contains("/bin/", StringComparison.Ordinal) && name.EndsWith(".dll", StringComparison.OrdinalIgnoreCase));
            File.Delete(zip);
        }

        var missing = CudaRuntime.RequiredLibraries.Where(lib => !File.Exists(Path.Combine(targetDir, lib))).ToList();
        if (missing.Count > 0)
        {
            throw new InvalidOperationException($"CUDA setup finished but these libraries are still missing in {targetDir}: {string.Join(", ", missing)}");
        }

        return targetDir;
    }
}

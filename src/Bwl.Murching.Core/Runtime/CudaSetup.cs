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

    /// <summary>CUDA 12.9 + cuDNN 9 user-mode libraries for the ONNX Runtime CUDA execution provider (Qwen3-TTS). ~2.7 GB download.</summary>
    public static readonly RedistPackage[] OnnxRuntimePackages =
    [
        new("cuda_cudart 12.9.79", new Uri(RedistBase + "cuda_cudart/windows-x86_64/cuda_cudart-windows-x86_64-12.9.79-archive.zip"), "179e9c43b0735ffe67207b3da556eb5a0c50f3047961882b7657d3b822d34ef8", 3_500_000),
        new("libcublas 12.9.2.10", new Uri(RedistBase + "libcublas/windows-x86_64/libcublas-windows-x86_64-12.9.2.10-archive.zip"), "62782c354e1536118caab8fe5fd295ac5fd9076e4814aba0bdaf032fe2b833ae", 549_700_000),
        new("libcufft 11.4.1.4", new Uri(RedistBase + "libcufft/windows-x86_64/libcufft-windows-x86_64-11.4.1.4-archive.zip"), "f26f80bb9abff3269c548e1559e8c2b4ba58ccb8acc6095bbc6404fc962d4b80", 198_400_000),
        new("libcurand 10.3.10.19", new Uri(RedistBase + "libcurand/windows-x86_64/libcurand-windows-x86_64-10.3.10.19-archive.zip"), "d0411f0b8c07e90d0fb6e01bfa7a54c9cb80f2ddf67e4ded2d96a50e19aadad6", 67_900_000),
        new("cudnn 9.26.0 (cuda12)", new Uri("https://developer.download.nvidia.com/compute/cudnn/redist/cudnn/windows-x86_64/cudnn-windows-x86_64-9.26.0.51_cuda12-archive.zip"), "b9bbe801c03faf30c0fcc60c25b2a21ace9e3c0db3b025900421101fca3cc95d", 1_901_600_000),
    ];

    public sealed record SetupProgress(RedistPackage Package, int PackageIndex, int PackageCount, DownloadProgress Download);

    /// <summary>Downloads and extracts the CUDA 13 set for whisper.cpp. Returns the target directory.</summary>
    public static Task<string> InstallAsync(string? targetDir = null, IProgress<SetupProgress>? progress = null, CancellationToken ct = default) =>
        InstallAsync(Packages, targetDir ?? AppPaths.CudaDir, CudaRuntime.RequiredLibraries, progress, ct);

    /// <summary>Downloads and extracts the CUDA 12 + cuDNN 9 set for ONNX Runtime. Returns the target directory.</summary>
    public static Task<string> InstallForOnnxRuntimeAsync(string? targetDir = null, IProgress<SetupProgress>? progress = null, CancellationToken ct = default) =>
        InstallAsync(OnnxRuntimePackages, targetDir ?? Path.Combine(AppPaths.DataRoot, "cuda12"), CudaRuntime.RequiredOnnxLibraries, progress, ct);

    private static async Task<string> InstallAsync(RedistPackage[] packages, string targetDir, string[] required, IProgress<SetupProgress>? progress, CancellationToken ct)
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("CUDA setup is only implemented for Windows x64.");
        }

        targetDir = AppPaths.EnsureDirectory(targetDir);
        var tmp = AppPaths.EnsureDirectory(AppPaths.TempDir);
        for (var i = 0; i < packages.Length; i++)
        {
            var package = packages[i];
            var zip = Path.Combine(tmp, Path.GetFileName(package.Url.LocalPath));
            var relay = progress is null ? null : new Progress<DownloadProgress>(p => progress.Report(new SetupProgress(package, i, packages.Length, p)));
            await Downloader.Default.DownloadFileAsync(package.Url, zip, relay, package.Sha256, ct).ConfigureAwait(false);
            ZipExtractor.ExtractFlat(zip, targetDir, name => name.Contains("/bin/", StringComparison.Ordinal) && name.EndsWith(".dll", StringComparison.OrdinalIgnoreCase));
            File.Delete(zip);
        }

        var missing = required.Where(lib => !File.Exists(Path.Combine(targetDir, lib))).ToList();
        if (missing.Count > 0)
        {
            throw new InvalidOperationException($"CUDA setup finished but these libraries are still missing in {targetDir}: {string.Join(", ", missing)}");
        }

        return targetDir;
    }
}

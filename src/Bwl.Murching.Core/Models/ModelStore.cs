using Bwl.Murching.Common;
using Bwl.Murching.Runtime;

namespace Bwl.Murching.Models;

public sealed record ModelStatus(ModelSpec Spec, string Path, bool Downloaded, long? Bytes);

/// <summary>Keeps downloaded models in <see cref="AppPaths.ModelsDir"/> and fetches missing ones on demand.</summary>
public sealed class ModelStore(string? directory = null, Downloader? downloader = null)
{
    private readonly Downloader _downloader = downloader ?? Downloader.Default;

    public string Directory { get; } = directory ?? AppPaths.ModelsDir;

    public string GetPath(ModelSpec spec) => spec.LocalPath ?? Path.Combine(Directory, spec.FileName);

    public bool IsAvailable(ModelSpec spec) => File.Exists(GetPath(spec));

    public ModelStatus GetStatus(ModelSpec spec)
    {
        var path = GetPath(spec);
        var exists = File.Exists(path);
        return new ModelStatus(spec, path, exists, exists ? new FileInfo(path).Length : null);
    }

    public IEnumerable<ModelStatus> List(ModelKind? kind = null)
    {
        var all = ModelCatalog.Whisper.Concat(ModelCatalog.Vad);
        if (kind is { } k)
        {
            all = all.Where(m => m.Kind == k);
        }

        return all.Select(GetStatus);
    }

    /// <summary>Returns the local path of the model, downloading it first when needed.</summary>
    public async Task<string> EnsureAsync(ModelSpec spec, IProgress<DownloadProgress>? progress = null, CancellationToken ct = default)
    {
        var path = GetPath(spec);
        if (File.Exists(path))
        {
            return path;
        }

        if (spec.Url is null)
        {
            throw new FileNotFoundException($"Model file not found: {path}", path);
        }

        AppPaths.EnsureDirectory(Path.GetDirectoryName(path)!);
        await _downloader.DownloadFileAsync(spec.Url, path, progress, ct: ct).ConfigureAwait(false);
        return path;
    }

    public bool Delete(ModelSpec spec)
    {
        var path = GetPath(spec);
        if (spec.IsLocal || !File.Exists(path))
        {
            return false;
        }

        File.Delete(path);
        return true;
    }
}

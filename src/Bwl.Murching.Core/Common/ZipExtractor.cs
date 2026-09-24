using System.IO.Compression;

namespace Bwl.Murching.Common;

public static class ZipExtractor
{
    /// <summary>Extracts entries matching <paramref name="filter"/> into <paramref name="targetDir"/> flat (directory structure dropped).</summary>
    public static IReadOnlyList<string> ExtractFlat(string zipPath, string targetDir, Func<string, bool> filter)
    {
        Directory.CreateDirectory(targetDir);
        var extracted = new List<string>();
        using var archive = ZipFile.OpenRead(zipPath);
        foreach (var entry in archive.Entries)
        {
            if (entry.Length == 0 || string.IsNullOrEmpty(entry.Name) || !filter(entry.FullName))
            {
                continue;
            }

            var target = Path.Combine(targetDir, entry.Name);
            entry.ExtractToFile(target, overwrite: true);
            extracted.Add(target);
        }

        return extracted;
    }
}

using Bwl.Murching.Models;
using Whisper.net;

namespace Bwl.Murching.Tests.Models;

public class ModelCatalogTests
{
    [Theory]
    [InlineData("large-v3-turbo", "large-v3-turbo")]
    [InlineData("ggml-large-v3-turbo.bin", "large-v3-turbo")]
    [InlineData("LARGE-V3-TURBO", "large-v3-turbo")]
    [InlineData("tiny.en", "tiny.en")]
    public void ResolveWhisper_accepts_names_and_file_names(string input, string expected)
    {
        var spec = ModelCatalog.ResolveWhisper(input);
        Assert.Equal(expected, spec.Name);
        Assert.Equal(ModelKind.Whisper, spec.Kind);
        Assert.NotNull(spec.Url);
        Assert.False(spec.IsLocal);
    }

    [Fact]
    public void ResolveWhisper_local_file_guesses_preset()
    {
        var path = Path.Combine(Path.GetTempPath(), $"ggml-medium-{Guid.NewGuid():N}.bin");
        File.WriteAllBytes(path, [1, 2, 3]);
        try
        {
            var spec = ModelCatalog.ResolveWhisper(path);
            Assert.True(spec.IsLocal);
            Assert.Equal(path, spec.LocalPath);
            Assert.Equal(WhisperAlignmentHeadsPreset.Medium, spec.HeadsPreset);
            Assert.Equal(3, spec.ApproxBytes);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void ResolveWhisper_unknown_throws_with_list()
    {
        var ex = Assert.Throws<ArgumentException>(() => ModelCatalog.ResolveWhisper("gigantic-v9"));
        Assert.Contains("large-v3-turbo", ex.Message);
    }

    [Fact]
    public void ResolveVad_default_exists()
    {
        var spec = ModelCatalog.ResolveVad(ModelCatalog.DefaultVadModel);
        Assert.Equal(ModelKind.Vad, spec.Kind);
        Assert.EndsWith("ggml-silero-v5.1.2.bin", spec.Url!.ToString());
    }

    [Theory]
    [InlineData("ggml-large-v3-turbo-q8_0", WhisperAlignmentHeadsPreset.LargeV3Turbo)]
    [InlineData("large-v3", WhisperAlignmentHeadsPreset.LargeV3)]
    [InlineData("small.en-q5_1", WhisperAlignmentHeadsPreset.SmallEn)]
    [InlineData("custom", WhisperAlignmentHeadsPreset.None)]
    public void GuessPreset_matches_by_substring(string stem, WhisperAlignmentHeadsPreset expected) =>
        Assert.Equal(expected, ModelCatalog.GuessPreset(stem));

    [Fact]
    public void Catalog_names_are_unique_and_files_follow_convention()
    {
        var all = ModelCatalog.Whisper.Concat(ModelCatalog.Vad).ToList();
        Assert.Equal(all.Count, all.Select(m => m.Name).Distinct().Count());
        Assert.All(all, m => Assert.Equal($"ggml-{m.Name}.bin", m.FileName));
    }

    [Fact]
    public void ModelStore_paths_and_status()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"murch-models-{Guid.NewGuid():N}");
        var store = new ModelStore(dir);
        var spec = ModelCatalog.ResolveWhisper("tiny");
        Assert.False(store.IsAvailable(spec));
        Directory.CreateDirectory(dir);
        File.WriteAllText(store.GetPath(spec), "x");
        try
        {
            Assert.True(store.IsAvailable(spec));
            var status = store.GetStatus(spec);
            Assert.True(status.Downloaded);
            Assert.Equal(1, status.Bytes);
            Assert.True(store.Delete(spec));
            Assert.False(store.IsAvailable(spec));
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }
}

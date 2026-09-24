using Whisper.net;

namespace Bwl.Murching.Models;

/// <summary>Known Whisper (ggml) and Silero VAD models hosted on Hugging Face by the whisper.cpp project.</summary>
public static class ModelCatalog
{
    public const string DefaultWhisperModel = "large-v3-turbo";
    public const string DefaultVadModel = "silero-v5.1.2";

    private const string WhisperBase = "https://huggingface.co/ggerganov/whisper.cpp/resolve/main/";
    private const string VadBase = "https://huggingface.co/ggml-org/whisper-vad/resolve/main/";

    private const long MB = 1_000_000;

    public static readonly IReadOnlyList<ModelSpec> Whisper =
    [
        W("tiny", 78 * MB, "Fastest, lowest quality. Multilingual.", WhisperAlignmentHeadsPreset.Tiny),
        W("tiny.en", 78 * MB, "Fastest, English only.", WhisperAlignmentHeadsPreset.TinyEn, en: true),
        W("base", 148 * MB, "Fast, low quality. Multilingual.", WhisperAlignmentHeadsPreset.Base),
        W("base.en", 148 * MB, "Fast, English only.", WhisperAlignmentHeadsPreset.BaseEn, en: true),
        W("small", 488 * MB, "Reasonable quality on CPU. Multilingual.", WhisperAlignmentHeadsPreset.Small),
        W("small.en", 488 * MB, "Reasonable quality on CPU, English only.", WhisperAlignmentHeadsPreset.SmallEn, en: true),
        W("medium", 1534 * MB, "Good quality. Multilingual.", WhisperAlignmentHeadsPreset.Medium),
        W("medium.en", 1534 * MB, "Good quality, English only.", WhisperAlignmentHeadsPreset.MediumEn, en: true),
        W("large-v2", 3095 * MB, "Best quality of the v2 generation.", WhisperAlignmentHeadsPreset.LargeV2),
        W("large-v3", 3095 * MB, "Best quality overall, slowest.", WhisperAlignmentHeadsPreset.LargeV3),
        W("large-v3-turbo", 1625 * MB, "Near large-v3 quality at a fraction of the cost. Recommended default.", WhisperAlignmentHeadsPreset.LargeV3Turbo),
        W("large-v3-turbo-q8_0", 874 * MB, "large-v3-turbo, 8-bit quantized (almost lossless).", WhisperAlignmentHeadsPreset.LargeV3Turbo),
        W("large-v3-turbo-q5_0", 574 * MB, "large-v3-turbo, 5-bit quantized (small quality loss).", WhisperAlignmentHeadsPreset.LargeV3Turbo),
        W("large-v3-q5_0", 1081 * MB, "large-v3, 5-bit quantized.", WhisperAlignmentHeadsPreset.LargeV3),
        W("medium-q5_0", 539 * MB, "medium, 5-bit quantized.", WhisperAlignmentHeadsPreset.Medium),
        W("small-q5_1", 190 * MB, "small, 5-bit quantized.", WhisperAlignmentHeadsPreset.Small),
        W("base-q5_1", 60 * MB, "base, 5-bit quantized.", WhisperAlignmentHeadsPreset.Base),
        W("tiny-q5_1", 32 * MB, "tiny, 5-bit quantized.", WhisperAlignmentHeadsPreset.Tiny),
    ];

    public static readonly IReadOnlyList<ModelSpec> Vad =
    [
        V("silero-v5.1.2", 885_000, "Silero VAD v5.1.2 (ggml). Default."),
        V("silero-v6.2.0", 885_000, "Silero VAD v6.2.0 (ggml)."),
    ];

    /// <summary>Resolves a catalog name (<c>large-v3-turbo</c>, <c>ggml-large-v3-turbo.bin</c>) or a path to a local ggml file.</summary>
    public static ModelSpec ResolveWhisper(string nameOrPath) => Resolve(nameOrPath, Whisper, ModelKind.Whisper);

    public static ModelSpec ResolveVad(string nameOrPath) => Resolve(nameOrPath, Vad, ModelKind.Vad);

    public static ModelSpec? TryFindWhisper(string name) => TryFind(name, Whisper);

    private static ModelSpec Resolve(string nameOrPath, IReadOnlyList<ModelSpec> catalog, ModelKind kind)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(nameOrPath);
        if (TryFind(nameOrPath, catalog) is { } found)
        {
            return found;
        }

        if (File.Exists(nameOrPath))
        {
            var full = Path.GetFullPath(nameOrPath);
            var stem = NormalizeName(Path.GetFileName(full));
            var template = TryFind(stem, catalog) ?? catalog.FirstOrDefault(m => stem.Contains(m.Name, StringComparison.OrdinalIgnoreCase));
            return new ModelSpec
            {
                Name = stem,
                Kind = kind,
                FileName = Path.GetFileName(full),
                LocalPath = full,
                ApproxBytes = new FileInfo(full).Length,
                Description = "Local file",
                HeadsPreset = template?.HeadsPreset ?? GuessPreset(stem),
                EnglishOnly = template?.EnglishOnly ?? stem.Contains(".en", StringComparison.OrdinalIgnoreCase),
            };
        }

        var known = string.Join(", ", catalog.Select(m => m.Name));
        throw new ArgumentException($"Unknown {kind} model '{nameOrPath}'. Use one of: {known}, or a path to a ggml .bin file.", nameof(nameOrPath));
    }

    private static ModelSpec? TryFind(string name, IReadOnlyList<ModelSpec> catalog)
    {
        var normalized = NormalizeName(name);
        return catalog.FirstOrDefault(m => m.Name.Equals(normalized, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Strips the <c>ggml-</c> prefix and <c>.bin</c> suffix.</summary>
    internal static string NormalizeName(string name)
    {
        var n = name.Trim();
        if (n.StartsWith("ggml-", StringComparison.OrdinalIgnoreCase))
        {
            n = n[5..];
        }

        if (n.EndsWith(".bin", StringComparison.OrdinalIgnoreCase))
        {
            n = n[..^4];
        }

        return n;
    }

    internal static WhisperAlignmentHeadsPreset GuessPreset(string stem)
    {
        var s = stem.ToLowerInvariant();
        return s switch
        {
            _ when s.Contains("large-v3-turbo") => WhisperAlignmentHeadsPreset.LargeV3Turbo,
            _ when s.Contains("large-v3") => WhisperAlignmentHeadsPreset.LargeV3,
            _ when s.Contains("large-v2") => WhisperAlignmentHeadsPreset.LargeV2,
            _ when s.Contains("large-v1") => WhisperAlignmentHeadsPreset.LargeV1,
            _ when s.Contains("medium.en") => WhisperAlignmentHeadsPreset.MediumEn,
            _ when s.Contains("medium") => WhisperAlignmentHeadsPreset.Medium,
            _ when s.Contains("small.en") => WhisperAlignmentHeadsPreset.SmallEn,
            _ when s.Contains("small") => WhisperAlignmentHeadsPreset.Small,
            _ when s.Contains("base.en") => WhisperAlignmentHeadsPreset.BaseEn,
            _ when s.Contains("base") => WhisperAlignmentHeadsPreset.Base,
            _ when s.Contains("tiny.en") => WhisperAlignmentHeadsPreset.TinyEn,
            _ when s.Contains("tiny") => WhisperAlignmentHeadsPreset.Tiny,
            _ => WhisperAlignmentHeadsPreset.None,
        };
    }

    private static ModelSpec W(string name, long bytes, string description, WhisperAlignmentHeadsPreset preset, bool en = false) => new()
    {
        Name = name,
        Kind = ModelKind.Whisper,
        FileName = $"ggml-{name}.bin",
        Url = new Uri(WhisperBase + $"ggml-{name}.bin"),
        ApproxBytes = bytes,
        Description = description,
        HeadsPreset = preset,
        EnglishOnly = en,
    };

    private static ModelSpec V(string name, long bytes, string description) => new()
    {
        Name = name,
        Kind = ModelKind.Vad,
        FileName = $"ggml-{name}.bin",
        Url = new Uri(VadBase + $"ggml-{name}.bin"),
        ApproxBytes = bytes,
        Description = description,
    };
}

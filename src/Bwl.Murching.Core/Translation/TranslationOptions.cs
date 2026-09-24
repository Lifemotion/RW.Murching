namespace Bwl.Murching.Translation;

/// <summary>Settings for translating subtitles with a local LLM served by Ollama.</summary>
public sealed record TranslationOptions
{
    /// <summary>Target language, ISO 639-1 (<c>ru</c>, <c>en</c>, ...).</summary>
    public required string TargetLanguage { get; init; }

    /// <summary>Source language when known; the model copes without it.</summary>
    public string? SourceLanguage { get; init; }

    public Uri Endpoint { get; init; } = new("http://localhost:11434");

    /// <summary>Ollama model tag. Qwen 3.5 9B translates well into Russian and fits a 12 GB card next to nothing else.</summary>
    public string Model { get; init; } = DefaultModel;

    public const string DefaultModel = "qwen3.5:9b";

    /// <summary>Cues translated in one request; the model sees them together for coherence.</summary>
    public int BatchSize { get; init; } = 24;

    /// <summary>Already translated lines shown before the batch as context.</summary>
    public int ContextLines { get; init; } = 6;

    public float Temperature { get; init; } = 0.15f;

    public int ContextWindow { get; init; } = 8192;

    /// <summary>Free-form hints: names, terminology, register ("уличный рэп", "научная лекция").</summary>
    public string? Glossary { get; init; }

    /// <summary>Timeout for one request.</summary>
    public TimeSpan RequestTimeout { get; init; } = TimeSpan.FromMinutes(5);
}

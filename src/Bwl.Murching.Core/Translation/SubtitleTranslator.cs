using Bwl.Murching.Subtitles;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Bwl.Murching.Translation;

public sealed record TranslationProgress(int Done, int Total, string? LastLine)
{
    public double Fraction => Total == 0 ? 1 : (double)Done / Total;
}

/// <summary>Translates a subtitle document cue by cue (timings kept), in batches with rolling context, and re-wraps the lines.</summary>
public static class SubtitleTranslator
{
    public static async Task<SubtitleDocument> TranslateAsync(
        SubtitleDocument document,
        ITranslator translator,
        string targetLanguage,
        CueBuilderOptions? layout = null,
        int batchSize = 24,
        int contextLines = 6,
        IProgress<TranslationProgress>? progress = null,
        ILogger? logger = null,
        CancellationToken ct = default)
    {
        logger ??= NullLogger.Instance;
        layout ??= new CueBuilderOptions();
        var sources = document.Cues.Select(c => string.Join(' ', c.Lines)).ToList();
        var translated = new List<string>(sources.Count);

        for (var offset = 0; offset < sources.Count; offset += batchSize)
        {
            ct.ThrowIfCancellationRequested();
            var batch = sources.Skip(offset).Take(batchSize).ToList();
            var context = translated.TakeLast(contextLines).ToList();
            var result = await translator.TranslateBatchAsync(batch, context, ct).ConfigureAwait(false);
            if (result.Count != batch.Count)
            {
                throw new InvalidOperationException($"Translator returned {result.Count} lines for a batch of {batch.Count}.");
            }

            for (var i = 0; i < batch.Count; i++)
            {
                var line = string.IsNullOrWhiteSpace(result[i]) ? batch[i] : result[i].Trim();
                translated.Add(line);
                logger.LogDebug("{Source}  →  {Target}", batch[i], line);
            }

            progress?.Report(new TranslationProgress(translated.Count, sources.Count, translated[^1]));
        }

        var cues = new List<SubtitleCue>(document.Cues.Count);
        for (var i = 0; i < document.Cues.Count; i++)
        {
            var cue = document.Cues[i];
            var words = translated[i].Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            var lines = words.Length == 0 ? cue.Lines : LineWrapper.Wrap(words, layout.MaxLineLength, layout.MaxLines, layout.PreferShorterTopLine);
            cues.Add(cue with { Lines = lines });
        }

        return new SubtitleDocument(cues) { Language = targetLanguage, Title = document.Title };
    }

    /// <summary>Original lines followed by the translated lines in each cue (the classic two-language learner format).</summary>
    public static SubtitleDocument Bilingual(SubtitleDocument original, SubtitleDocument translated)
    {
        if (original.Cues.Count != translated.Cues.Count)
        {
            throw new ArgumentException("Documents must have the same number of cues.");
        }

        var cues = original.Cues.Zip(translated.Cues, (o, t) => o with { Lines = [.. o.Lines, .. t.Lines] }).ToList();
        return new SubtitleDocument(cues) { Language = $"{original.Language}-{translated.Language}", Title = original.Title };
    }
}

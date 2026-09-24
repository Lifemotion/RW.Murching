using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;

namespace Bwl.Murching.Asr;

public sealed record HallucinationFilterOptions
{
    public bool Enabled { get; init; } = true;

    /// <summary>Segments whose average token probability is below this are dropped.</summary>
    public float MinProbability { get; init; } = 0.35f;

    /// <summary>Segments whose weakest word is below this are dropped (0 = off).</summary>
    public float MinWordProbability { get; init; }

    /// <summary>Segments with fewer words than this are dropped (1 = off).</summary>
    public int MinWords { get; init; } = 1;

    /// <summary>Segments whose no-speech probability exceeds this and whose confidence is low are dropped.</summary>
    public float NoSpeechThreshold { get; init; } = 0.8f;

    public float LowConfidence { get; init; } = 0.45f;

    /// <summary>The same normalised text appearing in this many consecutive segments is treated as a loop and removed entirely.</summary>
    public int RepeatRunLength { get; init; } = 3;

    /// <summary>Extra phrases (case-insensitive substrings) that indicate a hallucinated credit line.</summary>
    public IReadOnlyList<string> ExtraPhrases { get; init; } = [];

    /// <summary>Whole-segment texts (normalised) that are dropped outright in strict mode.</summary>
    public IReadOnlyList<string> ExactPhrases { get; init; } = [];

    /// <summary>Settings for audio that the VAD did not classify as speech (music, singing, effects).</summary>
    public static HallucinationFilterOptions Strict => new()
    {
        MinProbability = 0.75f,
        MinWordProbability = 0.3f,
        MinWords = 3,
        RepeatRunLength = 2,
        ExactPhrases = ["i'm going to go", "i'm going to go now", "thank you very much", "i don't know", "i'm sorry", "let's go", "here we go", "come on", "oh my god", "what are you doing", "i love you", "the end"],
    };
}

/// <summary>
/// Removes the classic Whisper artefacts: credit lines learned from subtitle corpora, music markers, word loops
/// inside a segment and the same sentence repeated across segments.
/// </summary>
public static partial class HallucinationFilter
{
    private static readonly string[] KnownPhrases =
    [
        "субтитры сделал", "субтитры создавал", "субтитры подготовил", "субтитры делал", "субтитры создал", "перевод субтитров",
        "dimatorzok", "редактор субтитров", "корректор", "продолжение следует", "спасибо за просмотр", "спасибо за внимание",
        "подписывайтесь на канал", "ставьте лайк", "смотрите в следующей серии", "до новых встреч", "с вами был",
        "thank you for watching", "thanks for watching", "please subscribe", "like and subscribe", "don't forget to subscribe",
        "we'll be right back", "see you next time", "see you in the next", "thanks for listening",
        "subtitles by", "amara.org", "captions by", "transcribed by", "transcription by", "www.", ".com", ".org",
        "ご視聴ありがとうございました", "字幕", "sous-titres", "untertitel", "subtítulos", "sottotitoli",
    ];

    [GeneratedRegex(@"[\[\(\*]\s*[^\]\)\*]{0,40}?\s*[\]\)\*]")]
    private static partial Regex BracketedRegex();

    [GeneratedRegex(@"[♪♫♬♩🎵🎶]+")]
    private static partial Regex MusicSymbolRegex();

    [GeneratedRegex(@"[\p{L}\p{N}']+")]
    private static partial Regex WordRegex();

    public static Transcript Apply(Transcript transcript, HallucinationFilterOptions? options = null, ILogger? logger = null)
    {
        options ??= new HallucinationFilterOptions();
        if (!options.Enabled)
        {
            return transcript;
        }

        var kept = new List<(TranscriptSegment Segment, string Key)>(transcript.Segments.Count);
        foreach (var original in transcript.Segments)
        {
            var segment = TruncateLoops(original, out var loopReason);
            if (segment is null)
            {
                logger?.LogDebug("Dropped segment {Segment}: {Reason}", original, loopReason);
                continue;
            }

            if (!ReferenceEquals(segment, original))
            {
                logger?.LogDebug("Truncated segment {Segment}: {Reason}", original, loopReason);
            }

            var reason = Judge(segment, options);
            if (reason is not null)
            {
                logger?.LogDebug("Dropped segment {Segment}: {Reason}", segment, reason);
                continue;
            }

            kept.Add((segment, Normalise(segment.Text)));
        }

        // Cross-segment loops: the same line over and over.
        var result = new List<TranscriptSegment>(kept.Count);
        var i = 0;
        while (i < kept.Count)
        {
            var j = i + 1;
            while (j < kept.Count && kept[j].Key == kept[i].Key && kept[i].Key.Length > 0)
            {
                j++;
            }

            var run = j - i;
            if (run >= options.RepeatRunLength)
            {
                logger?.LogDebug("Dropped {Count} repeated segments starting at {Segment}", run, kept[i].Segment);
            }
            else
            {
                for (var k = i; k < j; k++)
                {
                    result.Add(kept[k].Segment);
                }
            }

            i = j;
        }

        return transcript.WithSegments(result);
    }

    /// <summary>Returns null when the segment looks legitimate, otherwise a short reason.</summary>
    public static string? Judge(TranscriptSegment segment, HallucinationFilterOptions options)
    {
        var text = segment.Text;
        var stripped = MusicSymbolRegex().Replace(BracketedRegex().Replace(text, " "), " ").Trim();
        var words = WordRegex().Matches(stripped).Select(m => m.Value.ToLowerInvariant()).ToList();

        if (words.Count == 0)
        {
            return "no words (music / sound marker)";
        }

        if (words.Count < options.MinWords)
        {
            return $"only {words.Count} word(s)";
        }

        if (segment.Probability < options.MinProbability)
        {
            return $"low confidence {segment.Probability:0.00}";
        }

        if (options.MinWordProbability > 0 && segment.Words.Count > 0 && segment.Words.Min(w => w.Probability) < options.MinWordProbability)
        {
            return $"weak word {segment.Words.Min(w => w.Probability):0.00}";
        }

        if (segment.NoSpeechProbability >= options.NoSpeechThreshold && segment.Probability <= options.LowConfidence)
        {
            return $"no-speech {segment.NoSpeechProbability:0.00} with low confidence {segment.Probability:0.00}";
        }

        var lower = text.ToLowerInvariant();
        if (options.ExactPhrases.Count > 0)
        {
            var key = string.Join(' ', words);
            if (options.ExactPhrases.Contains(key, StringComparer.Ordinal))
            {
                return $"known music hallucination '{key}'";
            }
        }

        if (words.Count <= 10)
        {
            foreach (var phrase in KnownPhrases.Concat(options.ExtraPhrases))
            {
                if (lower.Contains(phrase, StringComparison.Ordinal))
                {
                    return $"credit-line phrase '{phrase}'";
                }
            }
        }

        if (words.Count >= 8 && words.Distinct().Count() / (double)words.Count < 0.3)
        {
            return "repetition loop";
        }

        return null;
    }

    /// <summary>
    /// Detects an n-gram (1..4 words) repeated at least three times back to back and cuts the segment at the start of
    /// the loop. Returns null when nothing sensible remains, the original instance when there is no loop.
    /// </summary>
    public static TranscriptSegment? TruncateLoops(TranscriptSegment segment, out string? reason)
    {
        reason = null;
        var tokens = segment.Text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var norm = tokens.Select(Normalise).ToArray();
        var cut = -1;
        for (var n = 1; n <= 4 && cut < 0; n++)
        {
            var minRepeats = n == 1 ? 6 : 3;
            for (var i = 0; i + n * minRepeats <= norm.Length; i++)
            {
                if (n == 1 && norm[i].Length < 2)
                {
                    continue;
                }

                var repeats = 1;
                while (i + (repeats + 1) * n <= norm.Length && Same(norm, i, i + repeats * n, n))
                {
                    repeats++;
                }

                if (repeats >= minRepeats)
                {
                    cut = i;
                    reason = $"{n}-gram loop ×{repeats} at word {i}";
                    break;
                }
            }
        }

        if (cut < 0)
        {
            return segment;
        }

        if (cut < 2)
        {
            return null;
        }

        var keptText = string.Join(' ', tokens.Take(cut));
        if (segment.Words.Count == tokens.Length)
        {
            var keptWords = segment.Words.Take(cut).ToList();
            return segment with { Text = keptText, End = keptWords[^1].End, Words = keptWords };
        }

        return segment with { Text = keptText, Words = segment.Words.Where(w => w.Start < segment.End).ToList() };
    }

    private static bool Same(string[] words, int a, int b, int n)
    {
        for (var k = 0; k < n; k++)
        {
            if (words[a + k] != words[b + k])
            {
                return false;
            }
        }

        return true;
    }

    internal static string Normalise(string text) =>
        string.Join(' ', WordRegex().Matches(text).Select(m => m.Value.ToLowerInvariant().Replace('ё', 'е')));
}

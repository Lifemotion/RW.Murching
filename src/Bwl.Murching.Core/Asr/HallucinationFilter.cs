using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;

namespace Bwl.Murching.Asr;

public sealed record HallucinationFilterOptions
{
    public bool Enabled { get; init; } = true;

    /// <summary>Segments whose no-speech probability exceeds this and whose confidence is low are dropped.</summary>
    public float NoSpeechThreshold { get; init; } = 0.8f;

    public float LowConfidence { get; init; } = 0.45f;

    /// <summary>Extra phrases (case-insensitive substrings) that indicate a hallucinated credit line.</summary>
    public IReadOnlyList<string> ExtraPhrases { get; init; } = [];
}

/// <summary>
/// Removes the classic Whisper artefacts: credit lines learned from subtitle corpora, music markers and word loops.
/// </summary>
public static partial class HallucinationFilter
{
    private static readonly string[] KnownPhrases =
    [
        "субтитры сделал", "субтитры создавал", "субтитры подготовил", "субтитры делал", "субтитры создал",
        "dimatorzok", "редактор субтитров", "корректор", "продолжение следует", "спасибо за просмотр",
        "подписывайтесь на канал", "ставьте лайк",
        "thank you for watching", "thanks for watching", "please subscribe", "like and subscribe",
        "subtitles by", "amara.org", "captions by", "transcribed by", "www.", ".com", ".org",
        "ご視聴ありがとうございました", "字幕", "sous-titres", "untertitel", "subtítulos", "sottotitoli",
    ];

    [GeneratedRegex(@"[\[\(\*]\s*[^\]\)\*]{0,40}?\s*[\]\)\*]")]
    private static partial Regex BracketedRegex();

    [GeneratedRegex(@"[♪♫♬♩🎵🎶]+")]
    private static partial Regex MusicSymbolRegex();

    [GeneratedRegex(@"[\p{L}\p{N}]+")]
    private static partial Regex WordRegex();

    public static Transcript Apply(Transcript transcript, HallucinationFilterOptions? options = null, ILogger? logger = null)
    {
        options ??= new HallucinationFilterOptions();
        if (!options.Enabled)
        {
            return transcript;
        }

        var kept = new List<TranscriptSegment>(transcript.Segments.Count);
        foreach (var segment in transcript.Segments)
        {
            var reason = Judge(segment, options);
            if (reason is null)
            {
                kept.Add(segment);
            }
            else
            {
                logger?.LogDebug("Dropped segment {Segment}: {Reason}", segment, reason);
            }
        }

        return transcript.WithSegments(kept);
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

        if (segment.NoSpeechProbability >= options.NoSpeechThreshold && segment.Probability <= options.LowConfidence)
        {
            return $"no-speech {segment.NoSpeechProbability:0.00} with low confidence {segment.Probability:0.00}";
        }

        var lower = text.ToLowerInvariant();
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

        if (words.Count >= 8)
        {
            var distinct = words.Distinct().Count();
            if (distinct / (double)words.Count < 0.3)
            {
                return "repetition loop";
            }
        }

        var run = 1;
        for (var i = 1; i < words.Count; i++)
        {
            run = words[i] == words[i - 1] && words[i].Length >= 2 ? run + 1 : 1;
            if (run >= 5)
            {
                return "repeated word run";
            }
        }

        return null;
    }
}

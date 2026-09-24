using System.Text.RegularExpressions;
using Whisper.net;

namespace Bwl.Murching.Asr;

/// <summary>
/// Turns Whisper segments and their sub-word tokens into words with timestamps.
/// <para>
/// Whisper tokens are byte-level BPE pieces: a token that starts with a space begins a new word; closing punctuation
/// attaches to the previous word, opening punctuation (quotes, brackets, dashes) starts the next one. Multi-byte UTF-8
/// characters (Cyrillic!) can be split across tokens, which makes token <em>text</em> unreliable; the segment text is
/// therefore the source of truth for the words and the tokens only contribute timing, as long as the word counts agree.
/// </para>
/// </summary>
public static partial class WordAssembler
{
    /// <summary>whisper.cpp token timestamps are expressed in units of 10 ms.</summary>
    private const double TokenTimeUnitMs = 10.0;

    private static readonly TimeSpan MinWordDuration = TimeSpan.FromMilliseconds(40);

    private const string OpeningMarks = "«“‘\"'(¿¡[{-–—";

    [GeneratedRegex(@"^\s*(\[_[A-Za-z_0-9]+\]|<\|[^|]*\|>)\s*$")]
    private static partial Regex SpecialTokenRegex();

    [GeneratedRegex(@"\s+")]
    private static partial Regex WhitespaceRegex();

    public static TranscriptSegment Convert(SegmentData segment, TimeSpan offset, TimeSpan clipDuration, bool useDtw)
    {
        var text = CleanText(segment.Text);
        var relStart = Clamp(segment.Start, TimeSpan.Zero, clipDuration);
        var relEnd = Clamp(segment.End, relStart, clipDuration);
        if (relEnd <= relStart)
        {
            relEnd = Clamp(relStart + TimeSpan.FromMilliseconds(500), relStart, clipDuration);
        }

        var words = text.Length == 0
            ? []
            : BuildWords(segment.Tokens ?? [], text, relStart, relEnd, segment.Probability, useDtw);

        return new TranscriptSegment(
            text,
            relStart + offset,
            relEnd + offset,
            words.Select(w => w.Shift(offset)).ToList(),
            segment.Probability,
            segment.NoSpeechProbability);
    }

    public static string CleanText(string? text) =>
        string.IsNullOrWhiteSpace(text) ? string.Empty : WhitespaceRegex().Replace(text, " ").Trim();

    /// <summary>Groups tokens into words; each group is a list of token indexes into <paramref name="tokenTexts"/>.</summary>
    internal static List<List<int>> GroupTokens(IReadOnlyList<string> tokenTexts)
    {
        var groups = new List<List<int>>();
        var lastWasOpening = false;
        for (var i = 0; i < tokenTexts.Count; i++)
        {
            var text = tokenTexts[i];
            var trimmed = text.Trim();
            var punctuationOnly = trimmed.Length > 0 && trimmed.All(ch => char.IsPunctuation(ch) || char.IsSymbol(ch));
            var leadingSpace = text.Length > 0 && char.IsWhiteSpace(text[0]);
            var opening = punctuationOnly && trimmed.All(ch => OpeningMarks.Contains(ch));

            bool startsWord;
            if (groups.Count == 0)
            {
                startsWord = true;
            }
            else if (lastWasOpening)
            {
                startsWord = false; // the word after an opening quote/bracket joins it
            }
            else if (punctuationOnly)
            {
                startsWord = opening && leadingSpace;
            }
            else
            {
                startsWord = leadingSpace;
            }

            if (startsWord)
            {
                groups.Add([i]);
            }
            else
            {
                groups[^1].Add(i);
            }

            lastWasOpening = opening;
        }

        return groups;
    }

    internal static List<TranscriptWord> BuildWords(IReadOnlyList<WhisperToken> tokens, string text, TimeSpan segStart, TimeSpan segEnd, float segProbability, bool useDtw)
    {
        var textWords = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var usable = tokens.Where(t => !string.IsNullOrEmpty(t.Text) && !SpecialTokenRegex().IsMatch(t.Text)).ToList();
        if (usable.Count == 0 || textWords.Length == 0)
        {
            return SpreadEvenly(textWords, segStart, segEnd, segProbability);
        }

        var groups = GroupTokens(usable.Select(t => t.Text!).ToList());

        // Decide the word texts. The segment text wins when the counts line up.
        string[] wordTexts;
        if (groups.Count == textWords.Length)
        {
            wordTexts = textWords;
        }
        else
        {
            var fromTokens = groups.Select(g => CleanText(string.Concat(g.Select(i => usable[i].Text ?? string.Empty)))).ToArray();
            if (fromTokens.Any(t => t.Contains('�', StringComparison.Ordinal) || t.Length == 0))
            {
                // Broken multi-byte pieces and no way to map them: fall back to even spacing of the real words.
                return SpreadEvenly(textWords, segStart, segEnd, segProbability);
            }

            wordTexts = fromTokens;
        }

        var dtwAvailable = useDtw && groups.All(g => usable[g[0]].DtwTimestamp >= 0);
        TimeSpan[]? onsets = null;
        if (dtwAvailable)
        {
            onsets = groups.Select(g => Clamp(FromTokenUnits(usable[g[0]].DtwTimestamp), segStart, segEnd)).ToArray();

            // DTW pins the last token of a segment to the segment end, which would give the last word zero length
            // (and the previous word an end that is too late). Move the last onset back by a plausible duration.
            var last = onsets.Length - 1;
            var estimate = TimeSpan.FromMilliseconds(Math.Max(MinWordDuration.TotalMilliseconds * 2, 55 * wordTexts[last].Length));
            if (onsets[last] > segEnd - estimate)
            {
                var floor = last > 0 ? onsets[last - 1] + MinWordDuration : segStart;
                var pulled = segEnd - estimate;
                onsets[last] = pulled < floor ? floor : pulled;
            }

            for (var i = 1; i < onsets.Length; i++)
            {
                if (onsets[i] < onsets[i - 1])
                {
                    onsets[i] = onsets[i - 1];
                }
            }
        }

        var words = new List<TranscriptWord>(groups.Count);
        var previousEnd = segStart;
        for (var i = 0; i < groups.Count; i++)
        {
            var group = groups[i].Select(idx => usable[idx]).ToList();
            TimeSpan start, end;
            if (onsets is not null)
            {
                start = onsets[i];
                end = i + 1 < groups.Count ? onsets[i + 1] : segEnd;
            }
            else
            {
                start = FromTokenUnits(group.Min(t => t.Start));
                end = FromTokenUnits(group.Max(t => t.End));
            }

            start = Clamp(start, segStart, segEnd);
            end = Clamp(end, segStart, segEnd);
            if (start < previousEnd)
            {
                start = previousEnd;
            }

            if (end < start + MinWordDuration)
            {
                end = Clamp(start + MinWordDuration, start, segEnd);
            }

            var probability = group.Average(t => t.Probability);
            words.Add(new TranscriptWord(wordTexts[i], start, end, probability));
            previousEnd = end;
        }

        return words;
    }

    private static List<TranscriptWord> SpreadEvenly(string[] textWords, TimeSpan start, TimeSpan end, float probability)
    {
        if (textWords.Length == 0)
        {
            return [];
        }

        var totalChars = textWords.Sum(w => w.Length + 1);
        var cursor = start;
        var span = end - start;
        var result = new List<TranscriptWord>(textWords.Length);
        foreach (var word in textWords)
        {
            var share = span * ((word.Length + 1) / (double)totalChars);
            var wordEnd = cursor + share;
            result.Add(new TranscriptWord(word, cursor, wordEnd, probability));
            cursor = wordEnd;
        }

        result[^1] = result[^1] with { End = end };
        return result;
    }

    private static TimeSpan FromTokenUnits(long units) => TimeSpan.FromMilliseconds(units * TokenTimeUnitMs);

    private static TimeSpan Clamp(TimeSpan v, TimeSpan min, TimeSpan max) => v < min ? min : v > max ? max : v;
}

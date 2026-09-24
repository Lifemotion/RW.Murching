using Bwl.Murching.Asr;

namespace Bwl.Murching.Subtitles;

/// <summary>
/// Builds subtitle cues from timed words.
/// <list type="number">
/// <item>Words are cut into blocks at hard boundaries (long pauses).</item>
/// <item>Each block is partitioned into cues with a dynamic program that prefers breaks at sentence ends, clause
/// punctuation, Whisper segment ends and pauses, penalises very short cues and cues that swallow several sentences,
/// subject to the line/duration limits.</item>
/// <item>Cues are wrapped into balanced lines and their display times are adjusted for reading speed and gaps.</item>
/// </list>
/// </summary>
public static class CueBuilder
{
    private sealed record Unit(string Text, TimeSpan Start, TimeSpan End, bool SegmentEnd);

    public static SubtitleDocument Build(Transcript transcript, CueBuilderOptions? options = null)
    {
        options ??= new CueBuilderOptions();
        var units = Flatten(transcript);
        var cues = new List<SubtitleCue>();
        foreach (var block in SplitBlocks(units, options))
        {
            foreach (var range in Partition(block, options))
            {
                var words = range.Select(u => u.Text).ToList();
                var lines = LineWrapper.Wrap(words, options.MaxLineLength, options.MaxLines, options.PreferShorterTopLine);
                cues.Add(new SubtitleCue(cues.Count + 1, range[0].Start, range[^1].End, lines));
            }
        }

        AdjustTiming(cues, options);
        return new SubtitleDocument(cues) { Language = transcript.Language };
    }

    private static List<Unit> Flatten(Transcript transcript)
    {
        var units = new List<Unit>();
        foreach (var segment in transcript.Segments)
        {
            var words = segment.Words;
            if (words.Count == 0)
            {
                var text = segment.Text.Trim();
                if (text.Length > 0)
                {
                    units.Add(new Unit(text, segment.Start, segment.End, true));
                }

                continue;
            }

            for (var i = 0; i < words.Count; i++)
            {
                var w = words[i];
                var text = w.Text.Trim();
                if (text.Length == 0)
                {
                    continue;
                }

                // Guard against non-monotonic timestamps.
                var start = w.Start;
                var end = w.End < start ? start : w.End;
                if (units.Count > 0 && start < units[^1].End)
                {
                    start = units[^1].End;
                    if (end < start)
                    {
                        end = start;
                    }
                }

                units.Add(new Unit(text, start, end, i == words.Count - 1));
            }
        }

        return units;
    }

    private static IEnumerable<List<Unit>> SplitBlocks(List<Unit> units, CueBuilderOptions o)
    {
        var block = new List<Unit>();
        foreach (var u in units)
        {
            if (block.Count > 0 && u.Start - block[^1].End >= o.PauseSplit)
            {
                yield return block;
                block = [];
            }

            block.Add(u);
        }

        if (block.Count > 0)
        {
            yield return block;
        }
    }

    /// <summary>Partitions a block into cues using minimum-cost segmentation.</summary>
    private static List<List<Unit>> Partition(List<Unit> block, CueBuilderOptions o)
    {
        var n = block.Count;
        var capacity = o.MaxLines * o.MaxLineLength;
        var words = block.Select(u => u.Text).ToList();
        var prefix = new int[n + 1];
        var sentenceEnds = new int[n + 1]; // number of sentence ends among words [0, i)
        for (var i = 0; i < n; i++)
        {
            prefix[i + 1] = prefix[i] + words[i].Length + 1;
            sentenceEnds[i + 1] = sentenceEnds[i] + (TextRules.EndsSentence(words[i]) ? 1 : 0);
        }

        int Chars(int i, int j) => prefix[j] - prefix[i] - 1;

        var inf = double.PositiveInfinity;
        var best = new double[n + 1];
        var from = new int[n + 1];
        Array.Fill(best, inf);
        best[0] = 0;

        for (var j = 1; j <= n; j++)
        {
            for (var i = j - 1; i >= 0; i--)
            {
                var duration = block[j - 1].End - block[i].Start;
                var chars = Chars(i, j);
                var fits = chars <= capacity && duration <= o.MaxDuration &&
                           LineWrapper.GreedyLineCount(words.GetRange(i, j - i), o.MaxLineLength) <= o.MaxLines;
                if (!fits && j - i > 1)
                {
                    // Longer spans only get worse; a single word is always allowed so we never get stuck.
                    break;
                }

                if (best[i] == inf)
                {
                    continue;
                }

                // Sentence ends strictly inside the cue (the last word ending a sentence is fine).
                var innerSentenceEnds = sentenceEnds[j - 1] - sentenceEnds[i];
                var cost = best[i] + CueCost(block, i, j, chars, capacity, innerSentenceEnds, o) + (j < n ? BoundaryPenalty(block, j, o) : 0);
                if (!fits)
                {
                    cost += 10_000; // single over-long word: tolerated but expensive
                }

                if (cost < best[j])
                {
                    best[j] = cost;
                    from[j] = i;
                }
            }
        }

        var result = new List<List<Unit>>();
        var end = n;
        while (end > 0)
        {
            var start = from[end];
            result.Insert(0, block.GetRange(start, end - start));
            end = start;
        }

        return result;
    }

    private static double CueCost(List<Unit> block, int i, int j, int chars, int capacity, int innerSentenceEnds, CueBuilderOptions o)
    {
        // Under-filled cues are penalised relative to a comfortable target, not the hard capacity, so that
        // one sentence per cue stays attractive; filling to the brim is never rewarded.
        var target = Math.Max(o.MinCueChars, capacity * 0.6);
        var cost = 0.0;
        if (chars < target)
        {
            var slack = (target - chars) / target;
            cost += slack * slack * 100;
        }

        if (chars < o.MinCueChars)
        {
            cost += 60;
        }

        cost += innerSentenceEnds * 35;

        var duration = block[j - 1].End - block[i].Start;
        if (duration < o.MinDuration)
        {
            cost += 40;
        }

        // Reading speed: too many characters for the spoken time is uncomfortable even after stretching.
        var cps = duration > TimeSpan.Zero ? chars / duration.TotalSeconds : 0;
        if (cps > o.MaxCharsPerSecond * 1.5)
        {
            cost += 30;
        }

        cost += WrapPenalty(block, i, j, chars, o);
        return cost;
    }

    /// <summary>How ugly the line break inside this cue would be (only cues that need more than one line pay).</summary>
    private static double WrapPenalty(List<Unit> block, int i, int j, int chars, CueBuilderOptions o)
    {
        if (chars <= o.MaxLineLength || o.MaxLines < 2)
        {
            return 0;
        }

        var words = new List<string>(j - i);
        for (var k = i; k < j; k++)
        {
            words.Add(block[k].Text);
        }

        var lines = LineWrapper.Wrap(words, o.MaxLineLength, o.MaxLines, o.PreferShorterTopLine);
        var penalty = 0.0;
        for (var l = 0; l < lines.Count - 1; l++)
        {
            var lastWord = lines[l][(lines[l].LastIndexOf(' ') + 1)..];
            if (TextRules.IsFunctionWord(lastWord))
            {
                penalty += 45;
            }
            else if (!TextRules.EndsSentence(lastWord) && !TextRules.EndsClause(lastWord))
            {
                // Mid-clause break although the cue contains punctuation that could have been used.
                var hasPunctuationInside = false;
                for (var k = i; k < j - 1; k++)
                {
                    if (TextRules.EndsSentence(block[k].Text) || TextRules.EndsClause(block[k].Text))
                    {
                        hasPunctuationInside = true;
                        break;
                    }
                }

                if (hasPunctuationInside)
                {
                    penalty += 20;
                }
            }
        }

        return penalty;
    }

    /// <summary>Penalty for ending a cue after word j-1 (i.e. between j-1 and j).</summary>
    private static double BoundaryPenalty(List<Unit> block, int j, CueBuilderOptions o)
    {
        var before = block[j - 1];
        var after = block[j];
        var pause = after.Start - before.End;

        if (TextRules.EndsSentence(before.Text))
        {
            return 0;
        }

        if (TextRules.IsDialogueDash(after.Text))
        {
            return 0;
        }

        if (TextRules.EndsClause(before.Text))
        {
            return 12;
        }

        // A pause right after an article / preposition / conjunction is a hesitation, not a phrase boundary.
        if (TextRules.IsFunctionWord(before.Text))
        {
            return 120;
        }

        if (pause >= o.PausePreferred)
        {
            return 18;
        }

        // Whisper segment ends are only a weak hint: they often fall mid-clause.
        return before.SegmentEnd ? 55 : 70;
    }

    private static void AdjustTiming(List<SubtitleCue> cues, CueBuilderOptions o)
    {
        for (var i = 0; i < cues.Count; i++)
        {
            var cue = cues[i];
            var start = cue.Start;
            var spokenEnd = cue.End;
            var nextStart = i + 1 < cues.Count ? cues[i + 1].Start : TimeSpan.MaxValue;
            var latest = nextStart == TimeSpan.MaxValue ? spokenEnd + o.TailPadding + o.MinDuration : nextStart - o.MinGap;

            var end = spokenEnd + o.TailPadding;
            var readingTime = TimeSpan.FromSeconds(cue.CharacterCount / o.MaxCharsPerSecond);
            if (end < start + readingTime)
            {
                end = start + readingTime;
            }

            if (end < start + o.MinDuration)
            {
                end = start + o.MinDuration;
            }

            if (end > start + o.MaxDuration)
            {
                end = start + o.MaxDuration;
            }

            if (end > latest)
            {
                end = latest;
            }

            if (end <= start)
            {
                end = spokenEnd > start ? spokenEnd : start + TimeSpan.FromMilliseconds(500);
                if (end > latest && latest > start)
                {
                    end = latest;
                }
            }

            cues[i] = cue with { Start = start, End = end };
        }
    }
}

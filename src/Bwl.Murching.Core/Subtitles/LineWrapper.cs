namespace Bwl.Murching.Subtitles;

/// <summary>Splits a list of words into balanced subtitle lines.</summary>
public static class LineWrapper
{
    /// <summary>Minimum number of lines the words need with greedy filling (used as a feasibility check).</summary>
    public static int GreedyLineCount(IReadOnlyList<string> words, int maxLineLength)
    {
        if (words.Count == 0)
        {
            return 0;
        }

        var lines = 1;
        var current = 0;
        foreach (var w in words)
        {
            if (current == 0)
            {
                current = w.Length;
            }
            else if (current + 1 + w.Length <= maxLineLength)
            {
                current += 1 + w.Length;
            }
            else
            {
                lines++;
                current = w.Length;
            }
        }

        return lines;
    }

    /// <summary>
    /// Wraps words into at most <paramref name="maxLines"/> lines, each at most <paramref name="maxLineLength"/> characters
    /// when possible. Breaks after punctuation and before dialogue dashes are preferred; lines are balanced.
    /// </summary>
    public static IReadOnlyList<string> Wrap(IReadOnlyList<string> words, int maxLineLength, int maxLines, bool preferShorterTop = true)
    {
        if (words.Count == 0)
        {
            return [];
        }

        var total = words.Sum(w => w.Length) + words.Count - 1;

        // Two speakers in one cue: each gets a line of their own, even if everything would fit on one.
        if (maxLines >= 2)
        {
            var dashes = Enumerable.Range(1, words.Count - 1).Where(i => TextRules.IsDialogueDash(words[i])).ToList();
            if (dashes.Count == 1)
            {
                var first = string.Join(' ', words.Take(dashes[0]));
                var second = string.Join(' ', words.Skip(dashes[0]));
                if (first.Length <= maxLineLength && second.Length <= maxLineLength)
                {
                    return [first, second];
                }
            }
        }

        if (total <= maxLineLength || maxLines <= 1 || words.Count == 1)
        {
            return [string.Join(' ', words)];
        }

        var needed = GreedyLineCount(words, maxLineLength);
        var lineCount = Math.Clamp(needed, 2, maxLines);

        if (lineCount == 2)
        {
            return WrapTwo(words, maxLineLength, preferShorterTop);
        }

        return WrapDp(words, maxLineLength, lineCount);
    }

    private static IReadOnlyList<string> WrapTwo(IReadOnlyList<string> words, int maxLineLength, bool preferShorterTop)
    {
        var prefix = new int[words.Count + 1];
        for (var i = 0; i < words.Count; i++)
        {
            prefix[i + 1] = prefix[i] + words[i].Length + (i > 0 ? 1 : 0);
        }

        var bestCost = double.MaxValue;
        var bestSplit = -1;
        for (var k = 1; k < words.Count; k++)
        {
            var len1 = prefix[k];
            var len2 = prefix[words.Count] - prefix[k] - 1;
            double cost = 0;
            if (len1 > maxLineLength)
            {
                cost += (len1 - maxLineLength) * 1000.0;
            }

            if (len2 > maxLineLength)
            {
                cost += (len2 - maxLineLength) * 1000.0;
            }

            cost += Math.Pow(len1 - len2, 2);
            cost += BreakPenalty(words, k);
            if (preferShorterTop && len1 > len2)
            {
                cost += 40;
            }

            if (cost < bestCost)
            {
                bestCost = cost;
                bestSplit = k;
            }
        }

        return
        [
            string.Join(' ', words.Take(bestSplit)),
            string.Join(' ', words.Skip(bestSplit)),
        ];
    }

    /// <summary>Minimum raggedness partition into exactly <paramref name="lineCount"/> lines.</summary>
    private static IReadOnlyList<string> WrapDp(IReadOnlyList<string> words, int maxLineLength, int lineCount)
    {
        var n = words.Count;
        var prefix = new int[n + 1];
        for (var i = 0; i < n; i++)
        {
            prefix[i + 1] = prefix[i] + words[i].Length + 1; // includes one trailing space per word
        }

        int Len(int i, int j) => prefix[j] - prefix[i] - 1;

        var inf = double.PositiveInfinity;
        var cost = new double[lineCount + 1, n + 1];
        var from = new int[lineCount + 1, n + 1];
        for (var l = 0; l <= lineCount; l++)
        {
            for (var j = 0; j <= n; j++)
            {
                cost[l, j] = inf;
            }
        }

        cost[0, 0] = 0;
        for (var l = 1; l <= lineCount; l++)
        {
            for (var j = 1; j <= n; j++)
            {
                for (var i = l - 1; i < j; i++)
                {
                    if (cost[l - 1, i] == inf)
                    {
                        continue;
                    }

                    var len = Len(i, j);
                    var c = cost[l - 1, i] + Math.Pow(maxLineLength - len, 2) + (len > maxLineLength ? (len - maxLineLength) * 1000.0 : 0) + (j < n ? BreakPenalty(words, j) : 0);
                    if (c < cost[l, j])
                    {
                        cost[l, j] = c;
                        from[l, j] = i;
                    }
                }
            }
        }

        var lines = new List<string>();
        var end = n;
        for (var l = lineCount; l >= 1; l--)
        {
            var start = from[l, end];
            lines.Insert(0, string.Join(' ', words.Skip(start).Take(end - start)));
            end = start;
        }

        return lines;
    }

    /// <summary>Cost of breaking a line between word k-1 and word k (lower is better). Scaled against squared length imbalance.</summary>
    internal static double BreakPenalty(IReadOnlyList<string> words, int k)
    {
        var before = words[k - 1];
        var after = words[k];
        if (TextRules.IsDialogueDash(after))
        {
            return -800;
        }

        if (TextRules.EndsSentence(before))
        {
            return -600;
        }

        if (TextRules.EndsClause(before))
        {
            return -250;
        }

        // Do not split articles / prepositions / conjunctions from the word they introduce.
        if (TextRules.IsFunctionWord(before))
        {
            return 300;
        }

        return 0;
    }
}

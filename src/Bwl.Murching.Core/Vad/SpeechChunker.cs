namespace Bwl.Murching.Vad;

public sealed record ChunkingOptions
{
    /// <summary>Upper bound for one Whisper call. Keeping it at the 30 s window avoids in-model sliding.</summary>
    public TimeSpan MaxChunkDuration { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>Neighbouring speech segments separated by a pause longer than this are never merged.</summary>
    public TimeSpan MaxGap { get; init; } = TimeSpan.FromSeconds(2);

    /// <summary>Extra audio kept before and after each chunk so word onsets are not clipped.</summary>
    public TimeSpan Padding { get; init; } = TimeSpan.FromMilliseconds(250);
}

/// <summary>Groups VAD speech segments into chunks suitable for one Whisper inference each.</summary>
public static class SpeechChunker
{
    public static IReadOnlyList<SpeechSegment> Chunk(IReadOnlyList<SpeechSegment> segments, TimeSpan totalDuration, ChunkingOptions? options = null)
    {
        options ??= new ChunkingOptions();
        var max = options.MaxChunkDuration;
        var merged = new List<SpeechSegment>();
        SpeechSegment? current = null;

        foreach (var raw in segments.OrderBy(s => s.Start))
        {
            foreach (var seg in SplitLong(raw, max))
            {
                if (current is null)
                {
                    current = seg;
                    continue;
                }

                var c = current.Value;
                var gap = seg.Start - c.End;
                if (gap <= options.MaxGap && seg.End - c.Start <= max)
                {
                    current = new SpeechSegment(c.Start, seg.End > c.End ? seg.End : c.End);
                }
                else
                {
                    merged.Add(c);
                    current = seg;
                }
            }
        }

        if (current is { } last)
        {
            merged.Add(last);
        }

        // Pad, then resolve overlaps between neighbours by meeting in the middle of the gap.
        var padded = merged.Select(s => s.Pad(options.Padding, TimeSpan.Zero, totalDuration)).ToList();
        for (var i = 1; i < padded.Count; i++)
        {
            if (padded[i].Start < padded[i - 1].End)
            {
                var mid = merged[i - 1].End + (merged[i].Start - merged[i - 1].End) / 2;
                padded[i - 1] = padded[i - 1] with { End = mid };
                padded[i] = padded[i] with { Start = mid };
            }
        }

        return padded.Where(s => s.Duration > TimeSpan.Zero).ToList();
    }

    private static IEnumerable<SpeechSegment> SplitLong(SpeechSegment seg, TimeSpan max)
    {
        if (seg.Duration <= max)
        {
            yield return seg;
            yield break;
        }

        var parts = (int)Math.Ceiling(seg.Duration / max);
        var step = seg.Duration / parts;
        for (var i = 0; i < parts; i++)
        {
            var start = seg.Start + step * i;
            var end = i == parts - 1 ? seg.End : start + step;
            yield return new SpeechSegment(start, end);
        }
    }
}

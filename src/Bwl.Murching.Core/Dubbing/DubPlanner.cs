using Bwl.Murching.Subtitles;

namespace Bwl.Murching.Dubbing;

/// <summary>One utterance to synthesise: translated text, the slot it must land in, and the speaker reference window.</summary>
public sealed record DubUnit(
    int Index,
    TimeSpan Start,
    TimeSpan End,
    string Text,
    TimeSpan ReferenceStart,
    TimeSpan ReferenceEnd)
{
    public TimeSpan Duration => End - Start;

    /// <summary>Time until the next utterance starts (or the end of the media) — how far the speech may spill over.</summary>
    public TimeSpan SlotEnd { get; init; }
}

public static class DubPlanner
{
    /// <summary>Groups translated cues into utterances and attaches a reference window to each.</summary>
    public static IReadOnlyList<DubUnit> Plan(SubtitleDocument translated, TimeSpan mediaDuration, DubbingOptions options)
    {
        var merged = new List<(TimeSpan Start, TimeSpan End, List<string> Texts)>();
        foreach (var cue in translated.Cues.OrderBy(c => c.Start))
        {
            var text = string.Join(' ', cue.Lines).Trim();
            if (text.Length == 0)
            {
                continue;
            }

            if (merged.Count > 0)
            {
                var last = merged[^1];
                var gap = cue.Start - last.End;
                var combined = cue.End - last.Start;
                var lastText = last.Texts[^1];
                var sentenceEnded = TextRules.EndsSentence(lastText);
                // Keep a sentence together across cue boundaries; glue complete sentences only when they follow each
                // other almost immediately. Longer utterances fit their slots far better (Russian runs ~20% longer than
                // English and XTTS paces long texts faster), at the price of blending speakers in rapid-fire dialogue.
                var shouldMerge = gap <= options.MergeGap && combined <= options.MaxUnitDuration && (!sentenceEnded || gap <= options.SentenceJoinGap);
                if (shouldMerge)
                {
                    last.Texts.Add(text);
                    merged[^1] = (last.Start, cue.End > last.End ? cue.End : last.End, last.Texts);
                    continue;
                }
            }

            merged.Add((cue.Start, cue.End, [text]));
        }

        var units = new List<DubUnit>(merged.Count);
        for (var i = 0; i < merged.Count; i++)
        {
            var (start, end, texts) = merged[i];
            var (refStart, refEnd) = ReferenceWindow(start, end, mediaDuration, options);
            var slotEnd = i + 1 < merged.Count ? merged[i + 1].Start : mediaDuration;
            units.Add(new DubUnit(i + 1, start, end, string.Join(' ', texts), refStart, refEnd) { SlotEnd = slotEnd });
        }

        return units;
    }

    /// <summary>Expands the utterance symmetrically until it is at least <see cref="DubbingOptions.MinReference"/> long, clamped to the media.</summary>
    internal static (TimeSpan Start, TimeSpan End) ReferenceWindow(TimeSpan start, TimeSpan end, TimeSpan total, DubbingOptions o)
    {
        var duration = end - start;
        if (duration > o.MaxReference)
        {
            var centre = start + duration / 2;
            return (centre - o.MaxReference / 2, centre + o.MaxReference / 2);
        }

        var missing = o.MinReference - duration;
        if (missing <= TimeSpan.Zero)
        {
            return (start, end);
        }

        var refStart = start - missing / 2;
        var refEnd = end + missing / 2;
        if (refStart < TimeSpan.Zero)
        {
            refEnd += -refStart;
            refStart = TimeSpan.Zero;
        }

        if (refEnd > total)
        {
            refStart -= refEnd - total;
            refEnd = total;
            if (refStart < TimeSpan.Zero)
            {
                refStart = TimeSpan.Zero;
            }
        }

        return (refStart, refEnd);
    }
}

/// <summary>Decides how a synthesised clip is squeezed into its slot.</summary>
public static class DurationFitter
{
    public sealed record Fit(double Tempo, TimeSpan PlacedDuration, bool Overruns);

    /// <summary>
    /// The clip may use its own slot plus half of the silence before the next utterance. If it is still too long it
    /// is sped up, at most by <see cref="DubbingOptions.MaxSpeedUp"/>; anything beyond that overruns.
    /// </summary>
    public static Fit Compute(TimeSpan synthesized, DubUnit unit, DubbingOptions options)
    {
        var spare = unit.SlotEnd - unit.End;
        var available = unit.Duration + (spare > TimeSpan.Zero ? spare / 2 : TimeSpan.Zero);
        if (synthesized <= available)
        {
            return new Fit(1.0, synthesized, false);
        }

        var factor = synthesized / available;
        if (factor <= options.MaxSpeedUp)
        {
            return new Fit(factor, available, false);
        }

        return new Fit(options.MaxSpeedUp, synthesized / options.MaxSpeedUp, true);
    }
}

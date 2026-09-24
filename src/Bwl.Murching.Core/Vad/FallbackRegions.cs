using Bwl.Murching.Audio;

namespace Bwl.Murching.Vad;

/// <summary>
/// Silero VAD is trained on speech and ignores singing, rap over a beat, vocoded voices and heavily processed
/// dialogue. Whisper, on the other hand, transcribes lyrics quite well but hallucinates on pure music. The fallback
/// finds long non-speech gaps that still carry energy and hands them to Whisper as <em>tentative</em> chunks whose
/// output is then filtered strictly.
/// </summary>
public sealed record FallbackOptions
{
    public bool Enabled { get; init; } = true;

    /// <summary>Gaps between speech chunks shorter than this are left alone.</summary>
    public TimeSpan MinGap { get; init; } = TimeSpan.FromSeconds(3);

    /// <summary>RMS level (linear, 1.0 = full scale) above which a window counts as "there is something"; 0.01 ≈ −40 dBFS.</summary>
    public float EnergyThreshold { get; init; } = 0.01f;

    /// <summary>Window used for the energy scan.</summary>
    public TimeSpan Window { get; init; } = TimeSpan.FromSeconds(1);

    /// <summary>Consecutive quiet windows tolerated inside one active region.</summary>
    public int MaxQuietWindowsInside { get; init; } = 1;

    public TimeSpan MaxChunkDuration { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// A tentative chunk is only transcribed when Whisper's language detection on that chunk agrees with the language
    /// of the file. Chanting, foreign lyrics and pure music produce random low-probability languages and are skipped.
    /// </summary>
    public bool RequireLanguageMatch { get; init; } = true;

    public float MinLanguageProbability { get; init; } = 0.5f;

    /// <summary>
    /// When the file-level language detection is weaker than this, even the VAD-approved chunks are treated as
    /// tentative (per-chunk language check + strict filter). Chanting and non-speech vocals land here.
    /// </summary>
    public float UncertainFileLanguageProbability { get; init; } = 0.7f;
}

public static class FallbackRegions
{
    /// <summary>Energetic regions outside <paramref name="speechChunks"/>, each at most <see cref="FallbackOptions.MaxChunkDuration"/> long.</summary>
    public static IReadOnlyList<SpeechSegment> Find(PcmAudio audio, IReadOnlyList<SpeechSegment> speechChunks, FallbackOptions? options = null)
    {
        options ??= new FallbackOptions();
        if (!options.Enabled || audio.Length == 0)
        {
            return [];
        }

        var result = new List<SpeechSegment>();
        foreach (var gap in Gaps(speechChunks, audio.Duration))
        {
            if (gap.Duration < options.MinGap)
            {
                continue;
            }

            foreach (var region in ActiveRegions(audio, gap, options))
            {
                if (region.Duration < options.MinGap)
                {
                    continue;
                }

                result.AddRange(SplitEvenly(region, options.MaxChunkDuration));
            }
        }

        return result;
    }

    /// <summary>Complement of the (sorted, non-overlapping) chunks inside [0, total].</summary>
    internal static IEnumerable<SpeechSegment> Gaps(IReadOnlyList<SpeechSegment> chunks, TimeSpan total)
    {
        var cursor = TimeSpan.Zero;
        foreach (var chunk in chunks.OrderBy(c => c.Start))
        {
            if (chunk.Start > cursor)
            {
                yield return new SpeechSegment(cursor, chunk.Start);
            }

            if (chunk.End > cursor)
            {
                cursor = chunk.End;
            }
        }

        if (total > cursor)
        {
            yield return new SpeechSegment(cursor, total);
        }
    }

    private static List<SpeechSegment> ActiveRegions(PcmAudio audio, SpeechSegment gap, FallbackOptions o)
    {
        var windowSamples = Math.Max(1, (int)(o.Window.TotalSeconds * audio.SampleRate));
        var from = audio.ToSampleIndex(gap.Start);
        var to = audio.ToSampleIndex(gap.End);
        var samples = audio.Samples.Span;
        var regions = new List<SpeechSegment>();

        int? regionStart = null;
        var quietRun = 0;
        var lastActiveEnd = 0;
        for (var pos = from; pos < to; pos += windowSamples)
        {
            var len = Math.Min(windowSamples, to - pos);
            var active = Rms(samples.Slice(pos, len)) >= o.EnergyThreshold;
            if (active)
            {
                regionStart ??= pos;
                lastActiveEnd = pos + len;
                quietRun = 0;
            }
            else if (regionStart is not null)
            {
                quietRun++;
                if (quietRun > o.MaxQuietWindowsInside)
                {
                    regions.Add(new SpeechSegment(audio.ToTime(regionStart.Value), audio.ToTime(lastActiveEnd)));
                    regionStart = null;
                    quietRun = 0;
                }
            }
        }

        if (regionStart is not null)
        {
            regions.Add(new SpeechSegment(audio.ToTime(regionStart.Value), audio.ToTime(lastActiveEnd)));
        }

        return regions;
    }

    private static IEnumerable<SpeechSegment> SplitEvenly(SpeechSegment region, TimeSpan max)
    {
        if (region.Duration <= max)
        {
            yield return region;
            yield break;
        }

        var parts = (int)Math.Ceiling(region.Duration / max);
        var step = region.Duration / parts;
        for (var i = 0; i < parts; i++)
        {
            var start = region.Start + step * i;
            yield return new SpeechSegment(start, i == parts - 1 ? region.End : start + step);
        }
    }

    private static float Rms(ReadOnlySpan<float> span)
    {
        if (span.IsEmpty)
        {
            return 0;
        }

        double sum = 0;
        foreach (var s in span)
        {
            sum += (double)s * s;
        }

        return (float)Math.Sqrt(sum / span.Length);
    }
}

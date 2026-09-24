namespace Bwl.Murching.Audio;

/// <summary>
/// Mono PCM audio as 32-bit floats in the range [-1, 1]. Slices are views over the same buffer (no copy).
/// </summary>
public sealed class PcmAudio
{
    public const int WhisperSampleRate = 16_000;

    public PcmAudio(ReadOnlyMemory<float> samples, int sampleRate = WhisperSampleRate)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(sampleRate, 0);
        Samples = samples;
        SampleRate = sampleRate;
    }

    public ReadOnlyMemory<float> Samples { get; }

    public int SampleRate { get; }

    public int Length => Samples.Length;

    public TimeSpan Duration => ToTime(Length);

    public static PcmAudio Silence(TimeSpan duration, int sampleRate = WhisperSampleRate) =>
        new(new float[(int)Math.Round(duration.TotalSeconds * sampleRate)], sampleRate);

    public int ToSampleIndex(TimeSpan time) =>
        (int)Math.Clamp(Math.Round(time.TotalSeconds * SampleRate), 0, Length);

    public TimeSpan ToTime(int sampleIndex) => TimeSpan.FromSeconds(sampleIndex / (double)SampleRate);

    /// <summary>Returns a view of the audio between <paramref name="start"/> and <paramref name="end"/> (clamped to the buffer).</summary>
    public PcmAudio Slice(TimeSpan start, TimeSpan end)
    {
        var from = ToSampleIndex(start);
        var to = Math.Max(from, ToSampleIndex(end));
        return new PcmAudio(Samples.Slice(from, to - from), SampleRate);
    }

    public PcmAudio Slice(int start, int length) => new(Samples.Slice(start, length), SampleRate);

    public float[] ToArray() => Samples.ToArray();

    /// <summary>Root mean square level (0..1).</summary>
    public float Rms()
    {
        var span = Samples.Span;
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

    /// <summary>Peak absolute amplitude (0..1).</summary>
    public float Peak()
    {
        var span = Samples.Span;
        float peak = 0;
        foreach (var s in span)
        {
            var a = Math.Abs(s);
            if (a > peak)
            {
                peak = a;
            }
        }

        return peak;
    }

    /// <summary>Concatenates several clips (same sample rate) into one buffer.</summary>
    public static PcmAudio Concat(IEnumerable<PcmAudio> parts)
    {
        var list = parts.ToList();
        if (list.Count == 0)
        {
            return new PcmAudio(ReadOnlyMemory<float>.Empty);
        }

        var rate = list[0].SampleRate;
        if (list.Any(p => p.SampleRate != rate))
        {
            throw new ArgumentException("All parts must share the same sample rate.", nameof(parts));
        }

        var buffer = new float[list.Sum(p => p.Length)];
        var offset = 0;
        foreach (var part in list)
        {
            part.Samples.Span.CopyTo(buffer.AsSpan(offset));
            offset += part.Length;
        }

        return new PcmAudio(buffer, rate);
    }
}

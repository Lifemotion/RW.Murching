namespace Bwl.Murching.Vad;

/// <summary>A time range that contains speech.</summary>
public readonly record struct SpeechSegment(TimeSpan Start, TimeSpan End)
{
    public TimeSpan Duration => End - Start;

    public SpeechSegment Pad(TimeSpan padding, TimeSpan min, TimeSpan max) =>
        new(Clamp(Start - padding, min, max), Clamp(End + padding, min, max));

    private static TimeSpan Clamp(TimeSpan v, TimeSpan min, TimeSpan max) => v < min ? min : v > max ? max : v;

    public override string ToString() => $"{Start:hh\\:mm\\:ss\\.fff} - {End:hh\\:mm\\:ss\\.fff}";
}

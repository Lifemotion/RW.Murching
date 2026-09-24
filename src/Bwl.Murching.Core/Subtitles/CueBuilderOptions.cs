namespace Bwl.Murching.Subtitles;

/// <summary>Layout and timing rules for turning words into subtitle cues. Defaults follow common broadcast guidelines.</summary>
public sealed record CueBuilderOptions
{
    /// <summary>Maximum characters per line (42 is the usual TV / streaming limit for Latin and Cyrillic scripts).</summary>
    public int MaxLineLength { get; init; } = 42;

    public int MaxLines { get; init; } = 2;

    public TimeSpan MaxDuration { get; init; } = TimeSpan.FromSeconds(7);

    /// <summary>Cues shorter than this are extended when the following cue leaves room.</summary>
    public TimeSpan MinDuration { get; init; } = TimeSpan.FromSeconds(1);

    /// <summary>Minimum blank time between consecutive cues.</summary>
    public TimeSpan MinGap { get; init; } = TimeSpan.FromMilliseconds(80);

    /// <summary>A pause between two words at least this long always starts a new cue.</summary>
    public TimeSpan PauseSplit { get; init; } = TimeSpan.FromSeconds(1);

    /// <summary>Pauses at least this long are preferred split points (but not mandatory).</summary>
    public TimeSpan PausePreferred { get; init; } = TimeSpan.FromMilliseconds(350);

    /// <summary>Reading speed cap; cue display is stretched (when possible) so this is not exceeded.</summary>
    public double MaxCharsPerSecond { get; init; } = 21;

    /// <summary>Cues stay on screen this long after the last word ends (when the next cue allows).</summary>
    public TimeSpan TailPadding { get; init; } = TimeSpan.FromMilliseconds(350);

    /// <summary>Cues with fewer characters are penalised so very short cues get merged with neighbours.</summary>
    public int MinCueChars { get; init; } = 10;

    /// <summary>Prefer a shorter top line when wrapping into two lines.</summary>
    public bool PreferShorterTopLine { get; init; } = true;
}

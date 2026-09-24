using Bwl.Murching.Dubbing;
using Bwl.Murching.Subtitles;

namespace Bwl.Murching.Tests.Dubbing;

public class DubPlannerTests
{
    private static readonly DubbingOptions Options = new() { InputPath = "x.mp4", TargetLanguage = "ru" };

    private static SubtitleCue Cue(int i, double start, double end, string text) =>
        new(i, TimeSpan.FromSeconds(start), TimeSpan.FromSeconds(end), [text]);

    [Fact]
    public void Sentence_split_across_cues_becomes_one_utterance()
    {
        var doc = new SubtitleDocument([
            Cue(1, 0, 2, "Я думаю, что нам стоит"),
            Cue(2, 2.1, 4, "поехать туда завтра."),
            Cue(3, 4.4, 6, "Хорошо."),
            Cue(4, 9, 11, "Совсем другая мысль."),
        ]);
        var units = DubPlanner.Plan(doc, TimeSpan.FromSeconds(60), Options);
        Assert.Equal(3, units.Count);
        Assert.Equal("Я думаю, что нам стоит поехать туда завтра.", units[0].Text);
        Assert.Equal(TimeSpan.FromSeconds(0), units[0].Start);
        Assert.Equal(TimeSpan.FromSeconds(4), units[0].End);
        Assert.Equal(TimeSpan.FromSeconds(4.4), units[0].SlotEnd);
        Assert.Equal("Хорошо.", units[1].Text);
        Assert.Equal(TimeSpan.FromSeconds(60), units[2].SlotEnd);
    }

    [Fact]
    public void Long_runs_are_capped_by_max_unit_duration()
    {
        var cues = Enumerable.Range(0, 10).Select(i => Cue(i + 1, i * 3, i * 3 + 2.9, "слово за словом без точки")).ToList();
        var units = DubPlanner.Plan(new SubtitleDocument(cues), TimeSpan.FromSeconds(60), Options);
        Assert.True(units.Count >= 2);
        Assert.All(units, u => Assert.True(u.Duration <= Options.MaxUnitDuration));
    }

    [Fact]
    public void Reference_window_is_expanded_and_clamped()
    {
        var (s, e) = DubPlanner.ReferenceWindow(TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(100), Options);
        Assert.Equal(TimeSpan.Zero, s);
        Assert.Equal(TimeSpan.FromSeconds(6), e);

        (s, e) = DubPlanner.ReferenceWindow(TimeSpan.FromSeconds(50), TimeSpan.FromSeconds(52), TimeSpan.FromSeconds(100), Options);
        Assert.Equal(TimeSpan.FromSeconds(48), s);
        Assert.Equal(TimeSpan.FromSeconds(54), e);

        (s, e) = DubPlanner.ReferenceWindow(TimeSpan.FromSeconds(98), TimeSpan.FromSeconds(99), TimeSpan.FromSeconds(100), Options);
        Assert.Equal(TimeSpan.FromSeconds(94), s);
        Assert.Equal(TimeSpan.FromSeconds(100), e);

        (s, e) = DubPlanner.ReferenceWindow(TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(40), TimeSpan.FromSeconds(100), Options);
        Assert.Equal(TimeSpan.FromSeconds(20), e - s);
    }

    [Fact]
    public void Duration_fitter_uses_half_the_gap_then_speeds_up_then_overruns()
    {
        var unit = new DubUnit(1, TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(12), "x", TimeSpan.Zero, TimeSpan.Zero) { SlotEnd = TimeSpan.FromSeconds(14) };

        var fit = DurationFitter.Compute(TimeSpan.FromSeconds(2.5), unit, Options);
        Assert.Equal(1.0, fit.Tempo);
        Assert.False(fit.Overruns);

        fit = DurationFitter.Compute(TimeSpan.FromSeconds(3.6), unit, Options);
        Assert.Equal(1.2, fit.Tempo, 2);
        Assert.Equal(TimeSpan.FromSeconds(3), fit.PlacedDuration);

        fit = DurationFitter.Compute(TimeSpan.FromSeconds(6), unit, Options);
        Assert.Equal(Options.MaxSpeedUp, fit.Tempo);
        Assert.True(fit.Overruns);
    }

    [Fact]
    public void Soft_clip_is_identity_below_knee_and_bounded()
    {
        Assert.Equal(0.5f, AudioMixer.SoftClip(0.5f));
        Assert.InRange(AudioMixer.SoftClip(3f), 0.8f, 1f);
        Assert.InRange(AudioMixer.SoftClip(-3f), -1f, -0.8f);
        Assert.True(AudioMixer.SoftClip(0.9f) > 0.8f);
    }

    [Fact]
    public void Mix_ducks_original_only_where_voice_is_placed()
    {
        var rate = 1000;
        var original = new InterleavedPcm(Enumerable.Repeat(0.5f, 10 * rate * 2).ToArray(), 2, rate);
        var voice = new float[10 * rate];
        Array.Fill(voice, 0.2f, 4 * rate, 2 * rate);
        var mixed = AudioMixer.Mix(original, voice, [(TimeSpan.FromSeconds(4), TimeSpan.FromSeconds(6))], 0.2, TimeSpan.FromMilliseconds(100));

        Assert.Equal(0.5f, mixed.Samples[1 * rate * 2], 3);                 // untouched before
        Assert.Equal(0.5f * 0.2f + 0.2f, mixed.Samples[5 * rate * 2], 3);   // ducked + voice inside
        Assert.Equal(0.5f, mixed.Samples[8 * rate * 2], 3);                 // untouched after
    }
}

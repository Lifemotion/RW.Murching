using Bwl.Murching.Audio;
using Bwl.Murching.Vad;

namespace Bwl.Murching.Tests.Vad;

public class FallbackRegionsTests
{
    private static PcmAudio Synth(int seconds, params (double From, double To, float Amplitude)[] bursts)
    {
        var rate = 16000;
        var buf = new float[seconds * rate];
        var rng = new Random(42);
        foreach (var (from, to, amp) in bursts)
        {
            for (var i = (int)(from * rate); i < (int)(to * rate) && i < buf.Length; i++)
            {
                buf[i] = (float)((rng.NextDouble() * 2 - 1) * amp);
            }
        }

        return new PcmAudio(buf, rate);
    }

    private static SpeechSegment S(double a, double b) => new(TimeSpan.FromSeconds(a), TimeSpan.FromSeconds(b));

    [Fact]
    public void Gaps_are_the_complement_of_chunks()
    {
        var gaps = FallbackRegions.Gaps([S(10, 20), S(25, 30)], TimeSpan.FromSeconds(60)).ToList();
        Assert.Equal([S(0, 10), S(20, 25), S(30, 60)], gaps);
    }

    [Fact]
    public void Silent_gaps_produce_nothing()
    {
        var audio = Synth(60, (10, 20, 0.3f));
        var regions = FallbackRegions.Find(audio, [S(10, 20)]);
        Assert.Empty(regions);
    }

    [Fact]
    public void Energetic_gap_becomes_a_region()
    {
        var audio = Synth(60, (10, 20, 0.3f), (25, 50, 0.1f));
        var regions = FallbackRegions.Find(audio, [S(10, 20)]);
        var region = Assert.Single(regions);
        Assert.Equal(TimeSpan.FromSeconds(25), region.Start);
        Assert.Equal(TimeSpan.FromSeconds(50), region.End);
    }

    [Fact]
    public void Long_regions_are_split_to_max_chunk()
    {
        var audio = Synth(120, (0, 120, 0.1f));
        var regions = FallbackRegions.Find(audio, []);
        Assert.Equal(4, regions.Count);
        Assert.All(regions, r => Assert.True(r.Duration <= TimeSpan.FromSeconds(30)));
        Assert.Equal(TimeSpan.FromSeconds(120), regions[^1].End);
    }

    [Fact]
    public void Short_gaps_and_short_bursts_are_ignored()
    {
        var audio = Synth(30, (0, 10, 0.3f), (12, 13, 0.3f), (14, 30, 0.3f));
        var regions = FallbackRegions.Find(audio, [S(0, 10), S(14, 30)]);
        Assert.Empty(regions);
    }

    [Fact]
    public void Disabled_returns_nothing()
    {
        var audio = Synth(60, (0, 60, 0.3f));
        Assert.Empty(FallbackRegions.Find(audio, [], new FallbackOptions { Enabled = false }));
    }
}

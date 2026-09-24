using Bwl.Murching.Asr;

namespace Bwl.Murching.Tests.Asr;

public class HallucinationFilterLoopTests
{
    private static TranscriptSegment Seg(string text, double start = 0, float prob = 0.9f)
    {
        var words = new List<TranscriptWord>();
        var t = start;
        foreach (var w in text.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            words.Add(new TranscriptWord(w, TimeSpan.FromSeconds(t), TimeSpan.FromSeconds(t + 0.3), prob));
            t += 0.3;
        }

        return new TranscriptSegment(text, TimeSpan.FromSeconds(start), TimeSpan.FromSeconds(t), words, prob, 0f);
    }

    [Fact]
    public void Bigram_loop_is_truncated_keeping_prefix()
    {
        var seg = Seg("Sit right back as fuck as fuck as fuck as fuck as fuck.");
        var result = HallucinationFilter.TruncateLoops(seg, out var reason);
        Assert.NotNull(result);
        Assert.Equal("Sit right back", result!.Text);
        Assert.Equal(3, result.Words.Count);
        Assert.Contains("2-gram", reason);
    }

    [Fact]
    public void Loop_from_the_start_drops_segment()
    {
        var seg = Seg("go go go go go go");
        Assert.Null(HallucinationFilter.TruncateLoops(seg, out _));
    }

    [Fact]
    public void Normal_text_is_untouched()
    {
        var seg = Seg("The quick brown fox jumps over the lazy dog and the dog sleeps.");
        Assert.Same(seg, HallucinationFilter.TruncateLoops(seg, out _));
    }

    [Fact]
    public void Repeated_segments_are_removed_as_a_run()
    {
        var segments = Enumerable.Range(0, 6).Select(i => Seg("I'm going to go.", i * 2)).ToList();
        segments.Insert(0, Seg("Real sentence here.", -5));
        var filtered = HallucinationFilter.Apply(new Transcript("en", segments));
        var only = Assert.Single(filtered.Segments);
        Assert.Equal("Real sentence here.", only.Text);
    }

    [Fact]
    public void Two_identical_chorus_lines_survive_normal_mode()
    {
        var transcript = new Transcript("en", [Seg("Faster, faster spins the earth", 0), Seg("Faster, faster spins the earth", 5)]);
        Assert.Equal(2, HallucinationFilter.Apply(transcript).Segments.Count);
    }

    [Fact]
    public void Strict_mode_drops_short_and_unsure_segments()
    {
        var strict = HallucinationFilterOptions.Strict;
        Assert.NotNull(HallucinationFilter.Judge(Seg("Thank you.", prob: 0.95f), strict));
        Assert.NotNull(HallucinationFilter.Judge(Seg("I pray you to come.", prob: 0.6f), strict));
        Assert.Null(HallucinationFilter.Judge(Seg("Someone says those magic words that were waiting", prob: 0.9f), strict));
        Assert.Null(HallucinationFilter.Judge(Seg("Someone says those magic words", prob: 0.9f), new HallucinationFilterOptions()));
    }

    [Fact]
    public void Youtube_outro_phrases_are_dropped()
    {
        var o = new HallucinationFilterOptions();
        Assert.NotNull(HallucinationFilter.Judge(Seg("We'll be right back."), o));
        Assert.NotNull(HallucinationFilter.Judge(Seg("We'll see you next time."), o));
    }
}

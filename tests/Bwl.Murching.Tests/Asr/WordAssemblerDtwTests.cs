using Bwl.Murching.Asr;
using Whisper.net;

namespace Bwl.Murching.Tests.Asr;

public class WordAssemblerDtwTests
{
    private static WhisperToken Tok(string text, long dtw) => new() { Text = text, Start = dtw, End = dtw + 10, Probability = 0.9f, DtwTimestamp = dtw };

    [Fact]
    public void Last_word_pinned_to_segment_end_gets_a_plausible_duration()
    {
        // DTW gives the last token the segment end as its onset -> zero length without the fix.
        var tokens = new List<WhisperToken> { Tok(" like", 460), Tok(" the", 500) };
        var words = WordAssembler.BuildWords(tokens, "like the", TimeSpan.FromSeconds(4), TimeSpan.FromSeconds(5), 0.9f, useDtw: true);
        Assert.Equal(2, words.Count);
        Assert.Equal(TimeSpan.FromSeconds(5), words[1].End);
        Assert.True(words[1].Duration >= TimeSpan.FromMilliseconds(40));
        Assert.True(words[1].Start >= words[0].End);
        Assert.True(words[1].Duration <= TimeSpan.FromMilliseconds(200));
    }

    [Fact]
    public void Words_stay_monotonic()
    {
        var tokens = new List<WhisperToken> { Tok(" a", 100), Tok(" b", 90), Tok(" c", 95), Tok(" d", 300) };
        var words = WordAssembler.BuildWords(tokens, "a b c d", TimeSpan.Zero, TimeSpan.FromSeconds(4), 0.9f, useDtw: true);
        for (var i = 1; i < words.Count; i++)
        {
            Assert.True(words[i].Start >= words[i - 1].End, $"{words[i - 1]} -> {words[i]}");
        }
    }
}

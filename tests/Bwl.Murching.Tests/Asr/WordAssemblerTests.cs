using Bwl.Murching.Asr;
using Whisper.net;

namespace Bwl.Murching.Tests.Asr;

public class WordAssemblerTests
{
    private static WhisperToken Tok(string text, long start, long end, float p = 0.9f, long dtw = -1) => new()
    {
        Text = text,
        Start = start,
        End = end,
        Probability = p,
        DtwTimestamp = dtw,
    };

    [Fact]
    public void GroupTokens_splits_on_leading_space_and_attaches_closing_punctuation()
    {
        var groups = WordAssembler.GroupTokens([" Hello", ",", " wor", "ld", "."]);
        Assert.Equal(2, groups.Count);
        Assert.Equal([0, 1], groups[0]);
        Assert.Equal([2, 3, 4], groups[1]);
    }

    [Fact]
    public void GroupTokens_opening_quote_starts_next_word()
    {
        var groups = WordAssembler.GroupTokens([" из", " «", "Бас", "ни", " Эзопа", "»", "."]);
        Assert.Equal(3, groups.Count);
        Assert.Equal([0], groups[0]);
        Assert.Equal([1, 2, 3], groups[1]);
        Assert.Equal([4, 5, 6], groups[2]);
    }

    [Fact]
    public void GroupTokens_hyphenated_word_stays_together()
    {
        var groups = WordAssembler.GroupTokens([" Ну", "-", "ка", " ты"]);
        Assert.Equal(2, groups.Count);
        Assert.Equal([0, 1, 2], groups[0]);
    }

    [Fact]
    public void BuildWords_uses_segment_text_when_counts_match()
    {
        var tokens = new List<WhisperToken>
        {
            Tok("[_BEG_]", 0, 0),
            Tok(" Hel", 0, 20),
            Tok("lo", 20, 40),
            Tok(" world", 45, 90),
            Tok(".", 90, 95),
            Tok("[_TT_100]", 100, 100),
        };
        var words = WordAssembler.BuildWords(tokens, "Hello world.", TimeSpan.Zero, TimeSpan.FromSeconds(1), 0.9f, useDtw: false);
        Assert.Equal(2, words.Count);
        Assert.Equal("Hello", words[0].Text);
        Assert.Equal(TimeSpan.Zero, words[0].Start);
        Assert.Equal(TimeSpan.FromMilliseconds(400), words[0].End);
        Assert.Equal("world.", words[1].Text);
        Assert.Equal(TimeSpan.FromMilliseconds(450), words[1].Start);
        Assert.Equal(TimeSpan.FromMilliseconds(950), words[1].End);
    }

    [Fact]
    public void BuildWords_dtw_uses_next_word_onset_as_end()
    {
        var tokens = new List<WhisperToken>
        {
            Tok(" one", 0, 30, dtw: 10),
            Tok(" two", 30, 60, dtw: 40),
            Tok(" three", 60, 90, dtw: 70),
        };
        var words = WordAssembler.BuildWords(tokens, "one two three", TimeSpan.Zero, TimeSpan.FromSeconds(1), 0.9f, useDtw: true);
        Assert.Equal(TimeSpan.FromMilliseconds(100), words[0].Start);
        Assert.Equal(TimeSpan.FromMilliseconds(400), words[0].End);
        Assert.Equal(TimeSpan.FromMilliseconds(400), words[1].Start);
        Assert.Equal(TimeSpan.FromMilliseconds(700), words[1].End);
        Assert.Equal(TimeSpan.FromSeconds(1), words[2].End);
    }

    [Fact]
    public void BuildWords_falls_back_to_even_spread_on_broken_utf8()
    {
        var tokens = new List<WhisperToken>
        {
            Tok(" �", 0, 20),
            Tok("��", 20, 40),
            Tok(" ok", 40, 60),
            Tok(" more", 60, 80),
        };
        // 4 words in text vs 3 token groups -> counts differ, token text broken -> even spread.
        var words = WordAssembler.BuildWords(tokens, "Привет мир ok more", TimeSpan.Zero, TimeSpan.FromSeconds(2), 0.5f, useDtw: false);
        Assert.Equal(["Привет", "мир", "ok", "more"], words.Select(w => w.Text));
        Assert.Equal(TimeSpan.Zero, words[0].Start);
        Assert.Equal(TimeSpan.FromSeconds(2), words[^1].End);
        Assert.True(words.Zip(words.Skip(1)).All(p => p.First.End <= p.Second.Start));
    }

    [Fact]
    public void BuildWords_without_tokens_spreads_evenly()
    {
        var words = WordAssembler.BuildWords([], "a bb ccc", TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2), 0.7f, false);
        Assert.Equal(3, words.Count);
        Assert.Equal(TimeSpan.FromSeconds(1), words[0].Start);
        Assert.Equal(TimeSpan.FromSeconds(2), words[2].End);
        Assert.All(words, w => Assert.Equal(0.7f, w.Probability));
    }

    [Fact]
    public void Convert_applies_offset_and_clamps_to_clip()
    {
        var segment = new SegmentData("  Hello   world ", TimeSpan.FromSeconds(-0.5), TimeSpan.FromSeconds(99), 0.5f, 1f, 0.9f, 0.1f, "en",
            [Tok(" Hello", 0, 50), Tok(" world", 50, 100)]);
        var result = WordAssembler.Convert(segment, TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(2), useDtw: false);
        Assert.Equal("Hello world", result.Text);
        Assert.Equal(TimeSpan.FromSeconds(10), result.Start);
        Assert.Equal(TimeSpan.FromSeconds(12), result.End);
        Assert.Equal(2, result.Words.Count);
        Assert.Equal(TimeSpan.FromSeconds(10), result.Words[0].Start);
        Assert.Equal(TimeSpan.FromSeconds(11), result.Words[1].End);
    }
}

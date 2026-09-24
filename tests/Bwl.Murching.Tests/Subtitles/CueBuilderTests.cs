using Bwl.Murching.Asr;
using Bwl.Murching.Subtitles;

namespace Bwl.Murching.Tests.Subtitles;

public class CueBuilderTests
{
    /// <summary>Builds a transcript where each word takes <paramref name="wordSeconds"/> seconds; '|' in the text marks a pause of <paramref name="pauseSeconds"/>.</summary>
    private static Transcript MakeTranscript(string text, double wordSeconds = 0.3, double pauseSeconds = 1.5, string? language = "en")
    {
        var words = new List<TranscriptWord>();
        var t = 0.0;
        foreach (var token in text.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            if (token == "|")
            {
                t += pauseSeconds;
                continue;
            }

            words.Add(new TranscriptWord(token, TimeSpan.FromSeconds(t), TimeSpan.FromSeconds(t + wordSeconds), 0.9f));
            t += wordSeconds;
        }

        var segment = new TranscriptSegment(string.Join(' ', words.Select(w => w.Text)), words[0].Start, words[^1].End, words, 0.9f, 0f);
        return new Transcript(language, [segment]);
    }

    private static string Flat(SubtitleCue cue) => string.Join(' ', cue.Lines);

    private static void AssertWellFormed(SubtitleDocument doc, CueBuilderOptions o)
    {
        for (var i = 0; i < doc.Cues.Count; i++)
        {
            var cue = doc.Cues[i];
            Assert.Equal(i + 1, cue.Index);
            Assert.True(cue.End > cue.Start, $"cue {i} has non-positive duration");
            Assert.True(cue.Duration <= o.MaxDuration + TimeSpan.FromMilliseconds(1), $"cue {i} too long: {cue.Duration}");
            Assert.True(cue.Lines.Count <= o.MaxLines, $"cue {i} has {cue.Lines.Count} lines");
            Assert.All(cue.Lines, l => Assert.True(l.Length <= o.MaxLineLength || !l.Contains(' '), $"line too long: '{l}'"));
            if (i > 0)
            {
                Assert.True(cue.Start - doc.Cues[i - 1].End >= o.MinGap - TimeSpan.FromMilliseconds(1), $"cue {i} overlaps/gap too small");
            }
        }
    }

    [Fact]
    public void Single_short_sentence_is_one_cue()
    {
        var doc = CueBuilder.Build(MakeTranscript("The quick brown fox jumps over the lazy dog."));
        var cue = Assert.Single(doc.Cues);
        Assert.Equal("The quick brown fox jumps over the lazy dog.", Flat(cue));
        Assert.Equal("en", doc.Language);
    }

    [Fact]
    public void Long_pause_forces_new_cue()
    {
        var doc = CueBuilder.Build(MakeTranscript("Hello there | and welcome back"));
        Assert.Equal(2, doc.Cues.Count);
        Assert.Equal("Hello there", Flat(doc.Cues[0]));
        Assert.Equal("and welcome back", Flat(doc.Cues[1]));
    }

    [Fact]
    public void Two_medium_sentences_are_not_merged_when_they_cannot_share_a_line_break()
    {
        var doc = CueBuilder.Build(MakeTranscript("Басня номер один из «Басни Эзопа». Эта звукозапись сделана для сайта LibriVox."));
        Assert.Equal(2, doc.Cues.Count);
        Assert.Equal("Басня номер один из «Басни Эзопа».", Flat(doc.Cues[0]));
        Assert.Equal("Эта звукозапись сделана для сайта LibriVox.", Flat(doc.Cues[1]));
    }

    [Fact]
    public void Tiny_sentences_are_merged_into_one_cue()
    {
        var doc = CueBuilder.Build(MakeTranscript("Yes. No. Maybe. I don't know."));
        var cue = Assert.Single(doc.Cues);
        Assert.Equal("Yes. No. Maybe. I don't know.", Flat(cue));
    }

    [Fact]
    public void Long_sentence_is_split_at_clause_punctuation()
    {
        var text = "Для более подробной информации или регистрации в качестве волонтера, пожалуйста, посетите сайт проекта LibriVox точка org";
        var doc = CueBuilder.Build(MakeTranscript(text));
        Assert.True(doc.Cues.Count >= 2);
        Assert.EndsWith(",", doc.Cues[0].Lines[^1]);
        AssertWellFormed(doc, new CueBuilderOptions());
    }

    [Fact]
    public void Respects_limits_on_long_monologue()
    {
        var text = string.Join(' ', Enumerable.Range(1, 200).Select(i => i % 13 == 0 ? $"word{i}." : $"word{i}"));
        var options = new CueBuilderOptions();
        var doc = CueBuilder.Build(MakeTranscript(text, wordSeconds: 0.35), options);
        AssertWellFormed(doc, options);
        Assert.Equal(text, string.Join(' ', doc.Cues.SelectMany(c => c.Lines)));
    }

    [Fact]
    public void Cue_is_extended_for_reading_speed_but_not_into_next_cue()
    {
        // 44 chars spoken in 0.6 s: needs ~2.1 s at 21 cps, but the next cue starts 1 s later.
        var doc = CueBuilder.Build(MakeTranscript("Extraordinarily quickly spoken subtitle text. | Next", wordSeconds: 0.15, pauseSeconds: 1.0));
        Assert.Equal(2, doc.Cues.Count);
        var o = new CueBuilderOptions();
        Assert.True(doc.Cues[0].End <= doc.Cues[1].Start - o.MinGap + TimeSpan.FromMilliseconds(1));
        Assert.True(doc.Cues[0].Duration > TimeSpan.FromSeconds(0.6));
    }

    [Fact]
    public void Last_cue_gets_tail_padding_and_min_duration()
    {
        var doc = CueBuilder.Build(MakeTranscript("Bye", wordSeconds: 0.2));
        var cue = Assert.Single(doc.Cues);
        Assert.True(cue.Duration >= TimeSpan.FromSeconds(1));
    }

    [Fact]
    public void Segments_without_words_fall_back_to_segment_text()
    {
        var seg = new TranscriptSegment("Fallback text here.", TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(3), [], 0.8f, 0f);
        var doc = CueBuilder.Build(new Transcript("en", [seg]));
        var cue = Assert.Single(doc.Cues);
        Assert.Equal("Fallback text here.", cue.Text);
        Assert.Equal(TimeSpan.FromSeconds(1), cue.Start);
    }

    [Fact]
    public void Overlong_single_word_does_not_crash()
    {
        var doc = CueBuilder.Build(MakeTranscript("Supercalifragilisticexpialidociouswordthatiswaytoolongforanyline yes"), new CueBuilderOptions { MaxLineLength = 20 });
        Assert.NotEmpty(doc.Cues);
    }

    [Fact]
    public void Non_monotonic_word_times_are_repaired()
    {
        var words = new List<TranscriptWord>
        {
            new("one", TimeSpan.FromSeconds(1.0), TimeSpan.FromSeconds(1.5), 1f),
            new("two", TimeSpan.FromSeconds(1.2), TimeSpan.FromSeconds(1.1), 1f),
            new("three", TimeSpan.FromSeconds(1.6), TimeSpan.FromSeconds(2.0), 1f),
        };
        var seg = new TranscriptSegment("one two three", words[0].Start, words[^1].End, words, 1f, 0f);
        var doc = CueBuilder.Build(new Transcript("en", [seg]));
        var cue = Assert.Single(doc.Cues);
        Assert.Equal(TimeSpan.FromSeconds(1.0), cue.Start);
        Assert.True(cue.End > cue.Start);
    }

    [Fact]
    public void Dialogue_dash_starts_a_new_line()
    {
        var doc = CueBuilder.Build(MakeTranscript("- Ты идёшь? - Нет."));
        var cue = Assert.Single(doc.Cues);
        Assert.Equal(["- Ты идёшь?", "- Нет."], cue.Lines);
    }
}

using Bwl.Murching.Asr;
using Bwl.Murching.Subtitles;

namespace Bwl.Murching.Tests.Subtitles;

/// <summary>Cases distilled from real Whisper output (JFK Rice University speech) that produced ugly cues.</summary>
public class CueBuilderRegressionTests
{
    private static TranscriptSegment Seg(double start, string text)
    {
        var words = new List<TranscriptWord>();
        var t = start;
        foreach (var w in text.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            words.Add(new TranscriptWord(w, TimeSpan.FromSeconds(t), TimeSpan.FromSeconds(t + 0.32), 0.9f));
            t += 0.32;
        }

        return new TranscriptSegment(text, words[0].Start, words[^1].End, words, 0.85f, 0f);
    }

    private static IReadOnlyList<string> Flat(SubtitleDocument doc) => doc.Cues.Select(c => string.Join(' ', c.Lines)).ToList();

    [Fact]
    public void Whisper_segment_end_mid_clause_does_not_win_over_clause_punctuation()
    {
        // Whisper cut "alumnus | of Rice University" mid-clause; the cue must break at the commas / full stop instead.
        var transcript = new Transcript("en",
        [
            Seg(0.0, "But for my present position today,"),
            Seg(2.0, "I introduce as a very distinguished alumnus"),
            Seg(4.3, "of Rice University, the Honorable Albert Thomas."),
            Seg(6.6, "Won't you say a word again?"),
        ]);
        var doc = CueBuilder.Build(transcript);
        var texts = Flat(doc);
        Assert.DoesNotContain(texts, t => t.EndsWith("alumnus", StringComparison.Ordinal));
        Assert.All(doc.Cues, c => Assert.False(TextRules.IsFunctionWord(c.Lines[0].Split(' ')[^1]) && c.Lines.Count > 1, $"line ends with a function word: {c}"));
        Assert.Contains(doc.Cues.SelectMany(c => c.Lines), line => line.EndsWith("Thomas.", StringComparison.Ordinal));
    }

    [Fact]
    public void Function_word_at_whisper_segment_end_is_not_a_cue_boundary()
    {
        var transcript = new Transcript("en",
        [
            Seg(0.0, "wonderful to be at home then as no one could fill this stadium like the"),
            Seg(4.9, "president of the United States have. Let's give him a big hand when he's here."),
        ]);
        var doc = CueBuilder.Build(transcript);
        var texts = Flat(doc);
        Assert.DoesNotContain(texts, t => t.EndsWith(" the", StringComparison.Ordinal));
        Assert.Contains(texts, t => t.EndsWith("have.", StringComparison.Ordinal));
    }

    [Fact]
    public void Two_sentences_that_cannot_wrap_at_the_full_stop_get_separate_cues()
    {
        var transcript = new Transcript("en", [Seg(0.0, "of Rice University, the Honorable Albert Thomas. Won't you say a word again?")]);
        var doc = CueBuilder.Build(transcript);
        Assert.Equal(2, doc.Cues.Count);
        Assert.Equal("of Rice University, the Honorable Albert Thomas.", Flat(doc)[0]);
    }
}

using Bwl.Murching.Asr;

namespace Bwl.Murching.Tests.Asr;

public class HallucinationFilterTests
{
    private static TranscriptSegment Seg(string text, float prob = 0.9f, float noSpeech = 0.05f) =>
        new(text, TimeSpan.Zero, TimeSpan.FromSeconds(2), [], prob, noSpeech);

    [Theory]
    [InlineData("Субтитры сделал DimaTorzok")]
    [InlineData("Продолжение следует...")]
    [InlineData("Thanks for watching!")]
    [InlineData("Subtitles by the Amara.org community")]
    public void Known_credit_lines_are_dropped(string text) =>
        Assert.NotNull(HallucinationFilter.Judge(Seg(text), new HallucinationFilterOptions()));

    [Theory]
    [InlineData("[Music]")]
    [InlineData("♪♪♪")]
    [InlineData("(applause)")]
    [InlineData("...")]
    public void Sound_markers_without_words_are_dropped(string text) =>
        Assert.NotNull(HallucinationFilter.Judge(Seg(text), new HallucinationFilterOptions()));

    [Fact]
    public void Repetition_loops_are_dropped()
    {
        var text = string.Join(' ', Enumerable.Repeat("да да да", 6));
        Assert.NotNull(HallucinationFilter.Judge(Seg(text), new HallucinationFilterOptions()));
    }

    [Fact]
    public void Normal_speech_is_kept()
    {
        var text = "Подавился волк костью и не мог выперхнуть. Он подозвал журавля и сказал.";
        Assert.Null(HallucinationFilter.Judge(Seg(text), new HallucinationFilterOptions()));
    }

    [Fact]
    public void Low_confidence_no_speech_is_dropped_but_confident_kept()
    {
        var options = new HallucinationFilterOptions();
        Assert.NotNull(HallucinationFilter.Judge(Seg("hmm yes", prob: 0.3f, noSpeech: 0.9f), options));
        Assert.Null(HallucinationFilter.Judge(Seg("hmm yes", prob: 0.9f, noSpeech: 0.9f), options));
    }

    [Fact]
    public void Apply_keeps_metadata_and_order()
    {
        var transcript = new Transcript("ru", [Seg("Нормальная речь идёт здесь."), Seg("Субтитры сделал DimaTorzok"), Seg("И ещё одна фраза.")]) { Model = "tiny" };
        var filtered = HallucinationFilter.Apply(transcript);
        Assert.Equal(2, filtered.Segments.Count);
        Assert.Equal("tiny", filtered.Model);
        Assert.Equal("ru", filtered.Language);
    }

    [Fact]
    public void Disabled_filter_is_identity()
    {
        var transcript = new Transcript("en", [Seg("[Music]")]);
        Assert.Same(transcript, HallucinationFilter.Apply(transcript, new HallucinationFilterOptions { Enabled = false }));
    }
}

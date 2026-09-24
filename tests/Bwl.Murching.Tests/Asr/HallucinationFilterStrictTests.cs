using Bwl.Murching.Asr;

namespace Bwl.Murching.Tests.Asr;

public class HallucinationFilterStrictTests
{
    private static TranscriptSegment Seg(string text, float prob = 0.9f) =>
        new(text, TimeSpan.Zero, TimeSpan.FromSeconds(2), text.Split(' ').Select((w, i) => new TranscriptWord(w, TimeSpan.FromSeconds(i * 0.3), TimeSpan.FromSeconds(i * 0.3 + 0.3), prob)).ToList(), prob, 0f);

    [Theory]
    [InlineData("I'm going to go.")]
    [InlineData("Thank you very much.")]
    [InlineData("Oh my God!")]
    public void Strict_mode_drops_stock_music_hallucinations(string text) =>
        Assert.NotNull(HallucinationFilter.Judge(Seg(text), HallucinationFilterOptions.Strict));

    [Theory]
    [InlineData("I'm going to go.")]
    [InlineData("Thank you very much.")]
    public void Normal_mode_keeps_them(string text) =>
        Assert.Null(HallucinationFilter.Judge(Seg(text), new HallucinationFilterOptions()));

    [Fact]
    public void Strict_mode_keeps_real_lyrics()
    {
        Assert.Null(HallucinationFilter.Judge(Seg("Rent the guns, still get shot down", 0.78f), HallucinationFilterOptions.Strict));
        Assert.Null(HallucinationFilter.Judge(Seg("Надоел ВБ, мы хотим Озон.", 0.85f), HallucinationFilterOptions.Strict));
    }
}

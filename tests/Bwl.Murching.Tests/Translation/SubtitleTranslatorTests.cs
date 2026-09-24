using Bwl.Murching.Subtitles;
using Bwl.Murching.Translation;

namespace Bwl.Murching.Tests.Translation;

public class SubtitleTranslatorTests
{
    private sealed class FakeTranslator : ITranslator
    {
        public List<int> BatchSizes { get; } = [];

        public List<int> ContextSizes { get; } = [];

        public string Name => "fake";

        public Task<IReadOnlyList<string>> TranslateBatchAsync(IReadOnlyList<string> lines, IReadOnlyList<string> previousTranslations, CancellationToken ct = default)
        {
            BatchSizes.Add(lines.Count);
            ContextSizes.Add(previousTranslations.Count);
            return Task.FromResult<IReadOnlyList<string>>(lines.Select(l => "RU:" + l).ToList());
        }

        public void Dispose()
        {
        }
    }

    private static SubtitleDocument Doc(int n) => new(Enumerable.Range(0, n)
        .Select(i => new SubtitleCue(i + 1, TimeSpan.FromSeconds(i * 2), TimeSpan.FromSeconds(i * 2 + 1.5), [$"line {i}", "second"]))
        .ToList())
    { Language = "en" };

    [Fact]
    public async Task Translates_in_batches_with_rolling_context_and_keeps_timing()
    {
        var doc = Doc(50);
        var fake = new FakeTranslator();
        var result = await SubtitleTranslator.TranslateAsync(doc, fake, "ru", batchSize: 20, contextLines: 4);

        Assert.Equal([20, 20, 10], fake.BatchSizes);
        Assert.Equal([0, 4, 4], fake.ContextSizes);
        Assert.Equal(50, result.Cues.Count);
        Assert.Equal("ru", result.Language);
        Assert.Equal(doc.Cues[7].Start, result.Cues[7].Start);
        Assert.Equal(doc.Cues[7].End, result.Cues[7].End);
        Assert.Equal("RU:line 7 second", string.Join(' ', result.Cues[7].Lines));
    }

    [Fact]
    public async Task Long_translations_are_rewrapped()
    {
        var doc = new SubtitleDocument([new SubtitleCue(1, TimeSpan.Zero, TimeSpan.FromSeconds(3), ["short"])]);
        var translator = new LongTranslator();
        var result = await SubtitleTranslator.TranslateAsync(doc, translator, "ru", new CueBuilderOptions { MaxLineLength = 24, MaxLines = 2 });
        var cue = Assert.Single(result.Cues);
        Assert.Equal(2, cue.Lines.Count);
        Assert.All(cue.Lines, l => Assert.True(l.Length <= 24, l));
    }

    [Fact]
    public void Bilingual_merges_lines()
    {
        var original = Doc(2);
        var translated = new SubtitleDocument(original.Cues.Select(c => c with { Lines = ["перевод"] }).ToList()) { Language = "ru" };
        var bi = SubtitleTranslator.Bilingual(original, translated);
        Assert.Equal("en-ru", bi.Language);
        Assert.Equal(["line 0", "second", "перевод"], bi.Cues[0].Lines);
    }

    private sealed class LongTranslator : ITranslator
    {
        public string Name => "long";

        public Task<IReadOnlyList<string>> TranslateBatchAsync(IReadOnlyList<string> lines, IReadOnlyList<string> previousTranslations, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<string>>(lines.Select(_ => "очень длинный перевод который не влезает").ToList());

        public void Dispose()
        {
        }
    }
}

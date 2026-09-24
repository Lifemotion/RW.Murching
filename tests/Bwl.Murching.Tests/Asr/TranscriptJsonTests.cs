using Bwl.Murching.Asr;

namespace Bwl.Murching.Tests.Asr;

public class TranscriptJsonTests
{
    [Fact]
    public void Roundtrip_preserves_segments_words_and_metadata()
    {
        var words = new List<TranscriptWord>
        {
            new("Привет,", TimeSpan.FromMilliseconds(1000), TimeSpan.FromMilliseconds(1400), 0.95f),
            new("мир!", TimeSpan.FromMilliseconds(1450), TimeSpan.FromMilliseconds(1900), 0.8f),
        };
        var transcript = new Transcript("ru", [new TranscriptSegment("Привет, мир!", words[0].Start, words[^1].End, words, 0.9f, 0.02f)])
        {
            Model = "large-v3-turbo",
            SourceDuration = TimeSpan.FromSeconds(73.137),
        };

        var json = TranscriptJson.Serialize(transcript);
        Assert.Contains("\"language\": \"ru\"", json);
        Assert.Contains("Привет", json); // not \u-escaped

        var back = TranscriptJson.Deserialize(json);
        Assert.Equal("ru", back.Language);
        Assert.Equal("large-v3-turbo", back.Model);
        Assert.Equal(TimeSpan.FromSeconds(73.137), back.SourceDuration);
        var seg = Assert.Single(back.Segments);
        Assert.Equal("Привет, мир!", seg.Text);
        Assert.Equal(2, seg.Words.Count);
        Assert.Equal(TimeSpan.FromMilliseconds(1450), seg.Words[1].Start);
        Assert.Equal(0.8f, seg.Words[1].Probability, 3);
    }

    [Fact]
    public async Task File_roundtrip()
    {
        var path = Path.Combine(Path.GetTempPath(), $"murch-{Guid.NewGuid():N}.murch.json");
        try
        {
            var transcript = new Transcript("en", [new TranscriptSegment("Hi.", TimeSpan.Zero, TimeSpan.FromSeconds(1), [], 1f, 0f)]);
            await TranscriptJson.WriteAsync(path, transcript);
            var back = await TranscriptJson.ReadAsync(path);
            Assert.Equal("Hi.", back.Segments[0].Text);
        }
        finally
        {
            File.Delete(path);
        }
    }
}

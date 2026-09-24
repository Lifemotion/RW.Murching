using Bwl.Murching.Vad;

namespace Bwl.Murching.Tests.Vad;

public class SpeechChunkerTests
{
    private static SpeechSegment S(double start, double end) => new(TimeSpan.FromSeconds(start), TimeSpan.FromSeconds(end));

    [Fact]
    public void Empty_input_gives_no_chunks() =>
        Assert.Empty(SpeechChunker.Chunk([], TimeSpan.FromSeconds(60)));

    [Fact]
    public void Adjacent_segments_are_merged_up_to_max_duration()
    {
        var segments = new[] { S(0, 5), S(5.5, 12), S(12.5, 20), S(20.5, 29) };
        var chunks = SpeechChunker.Chunk(segments, TimeSpan.FromSeconds(60), new ChunkingOptions { MaxChunkDuration = TimeSpan.FromSeconds(30), Padding = TimeSpan.Zero });
        var chunk = Assert.Single(chunks);
        Assert.Equal(S(0, 29), chunk);
    }

    [Fact]
    public void Merging_stops_at_max_duration()
    {
        var segments = new[] { S(0, 15), S(15.5, 29), S(29.5, 40) };
        var chunks = SpeechChunker.Chunk(segments, TimeSpan.FromSeconds(60), new ChunkingOptions { MaxChunkDuration = TimeSpan.FromSeconds(30), Padding = TimeSpan.Zero });
        Assert.Equal(2, chunks.Count);
        Assert.Equal(S(0, 29), chunks[0]);
        Assert.Equal(S(29.5, 40), chunks[1]);
    }

    [Fact]
    public void Long_gap_prevents_merge()
    {
        var segments = new[] { S(0, 5), S(9, 12) };
        var chunks = SpeechChunker.Chunk(segments, TimeSpan.FromSeconds(60), new ChunkingOptions { MaxGap = TimeSpan.FromSeconds(2), Padding = TimeSpan.Zero });
        Assert.Equal(2, chunks.Count);
    }

    [Fact]
    public void Oversized_segment_is_split_evenly()
    {
        var chunks = SpeechChunker.Chunk([S(0, 70)], TimeSpan.FromSeconds(100), new ChunkingOptions { MaxChunkDuration = TimeSpan.FromSeconds(30), Padding = TimeSpan.Zero });
        Assert.Equal(3, chunks.Count);
        Assert.All(chunks, c => Assert.True(c.Duration <= TimeSpan.FromSeconds(30)));
        Assert.Equal(TimeSpan.Zero, chunks[0].Start);
        Assert.Equal(TimeSpan.FromSeconds(70), chunks[^1].End);
    }

    [Fact]
    public void Padding_is_applied_and_clamped_without_overlap()
    {
        var segments = new[] { S(0.1, 5), S(5.3, 10), S(59.9, 60) };
        var chunks = SpeechChunker.Chunk(segments, TimeSpan.FromSeconds(60), new ChunkingOptions { MaxGap = TimeSpan.FromMilliseconds(100), Padding = TimeSpan.FromMilliseconds(250) });
        Assert.Equal(3, chunks.Count);
        Assert.Equal(TimeSpan.Zero, chunks[0].Start);
        Assert.True(chunks[0].End <= chunks[1].Start);
        Assert.Equal(TimeSpan.FromSeconds(60), chunks[2].End);
    }
}

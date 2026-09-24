using Bwl.Murching.Subtitles;

namespace Bwl.Murching.Tests.Subtitles;

public class SrtParserTests
{
    [Fact]
    public void Parses_srt_with_multiline_cues_and_tags()
    {
        var text = "1\n00:00:01,000 --> 00:00:03,500\nHello <i>world</i>\nsecond line\n\n2\n00:01:04,000 --> 01:00:01,250\nПривет\n\n";
        var doc = SrtParser.Parse(text);
        Assert.Equal(2, doc.Cues.Count);
        Assert.Equal(TimeSpan.FromMilliseconds(1000), doc.Cues[0].Start);
        Assert.Equal(TimeSpan.FromMilliseconds(3500), doc.Cues[0].End);
        Assert.Equal(["Hello world", "second line"], doc.Cues[0].Lines);
        Assert.Equal(new TimeSpan(1, 0, 1) + TimeSpan.FromMilliseconds(250), doc.Cues[1].End);
    }

    [Fact]
    public void Parses_vtt_header_and_dot_millis()
    {
        var text = "WEBVTT\nLanguage: en\n\n00:00:00.500 --> 00:00:02.000\nHi\n";
        var doc = SrtParser.Parse(text);
        var cue = Assert.Single(doc.Cues);
        Assert.Equal(TimeSpan.FromMilliseconds(500), cue.Start);
        Assert.Equal("Hi", cue.Text);
    }

    [Fact]
    public void Roundtrip_through_writer()
    {
        var original = new SubtitleDocument([
            new SubtitleCue(1, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2), ["a", "b"]),
            new SubtitleCue(2, TimeSpan.FromSeconds(3), TimeSpan.FromSeconds(4.75), ["c"]),
        ]);
        var back = SrtParser.Parse(SubtitleWriters.ToText(original, SubtitleFormat.Srt));
        Assert.Equal(original.Cues.Select(c => (c.Start, c.End, c.Text)), back.Cues.Select(c => (c.Start, c.End, c.Text)));
    }

    [Fact]
    public void Language_is_taken_from_file_name()
    {
        var path = Path.Combine(Path.GetTempPath(), $"talk-{Guid.NewGuid():N}.en.srt");
        File.WriteAllText(path, "1\n00:00:00,000 --> 00:00:01,000\nx\n");
        try
        {
            Assert.Equal("en", SrtParser.ParseFile(path).Language);
        }
        finally
        {
            File.Delete(path);
        }
    }
}

using Bwl.Murching.Subtitles;

namespace Bwl.Murching.Tests.Subtitles;

public class SubtitleWritersTests
{
    private static SubtitleDocument Sample() => new(
    [
        new SubtitleCue(1, TimeSpan.FromMilliseconds(1_000), TimeSpan.FromMilliseconds(3_500), ["Hello <world>", "& friends"]),
        new SubtitleCue(2, TimeSpan.FromMilliseconds(4_000), TimeSpan.FromMilliseconds(3_600_000 + 1_250), ["Привет, {мир}!"]),
    ])
    { Language = "en", Title = "Test" };

    [Fact]
    public void Srt_output_is_exact()
    {
        var expected =
            "1\n00:00:01,000 --> 00:00:03,500\nHello <world>\n& friends\n\n" +
            "2\n00:00:04,000 --> 01:00:01,250\nПривет, {мир}!\n\n";
        Assert.Equal(expected, SubtitleWriters.ToText(Sample(), SubtitleFormat.Srt));
    }

    [Fact]
    public void Vtt_output_has_header_and_escapes_markup()
    {
        var text = SubtitleWriters.ToText(Sample(), SubtitleFormat.Vtt);
        Assert.StartsWith("WEBVTT\nLanguage: en\n\n", text);
        Assert.Contains("00:00:01.000 --> 00:00:03.500\nHello &lt;world&gt;\n&amp; friends\n", text);
    }

    [Fact]
    public void Ass_output_has_styles_and_escapes_braces()
    {
        var text = SubtitleWriters.ToText(Sample(), SubtitleFormat.Ass);
        Assert.Contains("[V4+ Styles]", text);
        Assert.Contains("Dialogue: 0,0:00:01.00,0:00:03.50,Default,,0,0,0,,Hello <world>\\N& friends", text);
        Assert.Contains("Привет, ｛мир｝!", text);
    }

    [Fact]
    public void Txt_output_has_timecodes_and_joins_lines_per_cue()
    {
        Assert.Equal("[   1.00 ->    3.50]  Hello <world> & friends\n[   4.00 -> 3601.25]  Привет, {мир}!\n", SubtitleWriters.ToText(Sample(), SubtitleFormat.Txt));
    }

    [Theory]
    [InlineData("srt", SubtitleFormat.Srt)]
    [InlineData(".SRT", SubtitleFormat.Srt)]
    [InlineData("webvtt", SubtitleFormat.Vtt)]
    [InlineData("ssa", SubtitleFormat.Ass)]
    [InlineData("text", SubtitleFormat.Txt)]
    public void TryParseFormat_accepts_aliases(string text, SubtitleFormat expected)
    {
        Assert.True(SubtitleWriters.TryParseFormat(text, out var format));
        Assert.Equal(expected, format);
    }

    [Fact]
    public void TryParseFormat_rejects_unknown() => Assert.False(SubtitleWriters.TryParseFormat("docx", out _));

    [Fact]
    public void Write_creates_utf8_file_without_bom()
    {
        var path = Path.Combine(Path.GetTempPath(), $"murch-{Guid.NewGuid():N}.srt");
        try
        {
            SubtitleWriters.Write(path, Sample(), SubtitleFormat.Srt);
            var bytes = File.ReadAllBytes(path);
            Assert.False(bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF, "BOM present");
            Assert.Equal(SubtitleWriters.ToText(Sample(), SubtitleFormat.Srt), File.ReadAllText(path));
        }
        finally
        {
            File.Delete(path);
        }
    }
}

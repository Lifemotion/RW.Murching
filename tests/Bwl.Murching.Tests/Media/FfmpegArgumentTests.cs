using Bwl.Murching.Media;

namespace Bwl.Murching.Tests.Media;

public class FfmpegArgumentTests
{
    [Fact]
    public void Extract_arguments_default()
    {
        var args = AudioExtractor.BuildArguments("in.mp4", new AudioExtractOptions(), "f32le", "pipe:1");
        Assert.Equal(["-hide_banner", "-nostdin", "-loglevel", "error", "-i", "in.mp4", "-map", "0:a:0?", "-vn", "-sn", "-dn", "-ac", "1", "-ar", "16000", "-f", "f32le", "pipe:1"], args);
    }

    [Fact]
    public void Extract_arguments_with_range_and_stream()
    {
        var args = AudioExtractor.BuildArguments("in.mkv", new AudioExtractOptions
        {
            AudioStreamIndex = 2,
            Start = TimeSpan.FromSeconds(90.5),
            End = TimeSpan.FromSeconds(120),
            SampleRate = 22050,
        }, "wav", "out.wav", overwrite: true, codec: "pcm_s16le");

        Assert.Contains("-y", args);
        var ss = Array.IndexOf(args.ToArray(), "-ss");
        Assert.Equal("90.5", args[ss + 1]);
        Assert.True(ss < args.IndexOf("-i"), "-ss must precede -i for fast seeking");
        var t = args.IndexOf("-t");
        Assert.Equal("29.5", args[t + 1]);
        Assert.Contains("0:a:2", args);
        Assert.Contains("22050", args);
        Assert.Contains("pcm_s16le", args);
        Assert.Equal("out.wav", args[^1]);
    }

    [Fact]
    public void Filter_path_escaping_for_subtitles_filter()
    {
        var escaped = SubtitleMuxer.EscapeFilterPath(@"C:\Users\me\it's.srt");
        Assert.DoesNotContain('\\' + "U", escaped);
        Assert.Contains("C\\:/Users/me/it\\'s.srt", escaped);
    }

    [Theory]
    [InlineData(@"C:\v\movie.mp4", @"C:\v\movie.subbed.mp4")]
    [InlineData(@"C:\v\movie.mkv", @"C:\v\movie.subbed.mkv")]
    [InlineData(@"C:\v\movie.avi", @"C:\v\movie.subbed.mkv")]
    public void Muxer_default_output(string input, string expected) =>
        Assert.Equal(expected, SubtitleMuxer.DefaultOutputPath(input));

    [Theory]
    [InlineData("ru", "rus")]
    [InlineData("EN", "eng")]
    [InlineData("xx", "xx")]
    [InlineData(null, "und")]
    public void Iso639_mapping(string? code, string expected) =>
        Assert.Equal(expected, SubtitleMuxer.ToIso639Part2(code));
}

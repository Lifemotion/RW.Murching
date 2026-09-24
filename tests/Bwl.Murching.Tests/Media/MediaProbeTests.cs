using Bwl.Murching.Media;

namespace Bwl.Murching.Tests.Media;

public class MediaProbeTests
{
    private const string SampleJson = """
        {
          "streams": [
            { "index": 0, "codec_name": "h264", "codec_type": "video", "width": 854, "height": 480, "r_frame_rate": "24/1", "avg_frame_rate": "24000/1001", "duration": "52.208333", "disposition": { "default": 1 } },
            { "index": 1, "codec_name": "aac", "codec_type": "audio", "sample_rate": "48000", "channels": 2, "channel_layout": "stereo", "tags": { "language": "eng", "title": "Stereo" }, "disposition": { "default": 1 } },
            { "index": 2, "codec_name": "subrip", "codec_type": "subtitle", "tags": { "language": "rus" }, "disposition": { "default": 0 } }
          ],
          "format": { "format_name": "mov,mp4,m4a,3gp,3g2,mj2", "format_long_name": "QuickTime / MOV", "duration": "52.208333", "size": "4372373", "bit_rate": "669987" }
        }
        """;

    [Fact]
    public void Parses_streams_and_format()
    {
        var info = MediaProbe.Parse("x.mp4", SampleJson);
        Assert.Equal(TimeSpan.FromSeconds(52.208333), info.Duration);
        Assert.Equal(4_372_373, info.SizeBytes);
        Assert.Equal(669_987, info.BitRate);
        Assert.Equal(3, info.Streams.Count);
        Assert.True(info.HasVideo);
        Assert.True(info.HasAudio);

        var video = info.VideoStreams.Single();
        Assert.Equal(854, video.Width);
        Assert.Equal(23.976, video.FrameRate!.Value, 3);
        Assert.True(video.IsDefault);

        var audio = info.AudioStreams.Single();
        Assert.Equal(48000, audio.SampleRate);
        Assert.Equal(2, audio.Channels);
        Assert.Equal("eng", audio.Language);
        Assert.Equal("Stereo", audio.Title);

        var sub = info.SubtitleStreams.Single();
        Assert.Equal("rus", sub.Language);
        Assert.False(sub.IsDefault);
    }

    [Fact]
    public void Error_object_throws()
    {
        var ex = Assert.Throws<InvalidDataException>(() => MediaProbe.Parse("x", """{ "error": { "code": -2, "string": "No such file or directory" } }"""));
        Assert.Contains("No such file", ex.Message);
    }

    [Fact]
    public void Cover_art_is_not_video()
    {
        var json = """{ "streams": [ { "index": 0, "codec_name": "mp3", "codec_type": "audio", "sample_rate": "44100", "channels": 2 }, { "index": 1, "codec_name": "mjpeg", "codec_type": "video", "width": 500, "height": 500, "avg_frame_rate": "0/0", "r_frame_rate": "90000/1" } ], "format": { "duration": "10" } }""";
        var info = MediaProbe.Parse("x.mp3", json);
        Assert.True(info.HasAudio);
        Assert.False(info.HasVideo);
    }

    [Theory]
    [InlineData("24000/1001", 23.976)]
    [InlineData("25/1", 25.0)]
    [InlineData("0/0", null)]
    [InlineData("", null)]
    [InlineData("29.97", 29.97)]
    public void ParseRational(string text, double? expected)
    {
        var actual = MediaProbe.ParseRational(text);
        if (expected is null)
        {
            Assert.Null(actual);
        }
        else
        {
            Assert.Equal(expected.Value, actual!.Value, 3);
        }
    }
}

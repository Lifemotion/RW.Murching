using Bwl.Murching.Common;

namespace Bwl.Murching.Tests.Common;

public class TimeFormatTests
{
    [Theory]
    [InlineData(0, "00:00:00,000")]
    [InlineData(1_500, "00:00:01,500")]
    [InlineData(61_001, "00:01:01,001")]
    [InlineData(3_600_000 + 5 * 60_000 + 7_000 + 89, "01:05:07,089")]
    [InlineData(-10, "00:00:00,000")]
    public void Srt_formats_hours_minutes_seconds_comma_millis(int ms, string expected) =>
        Assert.Equal(expected, TimeFormat.Srt(TimeSpan.FromMilliseconds(ms)));

    [Fact]
    public void Vtt_uses_dot_separator() =>
        Assert.Equal("00:00:01.250", TimeFormat.Vtt(TimeSpan.FromMilliseconds(1250)));

    [Theory]
    [InlineData(0, "0:00:00.00")]
    [InlineData(1_234, "0:00:01.23")]
    [InlineData(1_235, "0:00:01.24")]
    [InlineData(3_661_000, "1:01:01.00")]
    public void Ass_uses_centiseconds(int ms, string expected) =>
        Assert.Equal(expected, TimeFormat.Ass(TimeSpan.FromMilliseconds(ms)));

    [Theory]
    [InlineData(12_300, "12.3s")]
    [InlineData(65_000, "1:05")]
    [InlineData(3_725_000, "1:02:05")]
    public void Human_is_compact(int ms, string expected) =>
        Assert.Equal(expected, TimeFormat.Human(TimeSpan.FromMilliseconds(ms)));

    [Fact]
    public void Srt_rounds_to_nearest_millisecond() =>
        Assert.Equal("00:00:00,001", TimeFormat.Srt(TimeSpan.FromTicks(6_000))); // 0.6 ms
}

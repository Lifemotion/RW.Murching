using Bwl.Murching.Audio;

namespace Bwl.Murching.Tests.Audio;

public class WavFileTests
{
    private static PcmAudio Sine(int samples, int rate = 16000, double hz = 440)
    {
        var buf = new float[samples];
        for (var i = 0; i < samples; i++)
        {
            buf[i] = (float)(0.5 * Math.Sin(2 * Math.PI * hz * i / rate));
        }

        return new PcmAudio(buf, rate);
    }

    [Theory]
    [InlineData(WavSampleFormat.Int16, 1e-4)]
    [InlineData(WavSampleFormat.Float32, 1e-7)]
    public void Roundtrip(WavSampleFormat format, double tolerance)
    {
        var audio = Sine(1600);
        using var ms = new MemoryStream();
        WavFile.Write(ms, audio, format);
        var back = WavFile.Parse(ms.ToArray());
        Assert.Equal(audio.SampleRate, back.SampleRate);
        Assert.Equal(audio.Length, back.Length);
        for (var i = 0; i < audio.Length; i += 37)
        {
            Assert.Equal(audio.Samples.Span[i], back.Samples.Span[i], tolerance);
        }
    }

    [Fact]
    public void PcmAudio_slice_and_time_math()
    {
        var audio = Sine(32000);
        Assert.Equal(TimeSpan.FromSeconds(2), audio.Duration);
        var slice = audio.Slice(TimeSpan.FromSeconds(0.5), TimeSpan.FromSeconds(1.5));
        Assert.Equal(16000, slice.Length);
        Assert.Equal(TimeSpan.FromSeconds(1), slice.Duration);
        var clamped = audio.Slice(TimeSpan.FromSeconds(1.9), TimeSpan.FromSeconds(5));
        Assert.Equal(1600, clamped.Length);
        Assert.Equal(16000, audio.ToSampleIndex(TimeSpan.FromSeconds(1)));
    }

    [Fact]
    public void PcmAudio_concat_and_levels()
    {
        var a = PcmAudio.Silence(TimeSpan.FromSeconds(1));
        var b = Sine(16000);
        var c = PcmAudio.Concat([a, b]);
        Assert.Equal(32000, c.Length);
        Assert.Equal(0f, a.Peak());
        Assert.InRange(b.Peak(), 0.49f, 0.5f);
        Assert.InRange(b.Rms(), 0.34f, 0.36f);
    }
}

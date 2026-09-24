using System.Globalization;
using System.Runtime.InteropServices;
using Bwl.Murching.Audio;
using Bwl.Murching.Common;
using Bwl.Murching.Media;

namespace Bwl.Murching.Dubbing;

/// <summary>Interleaved multi-channel float PCM.</summary>
public sealed class InterleavedPcm
{
    public InterleavedPcm(float[] samples, int channels, int sampleRate)
    {
        Samples = samples;
        Channels = channels;
        SampleRate = sampleRate;
    }

    public float[] Samples { get; }

    public int Channels { get; }

    public int SampleRate { get; }

    public int Frames => Samples.Length / Channels;

    public TimeSpan Duration => TimeSpan.FromSeconds(Frames / (double)SampleRate);

    public int FrameAt(TimeSpan t) => (int)Math.Clamp(Math.Round(t.TotalSeconds * SampleRate), 0, Frames);

    /// <summary>Mono mixdown of a time range.</summary>
    public PcmAudio Mono(TimeSpan start, TimeSpan end)
    {
        var from = FrameAt(start);
        var to = Math.Max(from, FrameAt(end));
        var mono = new float[to - from];
        for (var f = from; f < to; f++)
        {
            float acc = 0;
            for (var c = 0; c < Channels; c++)
            {
                acc += Samples[f * Channels + c];
            }

            mono[f - from] = acc / Channels;
        }

        return new PcmAudio(mono, SampleRate);
    }

    /// <summary>RMS over a time range across channels.</summary>
    public double Rms(TimeSpan start, TimeSpan end)
    {
        var from = FrameAt(start) * Channels;
        var to = Math.Max(from, FrameAt(end) * Channels);
        if (to == from)
        {
            return 0;
        }

        double sum = 0;
        for (var i = from; i < to; i++)
        {
            sum += (double)Samples[i] * Samples[i];
        }

        return Math.Sqrt(sum / (to - from));
    }
}

/// <summary>ffmpeg-backed decoding / tempo tools plus an in-memory ducking mixer.</summary>
public sealed class AudioMixer(FfmpegTools tools)
{
    /// <summary>Decodes the default audio track as interleaved float at the given rate/channels.</summary>
    public async Task<InterleavedPcm> DecodeAsync(string input, int sampleRate, int channels, CancellationToken ct = default)
    {
        var args = new List<string>
        {
            "-hide_banner", "-nostdin", "-loglevel", "error", "-i", input, "-map", "0:a:0?", "-vn", "-sn", "-dn",
            "-ac", channels.ToString(CultureInfo.InvariantCulture), "-ar", sampleRate.ToString(CultureInfo.InvariantCulture), "-f", "f32le", "pipe:1",
        };
        var buffer = new MemoryStream();
        await ProcessRunner.RunStreamingAsync(tools.FfmpegPath, args, (stdout, token) => stdout.CopyToAsync(buffer, 1 << 18, token), ct).ConfigureAwait(false);
        var bytes = buffer.GetBuffer().AsSpan(0, (int)buffer.Length);
        var count = bytes.Length / sizeof(float) / channels * channels;
        var samples = new float[count];
        MemoryMarshal.Cast<byte, float>(bytes[..(count * sizeof(float))]).CopyTo(samples);
        return new InterleavedPcm(samples, channels, sampleRate);
    }

    /// <summary>Loads a WAV/any audio file as mono float at <paramref name="sampleRate"/>, optionally time-stretched (pitch preserved) by <paramref name="tempo"/>.</summary>
    public async Task<PcmAudio> LoadMonoAsync(string input, int sampleRate, double tempo = 1.0, CancellationToken ct = default)
    {
        var filters = new List<string>();
        var remaining = tempo;
        // atempo accepts 0.5..100 per instance; chain for larger factors.
        while (remaining > 2.0)
        {
            filters.Add("atempo=2.0");
            remaining /= 2.0;
        }

        while (remaining < 0.5)
        {
            filters.Add("atempo=0.5");
            remaining /= 0.5;
        }

        if (Math.Abs(remaining - 1.0) > 0.001)
        {
            filters.Add("atempo=" + remaining.ToString("0.####", CultureInfo.InvariantCulture));
        }

        var args = new List<string> { "-hide_banner", "-nostdin", "-loglevel", "error", "-i", input, "-vn" };
        if (filters.Count > 0)
        {
            args.AddRange(["-filter:a", string.Join(',', filters)]);
        }

        args.AddRange(["-ac", "1", "-ar", sampleRate.ToString(CultureInfo.InvariantCulture), "-f", "f32le", "pipe:1"]);
        var buffer = new MemoryStream();
        await ProcessRunner.RunStreamingAsync(tools.FfmpegPath, args, (stdout, token) => stdout.CopyToAsync(buffer, 1 << 18, token), ct).ConfigureAwait(false);
        var bytes = buffer.GetBuffer().AsSpan(0, (int)buffer.Length);
        var samples = new float[bytes.Length / sizeof(float)];
        MemoryMarshal.Cast<byte, float>(bytes[..(samples.Length * sizeof(float))]).CopyTo(samples);
        return new PcmAudio(samples, sampleRate);
    }

    /// <summary>
    /// Ducks the original wherever the voice track has content (per placed clip), adds the voice to every channel and
    /// soft-limits the sum.
    /// </summary>
    public static InterleavedPcm Mix(InterleavedPcm original, float[] voice, IReadOnlyList<(TimeSpan Start, TimeSpan End)> voiced, double duck, TimeSpan fade)
    {
        var channels = original.Channels;
        var frames = original.Frames;
        var output = new float[original.Samples.Length];
        var gain = new float[frames];
        Array.Fill(gain, 1f);

        var fadeFrames = Math.Max(1, original.FrameAt(fade));
        foreach (var (start, end) in voiced)
        {
            var a = original.FrameAt(start);
            var b = original.FrameAt(end);
            for (var f = Math.Max(0, a - fadeFrames); f < Math.Min(frames, b + fadeFrames); f++)
            {
                float g;
                if (f < a)
                {
                    g = (float)(1 - (1 - duck) * (f - (a - fadeFrames)) / (double)fadeFrames);
                }
                else if (f >= b)
                {
                    g = (float)(duck + (1 - duck) * (f - b) / (double)fadeFrames);
                }
                else
                {
                    g = (float)duck;
                }

                if (g < gain[f])
                {
                    gain[f] = g;
                }
            }
        }

        for (var f = 0; f < frames; f++)
        {
            var v = f < voice.Length ? voice[f] : 0f;
            for (var c = 0; c < channels; c++)
            {
                var sum = original.Samples[f * channels + c] * gain[f] + v;
                output[f * channels + c] = SoftClip(sum);
            }
        }

        return new InterleavedPcm(output, channels, original.SampleRate);
    }

    /// <summary>Gentle limiter: linear below 0.8, tanh-shaped above.</summary>
    internal static float SoftClip(float x)
    {
        const float knee = 0.8f;
        var a = Math.Abs(x);
        if (a <= knee)
        {
            return x;
        }

        var over = (a - knee) / (1 - knee);
        var shaped = knee + (1 - knee) * MathF.Tanh(over);
        return x < 0 ? -shaped : shaped;
    }

    public static void WriteWav(string path, InterleavedPcm pcm)
    {
        using var stream = File.Create(path);
        Span<byte> header = stackalloc byte[44];
        var bytesPerSample = 2;
        var dataSize = pcm.Samples.Length * bytesPerSample;
        System.Text.Encoding.ASCII.GetBytes("RIFF", header[..4]);
        System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(header[4..], 36 + dataSize);
        System.Text.Encoding.ASCII.GetBytes("WAVE", header[8..12]);
        System.Text.Encoding.ASCII.GetBytes("fmt ", header[12..16]);
        System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(header[16..], 16);
        System.Buffers.Binary.BinaryPrimitives.WriteInt16LittleEndian(header[20..], 1);
        System.Buffers.Binary.BinaryPrimitives.WriteInt16LittleEndian(header[22..], (short)pcm.Channels);
        System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(header[24..], pcm.SampleRate);
        System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(header[28..], pcm.SampleRate * pcm.Channels * bytesPerSample);
        System.Buffers.Binary.BinaryPrimitives.WriteInt16LittleEndian(header[32..], (short)(pcm.Channels * bytesPerSample));
        System.Buffers.Binary.BinaryPrimitives.WriteInt16LittleEndian(header[34..], 16);
        System.Text.Encoding.ASCII.GetBytes("data", header[36..40]);
        System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(header[40..], dataSize);
        stream.Write(header);

        var buffer = new byte[1 << 16];
        var offset = 0;
        while (offset < pcm.Samples.Length)
        {
            var count = Math.Min(buffer.Length / 2, pcm.Samples.Length - offset);
            for (var i = 0; i < count; i++)
            {
                var v = Math.Clamp(pcm.Samples[offset + i], -1f, 1f);
                System.Buffers.Binary.BinaryPrimitives.WriteInt16LittleEndian(buffer.AsSpan(i * 2), (short)Math.Round(v * short.MaxValue));
            }

            stream.Write(buffer, 0, count * 2);
            offset += count;
        }
    }

    /// <summary>Muxes the mixed track (and optionally the original one) with the untouched video stream.</summary>
    public async Task MuxAsync(string video, string mixedWav, string output, string language, bool keepOriginal, CancellationToken ct = default)
    {
        var args = new List<string>
        {
            "-hide_banner", "-nostdin", "-loglevel", "error", "-y",
            "-i", video, "-i", mixedWav,
            "-map", "0:v:0", "-map", "1:a:0",
        };
        if (keepOriginal)
        {
            args.AddRange(["-map", "0:a:0?"]);
        }

        args.AddRange(["-c:v", "copy", "-c:a", "aac", "-b:a", "192k"]);
        args.AddRange(["-metadata:s:a:0", "language=" + SubtitleMuxer.ToIso639Part2(language), "-metadata:s:a:0", "title=Dub (" + language + ")", "-disposition:a:0", "default"]);
        if (keepOriginal)
        {
            args.AddRange(["-metadata:s:a:1", "title=Original", "-disposition:a:1", "0"]);
        }

        if (Path.GetExtension(output).Equals(".mp4", StringComparison.OrdinalIgnoreCase))
        {
            args.AddRange(["-movflags", "+faststart"]);
        }

        args.AddRange(["-shortest", output]);
        await ProcessRunner.RunAsync(tools.FfmpegPath, args, ct).ConfigureAwait(false);
    }
}

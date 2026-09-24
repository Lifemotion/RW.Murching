using System.Buffers.Binary;
using System.Runtime.InteropServices;
using System.Text;

namespace Bwl.Murching.Audio;

public enum WavSampleFormat
{
    Int16,
    Float32,
}

/// <summary>Minimal RIFF/WAVE reader and writer (PCM 16-bit and IEEE float), enough for debugging dumps and TTS output.</summary>
public static class WavFile
{
    public static void Write(string path, PcmAudio audio, WavSampleFormat format = WavSampleFormat.Int16)
    {
        using var stream = File.Create(path);
        Write(stream, audio, format);
    }

    public static void Write(Stream stream, PcmAudio audio, WavSampleFormat format = WavSampleFormat.Int16)
    {
        var samples = audio.Samples.Span;
        var bytesPerSample = format == WavSampleFormat.Int16 ? 2 : 4;
        var dataSize = samples.Length * bytesPerSample;
        Span<byte> header = stackalloc byte[44];
        Encoding.ASCII.GetBytes("RIFF", header[..4]);
        BinaryPrimitives.WriteInt32LittleEndian(header[4..], 36 + dataSize);
        Encoding.ASCII.GetBytes("WAVE", header[8..12]);
        Encoding.ASCII.GetBytes("fmt ", header[12..16]);
        BinaryPrimitives.WriteInt32LittleEndian(header[16..], 16);
        BinaryPrimitives.WriteInt16LittleEndian(header[20..], (short)(format == WavSampleFormat.Int16 ? 1 : 3));
        BinaryPrimitives.WriteInt16LittleEndian(header[22..], 1);
        BinaryPrimitives.WriteInt32LittleEndian(header[24..], audio.SampleRate);
        BinaryPrimitives.WriteInt32LittleEndian(header[28..], audio.SampleRate * bytesPerSample);
        BinaryPrimitives.WriteInt16LittleEndian(header[32..], (short)bytesPerSample);
        BinaryPrimitives.WriteInt16LittleEndian(header[34..], (short)(bytesPerSample * 8));
        Encoding.ASCII.GetBytes("data", header[36..40]);
        BinaryPrimitives.WriteInt32LittleEndian(header[40..], dataSize);
        stream.Write(header);

        var buffer = new byte[Math.Min(dataSize, 1 << 16)];
        var offset = 0;
        while (offset < samples.Length)
        {
            var count = Math.Min(buffer.Length / bytesPerSample, samples.Length - offset);
            var chunk = samples.Slice(offset, count);
            if (format == WavSampleFormat.Int16)
            {
                for (var i = 0; i < count; i++)
                {
                    var v = Math.Clamp(chunk[i], -1f, 1f);
                    BinaryPrimitives.WriteInt16LittleEndian(buffer.AsSpan(i * 2), (short)Math.Round(v * short.MaxValue));
                }
            }
            else
            {
                MemoryMarshal.AsBytes(chunk).CopyTo(buffer);
            }

            stream.Write(buffer, 0, count * bytesPerSample);
            offset += count;
        }
    }

    /// <summary>Reads a PCM WAV file (8/16/24/32-bit integer or 32-bit float), downmixing to mono.</summary>
    public static PcmAudio Read(string path)
    {
        var bytes = File.ReadAllBytes(path);
        return Parse(bytes);
    }

    public static PcmAudio Parse(ReadOnlySpan<byte> data)
    {
        if (data.Length < 12 || !data[..4].SequenceEqual("RIFF"u8) || !data[8..12].SequenceEqual("WAVE"u8))
        {
            throw new InvalidDataException("Not a RIFF/WAVE file.");
        }

        var pos = 12;
        short audioFormat = 0, channels = 0, bitsPerSample = 0;
        var sampleRate = 0;
        ReadOnlySpan<byte> pcm = default;
        while (pos + 8 <= data.Length)
        {
            var id = data.Slice(pos, 4);
            var size = BinaryPrimitives.ReadInt32LittleEndian(data[(pos + 4)..]);
            var body = data.Slice(pos + 8, Math.Min(size, data.Length - pos - 8));
            if (id.SequenceEqual("fmt "u8))
            {
                audioFormat = BinaryPrimitives.ReadInt16LittleEndian(body);
                channels = BinaryPrimitives.ReadInt16LittleEndian(body[2..]);
                sampleRate = BinaryPrimitives.ReadInt32LittleEndian(body[4..]);
                bitsPerSample = BinaryPrimitives.ReadInt16LittleEndian(body[14..]);
                if (audioFormat == -2 && body.Length >= 26) // WAVE_FORMAT_EXTENSIBLE
                {
                    audioFormat = BinaryPrimitives.ReadInt16LittleEndian(body[24..]);
                }
            }
            else if (id.SequenceEqual("data"u8))
            {
                pcm = body;
                break;
            }

            pos += 8 + size + (size & 1);
        }

        if (channels <= 0 || sampleRate <= 0 || pcm.IsEmpty)
        {
            throw new InvalidDataException("WAV file has no usable fmt/data chunks.");
        }

        var bytesPerSample = bitsPerSample / 8;
        var frames = pcm.Length / (bytesPerSample * channels);
        var result = new float[frames];
        for (var f = 0; f < frames; f++)
        {
            float acc = 0;
            for (var c = 0; c < channels; c++)
            {
                var o = (f * channels + c) * bytesPerSample;
                acc += (audioFormat, bitsPerSample) switch
                {
                    (3, 32) => BinaryPrimitives.ReadSingleLittleEndian(pcm[o..]),
                    (1, 16) => BinaryPrimitives.ReadInt16LittleEndian(pcm[o..]) / 32768f,
                    (1, 8) => (pcm[o] - 128) / 128f,
                    (1, 24) => ((pcm[o] << 8) | (pcm[o + 1] << 16) | (pcm[o + 2] << 24)) / 2147483648f,
                    (1, 32) => BinaryPrimitives.ReadInt32LittleEndian(pcm[o..]) / 2147483648f,
                    _ => throw new NotSupportedException($"Unsupported WAV format {audioFormat} / {bitsPerSample} bit."),
                };
            }

            result[f] = acc / channels;
        }

        return new PcmAudio(result, sampleRate);
    }
}

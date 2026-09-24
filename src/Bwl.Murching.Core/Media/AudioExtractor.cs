using System.Globalization;
using System.Runtime.InteropServices;
using Bwl.Murching.Audio;
using Bwl.Murching.Common;

namespace Bwl.Murching.Media;

public sealed record AudioExtractOptions
{
    public int SampleRate { get; init; } = PcmAudio.WhisperSampleRate;

    /// <summary>Zero-based index among the audio streams of the file (ffmpeg <c>0:a:N</c>). Null = default stream.</summary>
    public int? AudioStreamIndex { get; init; }

    public TimeSpan? Start { get; init; }

    public TimeSpan? End { get; init; }
}

/// <summary>Decodes any ffmpeg-readable input into mono float PCM in memory.</summary>
public sealed class AudioExtractor(FfmpegTools tools)
{
    public async Task<PcmAudio> ExtractAsync(string inputPath, AudioExtractOptions? options = null, CancellationToken ct = default)
    {
        options ??= new AudioExtractOptions();
        if (!File.Exists(inputPath))
        {
            throw new FileNotFoundException("Input file not found.", inputPath);
        }

        var args = BuildArguments(inputPath, options, "f32le", "pipe:1");
        var buffer = new MemoryStream();
        await ProcessRunner.RunStreamingAsync(tools.FfmpegPath, args, async (stdout, token) =>
        {
            await stdout.CopyToAsync(buffer, 1 << 18, token).ConfigureAwait(false);
        }, ct).ConfigureAwait(false);

        var bytes = buffer.GetBuffer().AsMemory(0, (int)buffer.Length);
        var sampleCount = bytes.Length / sizeof(float);
        var samples = new float[sampleCount];
        MemoryMarshal.Cast<byte, float>(bytes.Span[..(sampleCount * sizeof(float))]).CopyTo(samples);
        return new PcmAudio(samples, options.SampleRate);
    }

    /// <summary>Writes a 16-bit PCM WAV using ffmpeg directly (handy for debugging).</summary>
    public async Task ExtractToWavAsync(string inputPath, string outputPath, AudioExtractOptions? options = null, CancellationToken ct = default)
    {
        options ??= new AudioExtractOptions();
        var args = BuildArguments(inputPath, options, "wav", outputPath, overwrite: true, codec: "pcm_s16le");
        await ProcessRunner.RunAsync(tools.FfmpegPath, args, ct).ConfigureAwait(false);
    }

    internal static List<string> BuildArguments(string inputPath, AudioExtractOptions options, string format, string output, bool overwrite = false, string? codec = null)
    {
        var args = new List<string> { "-hide_banner", "-nostdin", "-loglevel", "error" };
        if (overwrite)
        {
            args.Add("-y");
        }

        if (options.Start is { } start && start > TimeSpan.Zero)
        {
            args.AddRange(["-ss", start.TotalSeconds.ToString("0.###", CultureInfo.InvariantCulture)]);
        }

        args.AddRange(["-i", inputPath]);

        if (options.End is { } end)
        {
            var duration = end - (options.Start ?? TimeSpan.Zero);
            if (duration > TimeSpan.Zero)
            {
                args.AddRange(["-t", duration.TotalSeconds.ToString("0.###", CultureInfo.InvariantCulture)]);
            }
        }

        if (options.AudioStreamIndex is { } idx)
        {
            args.AddRange(["-map", $"0:a:{idx}"]);
        }
        else
        {
            args.AddRange(["-map", "0:a:0?"]);
        }

        args.AddRange(["-vn", "-sn", "-dn", "-ac", "1", "-ar", options.SampleRate.ToString(CultureInfo.InvariantCulture)]);
        if (codec is not null)
        {
            args.AddRange(["-c:a", codec]);
        }

        args.AddRange(["-f", format, output]);
        return args;
    }
}

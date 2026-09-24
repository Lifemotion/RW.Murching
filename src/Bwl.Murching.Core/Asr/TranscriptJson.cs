using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Bwl.Murching.Asr;

/// <summary>JSON sidecar format for transcripts (word timings included) so later stages such as dubbing can reuse them.</summary>
public static class TranscriptJson
{
    public const string Extension = ".murch.json";

    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        NumberHandling = JsonNumberHandling.AllowReadingFromString,
    };

    public static string Serialize(Transcript transcript) => JsonSerializer.Serialize(ToDto(transcript), Options);

    public static Transcript Deserialize(string json)
    {
        var dto = JsonSerializer.Deserialize<TranscriptDto>(json, Options) ?? throw new InvalidDataException("Empty transcript JSON.");
        return FromDto(dto);
    }

    public static async Task WriteAsync(string path, Transcript transcript, CancellationToken ct = default)
    {
        await using var stream = File.Create(path);
        await JsonSerializer.SerializeAsync(stream, ToDto(transcript), Options, ct).ConfigureAwait(false);
    }

    public static async Task<Transcript> ReadAsync(string path, CancellationToken ct = default)
    {
        await using var stream = File.OpenRead(path);
        var dto = await JsonSerializer.DeserializeAsync<TranscriptDto>(stream, Options, ct).ConfigureAwait(false)
                  ?? throw new InvalidDataException("Empty transcript JSON.");
        return FromDto(dto);
    }

    private static TranscriptDto ToDto(Transcript t) => new()
    {
        Format = "murch-transcript/1",
        Language = t.Language,
        Model = t.Model,
        Duration = t.SourceDuration is { } d ? Seconds(d) : null,
        Segments = t.Segments.Select(s => new SegmentDto
        {
            Start = Seconds(s.Start),
            End = Seconds(s.End),
            Text = s.Text,
            Probability = Round(s.Probability),
            NoSpeech = Round(s.NoSpeechProbability),
            Words = s.Words.Select(w => new WordDto { Text = w.Text, Start = Seconds(w.Start), End = Seconds(w.End), Probability = Round(w.Probability) }).ToList(),
        }).ToList(),
    };

    private static Transcript FromDto(TranscriptDto dto) => new(
        dto.Language,
        (dto.Segments ?? []).Select(s => new TranscriptSegment(
            s.Text ?? string.Empty,
            TimeSpan.FromSeconds(s.Start),
            TimeSpan.FromSeconds(s.End),
            (s.Words ?? []).Select(w => new TranscriptWord(w.Text ?? string.Empty, TimeSpan.FromSeconds(w.Start), TimeSpan.FromSeconds(w.End), w.Probability)).ToList(),
            s.Probability,
            s.NoSpeech)).ToList())
    {
        Model = dto.Model,
        SourceDuration = dto.Duration is { } d ? TimeSpan.FromSeconds(d) : null,
    };

    private static double Seconds(TimeSpan t) => Math.Round(t.TotalSeconds, 3);

    private static float Round(float v) => MathF.Round(v, 3);

    private sealed class TranscriptDto
    {
        public string? Format { get; set; }

        public string? Language { get; set; }

        public string? Model { get; set; }

        public double? Duration { get; set; }

        public List<SegmentDto>? Segments { get; set; }
    }

    private sealed class SegmentDto
    {
        public double Start { get; set; }

        public double End { get; set; }

        public string? Text { get; set; }

        public float Probability { get; set; }

        public float NoSpeech { get; set; }

        public List<WordDto>? Words { get; set; }
    }

    private sealed class WordDto
    {
        public string? Text { get; set; }

        public double Start { get; set; }

        public double End { get; set; }

        public float Probability { get; set; }
    }
}

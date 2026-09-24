namespace Bwl.Murching.Media;

public enum MediaStreamType
{
    Unknown,
    Audio,
    Video,
    Subtitle,
    Data,
    Attachment,
}

/// <summary>One elementary stream reported by ffprobe.</summary>
public sealed record MediaStreamInfo(
    int Index,
    MediaStreamType Type,
    string? Codec,
    int? SampleRate,
    int? Channels,
    string? ChannelLayout,
    int? Width,
    int? Height,
    double? FrameRate,
    TimeSpan? Duration,
    string? Language,
    string? Title,
    bool IsDefault,
    bool IsAttachedPicture = false)
{
    public override string ToString() => Type switch
    {
        MediaStreamType.Audio => $"#{Index} audio {Codec} {SampleRate} Hz {ChannelLayout ?? Channels + "ch"}{Tag()}",
        MediaStreamType.Video => $"#{Index} video {Codec} {Width}x{Height}{(FrameRate is { } f ? $" {f:0.###} fps" : string.Empty)}{Tag()}",
        _ => $"#{Index} {Type.ToString().ToLowerInvariant()} {Codec}{Tag()}",
    };

    private string Tag() => (Language, Title) switch
    {
        (null, null) => string.Empty,
        (var l, null) => $" [{l}]",
        (null, var t) => $" [{t}]",
        var (l, t) => $" [{l}: {t}]",
    };
}

/// <summary>Container level information for a media file.</summary>
public sealed record MediaInfo(
    string Path,
    string? Format,
    string? FormatLongName,
    TimeSpan Duration,
    long? SizeBytes,
    long? BitRate,
    IReadOnlyList<MediaStreamInfo> Streams)
{
    public IEnumerable<MediaStreamInfo> AudioStreams => Streams.Where(s => s.Type == MediaStreamType.Audio);

    public IEnumerable<MediaStreamInfo> VideoStreams => Streams.Where(s => s.Type == MediaStreamType.Video && !s.IsAttachedPicture);

    public IEnumerable<MediaStreamInfo> SubtitleStreams => Streams.Where(s => s.Type == MediaStreamType.Subtitle);

    public bool HasAudio => AudioStreams.Any();

    public bool HasVideo => VideoStreams.Any();
}

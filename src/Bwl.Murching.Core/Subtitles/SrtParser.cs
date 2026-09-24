using System.Globalization;
using System.Text.RegularExpressions;

namespace Bwl.Murching.Subtitles;

/// <summary>Reads SubRip (.srt) and WebVTT (.vtt) files back into a <see cref="SubtitleDocument"/>.</summary>
public static partial class SrtParser
{
    [GeneratedRegex(@"^\s*(?<sh>\d{1,2}):(?<sm>\d{2}):(?<ss>\d{2})[,.](?<sms>\d{1,3})\s*-->\s*(?<eh>\d{1,2}):(?<em>\d{2}):(?<es>\d{2})[,.](?<ems>\d{1,3})")]
    private static partial Regex TimingRegex();

    [GeneratedRegex(@"<[^>]+>")]
    private static partial Regex TagRegex();

    public static SubtitleDocument ParseFile(string path)
    {
        var text = File.ReadAllText(path);
        var doc = Parse(text);
        var name = Path.GetFileNameWithoutExtension(path);
        var lang = name.Split('.') is { Length: > 1 } parts && parts[^1].Length is 2 or 3 && parts[^1].All(char.IsLetter) ? parts[^1] : null;
        return new SubtitleDocument(doc.Cues) { Language = lang, Title = name };
    }

    public static SubtitleDocument Parse(string text)
    {
        var lines = text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').Split('\n');
        var cues = new List<SubtitleCue>();
        var i = 0;
        if (lines.Length > 0 && lines[0].TrimStart('﻿').StartsWith("WEBVTT", StringComparison.Ordinal))
        {
            while (i < lines.Length && lines[i].Trim().Length > 0)
            {
                i++;
            }
        }

        while (i < lines.Length)
        {
            var line = lines[i].TrimStart('﻿');
            var timing = TimingRegex().Match(line);
            if (!timing.Success)
            {
                i++;
                continue;
            }

            var start = ToTime(timing, "sh", "sm", "ss", "sms");
            var end = ToTime(timing, "eh", "em", "es", "ems");
            i++;
            var body = new List<string>();
            while (i < lines.Length && lines[i].Trim().Length > 0)
            {
                var clean = TagRegex().Replace(lines[i], string.Empty).Trim();
                if (clean.Length > 0)
                {
                    body.Add(clean);
                }

                i++;
            }

            if (body.Count > 0)
            {
                cues.Add(new SubtitleCue(cues.Count + 1, start, end, body));
            }
        }

        return new SubtitleDocument(cues);
    }

    private static TimeSpan ToTime(Match m, string h, string min, string s, string ms)
    {
        var millis = m.Groups[ms].Value.PadRight(3, '0');
        return new TimeSpan(0, int.Parse(m.Groups[h].Value, CultureInfo.InvariantCulture), int.Parse(m.Groups[min].Value, CultureInfo.InvariantCulture), int.Parse(m.Groups[s].Value, CultureInfo.InvariantCulture), int.Parse(millis, CultureInfo.InvariantCulture));
    }
}

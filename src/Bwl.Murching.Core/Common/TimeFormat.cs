using System.Globalization;

namespace Bwl.Murching.Common;

public static class TimeFormat
{
    /// <summary>Formats as <c>HH:MM:SS,mmm</c> (SubRip).</summary>
    public static string Srt(TimeSpan t) => Format(t, ',');

    /// <summary>Formats as <c>HH:MM:SS.mmm</c> (WebVTT).</summary>
    public static string Vtt(TimeSpan t) => Format(t, '.');

    /// <summary>Formats as <c>H:MM:SS.cc</c> (ASS / SSA, centiseconds).</summary>
    public static string Ass(TimeSpan t)
    {
        if (t < TimeSpan.Zero)
        {
            t = TimeSpan.Zero;
        }

        var totalCs = (long)Math.Round(t.TotalMilliseconds / 10.0);
        var h = totalCs / 360000;
        var m = totalCs / 6000 % 60;
        var s = totalCs / 100 % 60;
        var cs = totalCs % 100;
        return string.Create(CultureInfo.InvariantCulture, $"{h}:{m:00}:{s:00}.{cs:00}");
    }

    /// <summary>Compact human readable form, e.g. <c>1:02:03</c>, <c>2:03</c> or <c>12.3s</c>.</summary>
    public static string Human(TimeSpan t)
    {
        if (t.TotalHours >= 1)
        {
            return string.Create(CultureInfo.InvariantCulture, $"{(int)t.TotalHours}:{t.Minutes:00}:{t.Seconds:00}");
        }

        if (t.TotalMinutes >= 1)
        {
            return string.Create(CultureInfo.InvariantCulture, $"{t.Minutes}:{t.Seconds:00}");
        }

        return string.Create(CultureInfo.InvariantCulture, $"{t.TotalSeconds:0.0}s");
    }

    private static string Format(TimeSpan t, char msSeparator)
    {
        if (t < TimeSpan.Zero)
        {
            t = TimeSpan.Zero;
        }

        var totalMs = (long)Math.Round(t.TotalMilliseconds);
        var h = totalMs / 3_600_000;
        var m = totalMs / 60_000 % 60;
        var s = totalMs / 1000 % 60;
        var ms = totalMs % 1000;
        return string.Create(CultureInfo.InvariantCulture, $"{h:00}:{m:00}:{s:00}{msSeparator}{ms:000}");
    }
}

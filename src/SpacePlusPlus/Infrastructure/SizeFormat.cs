using System.Globalization;

namespace SpacePlusPlus.Infrastructure;

public static class SizeFormat
{
    public static string Format(long bytes)
    {
        double b = bytes;
        string sign = b < 0 ? "-" : "";
        b = Math.Abs(b);
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        int u = 0;
        while (b >= 1024 && u < units.Length - 1)
        {
            b /= 1024;
            u++;
        }
        string value = b.ToString(u == 0 ? "F0" : "F2", CultureInfo.InvariantCulture);
        return $"{sign}{value} {units[u]}";
    }

    public static string FormatCount(long n) => n.ToString("N0", CultureInfo.InvariantCulture);

    public static string FormatDuration(TimeSpan t)
    {
        if (t.TotalHours >= 1) return $"{(int)t.TotalHours}h {t.Minutes}m {t.Seconds}s";
        if (t.TotalMinutes >= 1) return $"{t.Minutes}m {t.Seconds}s";
        return $"{t.Seconds}.{Math.Min(9, t.Milliseconds / 100)}s";
    }

    /// <summary>Approximates allocated size on disk (per-file 4 KiB alignment). Compressed/sparse files deviate.</summary>
    public static long AlignToCluster(long size) => (size + 4095) & ~4095L;
}

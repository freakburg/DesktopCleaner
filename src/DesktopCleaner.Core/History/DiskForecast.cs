using System.Text.Json;
using DesktopCleaner.Core.Settings;

namespace DesktopCleaner.Core.History;

public sealed record DrivePoint(DateTime TimeUtc, string Drive, long FreeBytes, long TotalBytes);

public enum ForecastKind { NotEnoughData, Steady, FillingUp }

/// <param name="DaysToFull">Only set when <see cref="Kind"/> is FillingUp.</param>
public sealed record DiskForecastResult(ForecastKind Kind, double? DaysToFull, double BytesPerWeek, int Points, double SpanDays, bool Reliable)
{
    public string Message(string drive)
    {
        drive = drive.TrimEnd('\\');
        switch (Kind)
        {
            case ForecastKind.NotEnoughData:
                return "Not enough history yet to predict when the disk fills up. Check again after a few days.";
            case ForecastKind.Steady:
                return $"Free space on {drive} is steady or growing. No sign of it filling up.";
            default:
                var d = DaysToFull ?? 0;
                var when = d < 1 ? "within a day" : d < 14 ? $"in about {Math.Round(d)} days" : d < 90 ? $"in about {Math.Round(d / 7)} weeks" : d < 730 ? $"in about {Math.Round(d / 30)} months" : "not for a long time";
                return $"At this pace {drive} will be full {when} (using about {FormatBytes(BytesPerWeek)} more each week)." + (Reliable ? "" : " This is a rough guess: the history is short or uneven.");
        }
    }

    private static string FormatBytes(double b) => b switch
    {
        >= 1L << 30 => $"{b / (1L << 30):F1} GB", >= 1L << 20 => $"{b / (1L << 20):F0} MB", _ => $"{b / 1024:F0} KB",
    };
}

/// <summary>Remembers how much free space each drive had over time, so the app can say when a drive will run out.</summary>
public sealed class DriveHistory(string? path = null, Func<DateTime>? clock = null)
{
    private readonly object _gate = new();
    private readonly Func<DateTime> _now = clock ?? (() => DateTime.UtcNow);
    public string FilePath { get; } = path ?? Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DesktopCleaner", "drive-history.jsonl");

    /// <summary>Adds a reading unless this drive already has one from the last few hours (so opening the app often does not skew the trend).</summary>
    public bool Record(DiskStatus status, TimeSpan? minGap = null)
    {
        var gap = minGap ?? TimeSpan.FromHours(6);
        lock (_gate)
        {
            var last = Points(status.Drive).LastOrDefault();
            if (last is not null && _now() - last.TimeUtc < gap) return false;
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.AppendAllText(FilePath, JsonSerializer.Serialize(new DrivePoint(_now(), status.Drive, status.FreeBytes, status.TotalBytes)) + Environment.NewLine);
            Trim();
            return true;
        }
    }

    public IReadOnlyList<DrivePoint> Points(string drive)
    {
        if (!File.Exists(FilePath)) return [];
        var list = new List<DrivePoint>();
        foreach (var line in File.ReadAllLines(FilePath))
        {
            try
            {
                var p = JsonSerializer.Deserialize<DrivePoint>(line);
                if (p is not null && string.Equals(p.Drive, drive, StringComparison.OrdinalIgnoreCase)) list.Add(p);
            }
            catch (JsonException) { }
        }
        return list.OrderBy(p => p.TimeUtc).ToList();
    }

    private void Trim()
    {
        var lines = File.ReadAllLines(FilePath);
        if (lines.Length > 3000) File.WriteAllLines(FilePath, lines.Skip(lines.Length - 2000));
    }
}

public static class DiskForecast
{
    /// <summary>
    /// A straight line through the last <paramref name="windowDays"/> days of free-space readings. Needs three readings over at least
    /// three days. "Reliable" means enough readings and the line actually fits them.
    /// </summary>
    public static DiskForecastResult Estimate(IReadOnlyList<DrivePoint> points, DateTime nowUtc, int windowDays = 120)
    {
        var pts = points.Where(p => (nowUtc - p.TimeUtc).TotalDays <= windowDays).OrderBy(p => p.TimeUtc).ToList();
        if (pts.Count < 3 || (pts[^1].TimeUtc - pts[0].TimeUtc).TotalDays < 3)
            return new(ForecastKind.NotEnoughData, null, 0, pts.Count, pts.Count > 1 ? (pts[^1].TimeUtc - pts[0].TimeUtc).TotalDays : 0, false);

        var t0 = pts[0].TimeUtc;
        double[] x = pts.Select(p => (p.TimeUtc - t0).TotalDays).ToArray(), y = pts.Select(p => (double)p.FreeBytes).ToArray();
        double mx = x.Average(), my = y.Average();
        double sxx = x.Sum(v => (v - mx) * (v - mx)), sxy = x.Zip(y).Sum(p => (p.First - mx) * (p.Second - my)), syy = y.Sum(v => (v - my) * (v - my));
        double slope = sxx == 0 ? 0 : sxy / sxx;                         // bytes per day (negative = filling up)
        double r2 = sxx == 0 || syy == 0 ? 0 : sxy * sxy / (sxx * syy);
        double span = x[^1];
        bool reliable = pts.Count >= 5 && span >= 7 && r2 >= 0.5;

        const double steadyThreshold = 1024 * 1024;                      // under 1 MB/day of change counts as steady
        if (slope > -steadyThreshold) return new(ForecastKind.Steady, null, 0, pts.Count, span, reliable);
        double free = pts[^1].FreeBytes;
        return new(ForecastKind.FillingUp, free / -slope, -slope * 7, pts.Count, span, reliable);
    }
}

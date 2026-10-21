using DesktopCleaner.Core.History;
using DesktopCleaner.Core.Settings;
using Xunit;

namespace DesktopCleaner.Tests;

public sealed class ForecastTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("dc-fc").FullName;
    public void Dispose() => Directory.Delete(_dir, true);
    private const long GB = 1L << 30;
    private static readonly DateTime Now = new(2026, 10, 20, 12, 0, 0, DateTimeKind.Utc);

    private static DrivePoint P(int daysAgo, double freeGb) => new(Now.AddDays(-daysAgo), "C:\\", (long)(freeGb * GB), 500 * GB);

    [Fact]
    public void Steady_loss_is_projected_to_the_day_the_disk_fills()
    {
        // losing 2 GB a day, 40 GB free now -> 20 days
        var pts = Enumerable.Range(0, 11).Select(i => P(10 - i, 60 - i * 2.0)).ToList();
        var r = DiskForecast.Estimate(pts, Now);
        Assert.Equal(ForecastKind.FillingUp, r.Kind);
        Assert.Equal(20, r.DaysToFull!.Value, 1);
        Assert.Equal(14 * GB, r.BytesPerWeek, 1e8);
        Assert.True(r.Reliable);
        Assert.Contains("about 3 weeks", r.Message("C:\\"));
    }

    [Fact]
    public void Growing_or_flat_free_space_is_reported_as_steady()
    {
        Assert.Equal(ForecastKind.Steady, DiskForecast.Estimate(Enumerable.Range(0, 8).Select(i => P(8 - i, 100.0)).ToList(), Now).Kind);
        Assert.Equal(ForecastKind.Steady, DiskForecast.Estimate(Enumerable.Range(0, 8).Select(i => P(8 - i, 100.0 + i)).ToList(), Now).Kind);
    }

    [Fact]
    public void Too_little_history_says_so_instead_of_guessing()
    {
        Assert.Equal(ForecastKind.NotEnoughData, DiskForecast.Estimate([P(1, 50), P(0, 49)], Now).Kind);
        Assert.Equal(ForecastKind.NotEnoughData, DiskForecast.Estimate([P(2, 50), P(1, 49), P(0, 48)], Now).Kind);   // only two days apart
        Assert.Contains("Not enough history", DiskForecast.Estimate([], Now).Message("C:\\"));
    }

    [Fact]
    public void Noisy_short_history_is_marked_as_a_rough_guess()
    {
        var pts = new[] { P(6, 80), P(5, 60), P(4, 85), P(3, 55), P(2, 75), P(1, 50), P(0, 70) }.ToList();
        var r = DiskForecast.Estimate(pts, Now);
        Assert.False(r.Reliable);
        if (r.Kind == ForecastKind.FillingUp) Assert.Contains("rough guess", r.Message("C:\\"));
    }

    [Fact]
    public void A_cleanup_in_the_middle_does_not_crash_and_old_points_are_ignored()
    {
        var pts = new[] { P(300, 10), P(10, 50), P(8, 48), P(6, 46), P(4, 70), P(2, 68), P(0, 66) }.ToList();  // freed space 4 days ago
        var r = DiskForecast.Estimate(pts, Now);
        Assert.Equal(6, r.Points);   // the 300-day-old reading is outside the window
    }

    [Fact]
    public void History_records_per_drive_skips_rapid_duplicates_and_survives_garbage()
    {
        var now = Now;
        var h = new DriveHistory(Path.Combine(_dir, "h.jsonl"), () => now);
        Assert.True(h.Record(new DiskStatus("C:\\", 50 * GB, 500 * GB)));
        now = now.AddHours(1);
        Assert.False(h.Record(new DiskStatus("C:\\", 49 * GB, 500 * GB)));   // too soon
        Assert.True(h.Record(new DiskStatus("G:\\", 5 * GB, 100 * GB)));     // different drive
        now = now.AddHours(7);
        Assert.True(h.Record(new DiskStatus("c:\\", 48 * GB, 500 * GB)));    // drive letter compared ignoring case
        File.AppendAllText(h.FilePath, "garbage\n");
        Assert.Equal(2, h.Points("C:\\").Count);
        Assert.Single(h.Points("G:\\"));
    }
}

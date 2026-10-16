using DesktopCleaner.Core.Scheduling;
using Xunit;

namespace DesktopCleaner.Tests;

public sealed class SchedulerTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("dc-sched").FullName;
    public void Dispose() => Directory.Delete(_dir, true);
    private readonly List<string[]> _calls = [];

    private ReportScheduler Make(int rc = 0) => new(Path.Combine(_dir, "s.json"), Path.Combine(_dir, "reports"), a => { _calls.Add(a.ToArray()); return rc; });

    [Fact]
    public void Enable_creates_weekly_task_and_Get_returns_it()
    {
        var s = Make();
        s.Enable(DayOfWeek.Wednesday, "08:30", @"C:\App\app.exe", "--scheduled-report");
        var create = _calls.Single(c => c[0] == "/Create");
        Assert.Contains("WED", create);
        Assert.Contains("08:30", create);
        Assert.Contains("\"C:\\App\\app.exe\" --scheduled-report", create);
        var got = s.Get();
        Assert.Equal((DayOfWeek.Wednesday, 8, 30), (got!.Day, got.Hour, got.Minute));
    }

    [Theory]
    [InlineData("9:00")] [InlineData("25:00")] [InlineData("09:60")] [InlineData("09:00 /RU SYSTEM")]
    public void Enable_rejects_bad_times(string time) =>
        Assert.Throws<ArgumentException>(() => Make().Enable(DayOfWeek.Monday, time, "x.exe", ""));

    [Fact]
    public void Get_is_null_when_task_was_removed_outside_the_app()
    {
        Make().Enable(DayOfWeek.Monday, "09:00", "x.exe", "");
        Assert.Null(Make(rc: 1).Get());
    }

    [Fact]
    public void Disable_removes_settings()
    {
        var s = Make(); s.Enable(DayOfWeek.Monday, "09:00", "x.exe", "");
        s.Disable();
        Assert.Null(s.Get());
    }

    [Fact]
    public void NextRun_is_this_week_if_still_ahead_otherwise_next_week()
    {
        var set = new ScheduleSettings(DayOfWeek.Friday, 9, 0, "", "");
        var wed = new DateTime(2026, 10, 14, 12, 0, 0); // Wednesday
        Assert.Equal(new DateTime(2026, 10, 16, 9, 0, 0), set.NextRun(wed));
        var fridayLater = new DateTime(2026, 10, 16, 10, 0, 0);
        Assert.Equal(new DateTime(2026, 10, 23, 9, 0, 0), set.NextRun(fridayLater));
    }

    [Fact]
    public void ListReports_returns_newest_first()
    {
        var s = Make(); Directory.CreateDirectory(s.ReportsFolder);
        File.WriteAllText(Path.Combine(s.ReportsFolder, "cleanup-a.md"), "a");
        File.WriteAllText(Path.Combine(s.ReportsFolder, "cleanup-b.md"), "b");
        File.SetLastWriteTime(Path.Combine(s.ReportsFolder, "cleanup-a.md"), DateTime.Now.AddDays(-2));
        Assert.EndsWith("cleanup-b.md", s.ListReports()[0].Path);
    }
}

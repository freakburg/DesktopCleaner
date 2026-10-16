using System.Diagnostics;
using System.Text.Json;
using System.Text.RegularExpressions;
using DesktopCleaner.Core.Cleanup;

namespace DesktopCleaner.Core.Scheduling;

public sealed record ScheduleSettings(DayOfWeek Day, int Hour, int Minute, string ExePath, string Arguments)
{
    /// <summary>Next time this weekly schedule fires after <paramref name="now"/>.</summary>
    public DateTime NextRun(DateTime now)
    {
        var candidate = now.Date.AddHours(Hour).AddMinutes(Minute);
        candidate = candidate.AddDays(((int)Day - (int)candidate.DayOfWeek + 7) % 7);
        return candidate <= now ? candidate.AddDays(7) : candidate;
    }
}

public sealed record PastReport(string Path, DateTime CreatedLocal)
{
    public string Title => CreatedLocal.ToString("dddd, MMMM d 'at' h:mm tt");
}

/// <summary>
/// Weekly scheduled dry-run report via Windows Task Scheduler. The scheduled job only ever writes a
/// report; it never deletes anything. Settings are kept in a small JSON file so the UI can show them
/// without parsing localized schtasks output.
/// </summary>
public sealed class ReportScheduler(string? settingsPath = null, string? reportsFolder = null, Func<IReadOnlyList<string>, int>? runSchtasks = null)
{
    public const string TaskName = "DesktopCleanerWeekly";
    private static readonly Regex TimeRx = new(@"^([01]\d|2[0-3]):[0-5]\d$");

    private readonly string _settingsPath = settingsPath ?? Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DesktopCleaner", "schedule.json");
    public string ReportsFolder { get; } = reportsFolder ?? ReportWriter.DefaultFolder;
    private readonly Func<IReadOnlyList<string>, int> _schtasks = runSchtasks ?? RunSchtasksProcess;

    /// <summary>Current schedule, or null if none is set or the task was removed outside this app.</summary>
    public ScheduleSettings? Get()
    {
        if (!File.Exists(_settingsPath)) return null;
        ScheduleSettings? s;
        try { s = JsonSerializer.Deserialize<ScheduleSettings>(File.ReadAllText(_settingsPath)); }
        catch (JsonException) { return null; }
        return s is not null && _schtasks(["/Query", "/TN", TaskName]) == 0 ? s : null;
    }

    public void Enable(DayOfWeek day, string time, string exePath, string arguments)
    {
        if (!TimeRx.IsMatch(time)) throw new ArgumentException("Time must look like 09:30.", nameof(time));
        var code = day.ToString()[..3].ToUpperInvariant();
        var command = $"\"{exePath}\" {arguments}";
        var rc = _schtasks(["/Create", "/TN", TaskName, "/SC", "WEEKLY", "/D", code, "/ST", time, "/F", "/TR", command]);
        if (rc != 0) throw new IOException($"Windows could not create the scheduled task (code {rc}).");
        var parts = time.Split(':');
        var settings = new ScheduleSettings(day, int.Parse(parts[0]), int.Parse(parts[1]), exePath, arguments);
        Directory.CreateDirectory(Path.GetDirectoryName(_settingsPath)!);
        File.WriteAllText(_settingsPath, JsonSerializer.Serialize(settings));
    }

    public void Disable()
    {
        _schtasks(["/Delete", "/TN", TaskName, "/F"]);
        if (File.Exists(_settingsPath)) File.Delete(_settingsPath);
    }

    public IReadOnlyList<PastReport> ListReports() =>
        !Directory.Exists(ReportsFolder) ? [] :
        new DirectoryInfo(ReportsFolder).EnumerateFiles("cleanup-*.md")
            .OrderByDescending(f => f.LastWriteTime)
            .Select(f => new PastReport(f.FullName, f.LastWriteTime)).ToList();

    /// <summary>Runs every enabled rule as a dry run and saves the report. Never deletes anything.</summary>
    public static async Task<string> RunReportAsync(string? reportsFolder = null, CancellationToken ct = default)
    {
        var engine = new CleanupEngine();
        var reports = new List<RuleReport>();
        foreach (var rule in RuleStore.Load().Where(r => r.Enabled))
            reports.Add(await engine.PreviewAsync(rule, ct));
        return ReportWriter.Save(ReportWriter.Render(reports, applied: false), reportsFolder);
    }

    private static int RunSchtasksProcess(IReadOnlyList<string> args)
    {
        var psi = new ProcessStartInfo("schtasks.exe") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        p.StandardOutput.ReadToEnd(); p.StandardError.ReadToEnd();
        p.WaitForExit();
        return p.ExitCode;
    }
}

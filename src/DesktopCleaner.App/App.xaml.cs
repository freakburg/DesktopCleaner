using System.Windows;
using DesktopCleaner.Core.Deletion;
using DesktopCleaner.Core.Scheduling;
using DesktopCleaner.Core.Settings;

namespace DesktopCleaner.App;

public partial class App : Application
{
    public const string ScheduledReportArg = "--scheduled-report";
    public const string FolderArg = "--folder";

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        FileDisposal.Configure(AppSettings.Load());
        if (e.Args.Contains(ScheduledReportArg))
        {
            try { FileDisposal.Net.Purge(); } catch { /* expiring old held files is best effort */ }
            // Headless run for Task Scheduler: write a dry-run report, never delete, show no window.
            try { await ReportScheduler.RunReportAsync(); } catch { /* nothing to show a user at 3am */ }
            try { await LowDiskNotice.ShowIfNeededAsync(); } catch { /* a missed notification is not worth a crash */ }
            Shutdown();
            return;
        }
        var ti = Array.IndexOf(e.Args, "--theme");
        bool? forced = ti >= 0 && ti + 1 < e.Args.Length ? e.Args[ti + 1].Equals("light", StringComparison.OrdinalIgnoreCase) : null;
        ScrollTuning.Install();
        _ = Task.Run(() =>
        {
            try { FileDisposal.Net.Purge(); } catch { /* best effort */ }   // delete safety-net items past their date
            try { if (DiskSpaceCheck.SystemDrive() is { } d) new DesktopCleaner.Core.History.DriveHistory().Record(d); } catch { /* best effort */ }
        });
        Theme.Apply(this, forced);
        if (forced is null) Theme.Watch(this);
        var window = new MainWindow();
        window.Show();
        var i = Array.IndexOf(e.Args, FolderArg);
        if (i >= 0 && i + 1 < e.Args.Length) window.CheckFolder(e.Args[i + 1]); // from the Explorer right-click menu
    }
}

/// <summary>A tray balloon when the system drive is low on space. Shown from the weekly background run, which stays alive briefly so it can be seen.</summary>
public static class LowDiskNotice
{
    public static async Task ShowIfNeededAsync()
    {
        var settings = AppSettings.Load();
        var disk = DiskSpaceCheck.SystemDrive();
        if (disk is null || !DiskSpaceCheck.IsLow(disk, settings)) return;

        using var icon = new System.Windows.Forms.NotifyIcon
        {
            Icon = System.Drawing.SystemIcons.Warning, Visible = true,
            BalloonTipTitle = "Your disk is getting full",
            BalloonTipText = $"Drive {disk.Drive.TrimEnd('\\')} has only {Format.Bytes(disk.FreeBytes)} free ({disk.FreePercent:F0}%). Open Desktop Cleaner to see what you can clear.",
            BalloonTipIcon = System.Windows.Forms.ToolTipIcon.Warning,
        };
        icon.BalloonTipClicked += (_, _) => System.Diagnostics.Process.Start(Environment.ProcessPath!);
        icon.ShowBalloonTip(15000);
        await Task.Delay(TimeSpan.FromSeconds(16)); // keep the process alive long enough to be seen and clicked
    }
}







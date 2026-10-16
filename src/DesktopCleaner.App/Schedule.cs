using System.Diagnostics;
using System.IO;
using System.Windows;
using DesktopCleaner.Core.Scheduling;

namespace DesktopCleaner.App;

public partial class MainWindow
{
    private readonly ReportScheduler _scheduler = new();

    private void InitSchedule()
    {
        SchedDay.ItemsSource = Enum.GetValues<DayOfWeek>().Select(d => d.ToString()).ToList();
        SchedDay.SelectedItem = DayOfWeek.Monday.ToString();
        SchedTime.ItemsSource = Enumerable.Range(0, 48).Select(i => $"{i / 2:00}:{i % 2 * 30:00}").ToList();
        SchedTime.SelectedItem = "09:00";
        RefreshSchedule();
    }

    private void RefreshSchedule()
    {
        var s = _scheduler.Get();
        if (s is null) SchedStatus.Text = "The weekly report is off.";
        else
        {
            SchedDay.SelectedItem = s.Day.ToString(); SchedTime.SelectedItem = $"{s.Hour:00}:{s.Minute:00}";
            SchedStatus.Text = $"On. Next report: {s.NextRun(DateTime.Now):dddd, MMMM d 'at' h:mm tt}. Your PC must be on and you must be signed in.";
        }
        SchedOffBtn.IsEnabled = s is not null;
        var reports = _scheduler.ListReports();
        SchedList.ItemsSource = reports;
        if (reports.Count > 0 && SchedList.SelectedItem is null) SchedList.SelectedIndex = 0;
        else if (reports.Count == 0) SchedView.Text = "No reports yet. Press Make a report now to see what one looks like.";
    }

    private void SchedOn_Click(object s, RoutedEventArgs e)
    {
        var day = Enum.Parse<DayOfWeek>((string)SchedDay.SelectedItem);
        try
        {
            _scheduler.Enable(day, (string)SchedTime.SelectedItem, Environment.ProcessPath!, App.ScheduledReportArg);
            RefreshSchedule();
        }
        catch (Exception ex) when (ex is IOException or ArgumentException)
        {
            SchedStatus.Text = "Could not turn it on: " + ex.Message;
        }
    }

    private void SchedOff_Click(object s, RoutedEventArgs e) { _scheduler.Disable(); RefreshSchedule(); }

    private async void SchedNow_Click(object s, RoutedEventArgs e)
    {
        SchedStatus.Text = "Making a report...";
        var path = await ReportScheduler.RunReportAsync();
        RefreshSchedule();
        SchedList.SelectedIndex = 0;
        SchedStatus.Text = "Report saved. It is selected below.";
    }

    private void SchedList_Changed(object s, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (SchedList.SelectedItem is PastReport r && File.Exists(r.Path)) SchedView.Text = File.ReadAllText(r.Path);
    }

    private void SchedFolder_Click(object s, RoutedEventArgs e)
    {
        Directory.CreateDirectory(_scheduler.ReportsFolder);
        Process.Start(new ProcessStartInfo("explorer.exe", $"\"{_scheduler.ReportsFolder}\"") { UseShellExecute = true });
    }
}

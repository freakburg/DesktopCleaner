using System.IO;
using System.Windows.Controls;
using DesktopCleaner.Core.Deletion;

namespace DesktopCleaner.App;

/// <summary>
/// Drives a tab's progress bar and status line while files are being deleted: "Deleting 120 of 450: report.pdf".
/// A removal can be split into several batches (one per duplicate group, say); this adds them up into one overall bar.
/// </summary>
public sealed class DeleteBar
{
    private readonly ProgressBar _bar;
    private readonly TextBlock _status;
    private readonly string _verb;
    private readonly int _total;
    private int _base;
    private bool _closed;

    public DeleteBar(ProgressBar bar, TextBlock status, string verb, int total)
    {
        _bar = bar; _status = status; _verb = verb; _total = Math.Max(1, total);
        bar.IsIndeterminate = false; bar.Minimum = 0; bar.Maximum = _total; bar.Value = 0;
        status.Text = $"{verb} 0 of {_total:N0}...";
    }

    /// <summary>Progress for the next batch. Create it on the UI thread so updates are delivered there.</summary>
    public IProgress<DeleteProgress> Batch() => new Progress<DeleteProgress>(p => Show(_base + p.Done, p.Current));

    public void BatchDone(int size) => _base += size;

    private void Show(int done, string? current)
    {
        if (_closed) return;
        done = Math.Min(done, _total);
        _bar.Value = done;
        _status.Text = $"{_verb} {done:N0} of {_total:N0}" + (string.IsNullOrEmpty(current) ? "" : ": " + Path.GetFileName(current.TrimEnd('\\')));
    }

    /// <summary>Call when finished so late progress messages cannot move the bar again.</summary>
    public void Close() { _closed = true; _bar.Value = 0; }
}

using System.Text;
using DesktopCleaner.Core.Deletion;

namespace DesktopCleaner.Core.Cleanup;

public static class ReportWriter
{
    public static string DefaultFolder => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DesktopCleaner", "reports");

    public static string Render(IEnumerable<RuleReport> reports, bool applied, DateTime? when = null)
    {
        var list = reports.ToList();
        var sb = new StringBuilder();
        sb.AppendLine($"# Cleanup report - {(when ?? DateTime.Now):yyyy-MM-dd HH:mm}");
        sb.AppendLine(applied ? "_Mode: applied (files sent to Recycle Bin)_" : "_Mode: dry run (nothing was deleted)_").AppendLine();
        foreach (var r in list)
        {
            sb.AppendLine($"## {r.Rule.Name}");
            sb.AppendLine($"Rule: files older than {r.Rule.OlderThanDays} days in {string.Join(", ", r.Rule.Folders)}");
            sb.AppendLine($"- Matching files: {r.Candidates.Count}");
            sb.AppendLine($"- Recoverable: {Bytes(r.RecoverableBytes)}");
            if (applied)
            {
                sb.AppendLine($"- Freed: {Bytes(r.FreedBytes)} ({r.Outcomes.Count(o => o.Succeeded)} files)");
                sb.AppendLine($"- Not removed: {r.Outcomes.Count(o => !o.Succeeded)}");
            }
            sb.AppendLine($"- Skipped (in use / unreadable / unsafe): {r.Skipped.Count}").AppendLine();
            foreach (var c in r.Candidates.OrderByDescending(c => c.Size).Take(20))
                sb.AppendLine($"  - {c.Path} ({Bytes(c.Size)}, {c.LastWriteUtc.ToLocalTime():yyyy-MM-dd})");
            if (r.Candidates.Count > 20) sb.AppendLine($"  - ... and {r.Candidates.Count - 20} more");
            sb.AppendLine();
        }
        sb.AppendLine($"**Total recoverable: {Bytes(list.Sum(r => r.RecoverableBytes))}**");
        return sb.ToString();
    }

    public static string Save(string content, string? folder = null)
    {
        folder ??= DefaultFolder;
        Directory.CreateDirectory(folder);
        var path = Path.Combine(folder, $"cleanup-{DateTime.Now:yyyyMMdd-HHmmss}.md");
        File.WriteAllText(path, content);
        return path;
    }

    public static string Bytes(long b) => b switch
    {
        >= 1L << 30 => $"{b / (double)(1L << 30):F2} GB",
        >= 1L << 20 => $"{b / (double)(1L << 20):F1} MB",
        >= 1L << 10 => $"{b / 1024.0:F0} KB",
        _ => $"{b} B",
    };
}


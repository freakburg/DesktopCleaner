using System.Net;
using System.Text;
using DesktopCleaner.Core.Storage;

namespace DesktopCleaner.Core.History;

/// <summary>Turns a storage check into a spreadsheet-friendly CSV or a printable page (use Print to PDF in any browser).</summary>
public static class ReportExporter
{
    public static string ToCsv(StorageReport r)
    {
        var sb = new StringBuilder("Section,Name,Size (bytes),Share of total,Detail\r\n");
        double total = Math.Max(1, r.TotalBytes);
        foreach (var c in r.ByCategory) sb.AppendLine(Row("File type", c.Name, c.Bytes, c.Bytes / total, $"{c.Count} files"));
        foreach (var f in r.Root.Children) sb.AppendLine(Row("Folder", f.Name, f.Bytes, f.Bytes / total, f.Path));
        foreach (var f in r.LargestFiles) sb.AppendLine(Row("Large file", f.Name, f.Size, f.Size / total, f.Path));
        return sb.ToString();
    }

    private static string Row(string section, string name, long bytes, double share, string detail) =>
        string.Join(",", Csv(section), Csv(name), bytes, share.ToString("P1", System.Globalization.CultureInfo.InvariantCulture), Csv(detail));

    /// <summary>Quotes a value and defuses spreadsheet formulas (=, +, -, @) so a hostile file name cannot run as one.</summary>
    public static string Csv(string v)
    {
        if (v.Length > 0 && "=+-@\t\r".Contains(v[0])) v = "'" + v;
        return v.Contains('"') || v.Contains(',') || v.Contains('\n') ? "\"" + v.Replace("\"", "\"\"") + "\"" : v;
    }

    public static string ToHtml(StorageReport r, DateTime? when = null)
    {
        static string E(string s) => WebUtility.HtmlEncode(s);
        double total = Math.Max(1, r.TotalBytes);
        string Bar(long b) => $"<div class=\"bar\"><span style=\"width:{Math.Min(100, 100.0 * b / total):F1}%\"></span></div>";
        var sb = new StringBuilder();
        sb.Append("<!doctype html><html><head><meta charset=\"utf-8\"><title>Storage report</title><style>")
          .Append("body{font:14px Segoe UI,sans-serif;margin:32px;color:#222}h1{margin:0}h2{margin-top:28px}table{border-collapse:collapse;width:100%}")
          .Append("td,th{padding:5px 8px;border-bottom:1px solid #ddd;text-align:left}td.n{text-align:right;white-space:nowrap}")
          .Append(".bar{background:#eee;height:10px;min-width:120px}.bar span{display:block;height:10px;background:#2e86c1}.muted{color:#666}</style></head><body>");
        sb.Append($"<h1>Storage report</h1><p class=\"muted\">{E(r.Root.Path)} &middot; {(when ?? DateTime.Now):MMMM d, yyyy h:mm tt} &middot; {Fmt(r.TotalBytes)} in {r.FileCount:N0} files</p>");
        sb.Append("<h2>By file type</h2><table>");
        foreach (var c in r.ByCategory) sb.Append($"<tr><td>{E(c.Name)}</td><td>{Bar(c.Bytes)}</td><td class=\"n\">{Fmt(c.Bytes)}</td></tr>");
        sb.Append("</table><h2>Biggest folders</h2><table>");
        foreach (var f in r.Root.Children.Take(15)) sb.Append($"<tr><td title=\"{E(f.Path)}\">{E(f.Name)}</td><td>{Bar(f.Bytes)}</td><td class=\"n\">{Fmt(f.Bytes)}</td></tr>");
        sb.Append("</table><h2>Biggest files</h2><table>");
        foreach (var f in r.LargestFiles) sb.Append($"<tr><td>{E(f.Name)}<br><span class=\"muted\">{E(f.Folder)}</span></td><td class=\"n\">{Fmt(f.Size)}</td></tr>");
        sb.Append("</table></body></html>");
        return sb.ToString();
    }

    private static string Fmt(long b) => b switch
    {
        >= 1L << 30 => $"{b / (double)(1L << 30):F2} GB",
        >= 1L << 20 => $"{b / (double)(1L << 20):F1} MB",
        >= 1L << 10 => $"{b / 1024.0:F0} KB",
        _ => $"{b} B",
    };
}

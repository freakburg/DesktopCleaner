namespace DesktopCleaner.Core.Storage;

public sealed record TreemapItem(string Key, long Size);
public sealed record TreemapRect(string Key, long Size, double X, double Y, double Width, double Height);

/// <summary>Squarified treemap layout (Bruls, Huizing, van Wijk): tiles with areas proportional to size and aspect ratios near 1.</summary>
public static class Treemap
{
    public static IReadOnlyList<TreemapRect> Layout(IEnumerable<TreemapItem> items, double width, double height)
    {
        var list = items.Where(i => i.Size > 0).OrderByDescending(i => i.Size).ToList();
        var result = new List<TreemapRect>();
        if (list.Count == 0 || width <= 0 || height <= 0) return result;

        double total = list.Sum(i => (double)i.Size);
        double scale = width * height / total;
        double x = 0, y = 0, w = width, h = height;
        var row = new List<TreemapItem>();
        int idx = 0;
        while (idx < list.Count)
        {
            double side = Math.Min(w, h);
            var next = list[idx];
            if (row.Count == 0 || Worst(row, next, side, scale) <= Worst(row, null, side, scale))
            {
                row.Add(next); idx++;
            }
            else
            {
                Place(row, ref x, ref y, ref w, ref h, scale, result);
                row.Clear();
            }
        }
        if (row.Count > 0) Place(row, ref x, ref y, ref w, ref h, scale, result);
        return result;
    }

    /// <summary>Worst aspect ratio of the row if <paramref name="extra"/> were added.</summary>
    private static double Worst(List<TreemapItem> row, TreemapItem? extra, double side, double scale)
    {
        double sum = 0, max = double.MinValue, min = double.MaxValue;
        foreach (var i in extra is null ? row : row.Append(extra))
        {
            var a = i.Size * scale; sum += a; max = Math.Max(max, a); min = Math.Min(min, a);
        }
        double s2 = side * side, sum2 = sum * sum;
        return Math.Max(s2 * max / sum2, sum2 / (s2 * min));
    }

    private static void Place(List<TreemapItem> row, ref double x, ref double y, ref double w, ref double h, double scale, List<TreemapRect> output)
    {
        double area = row.Sum(i => i.Size * scale);
        if (w >= h)
        {   // lay the row out as a column on the left edge
            double colW = area / h, cy = y;
            foreach (var i in row) { var ih = i.Size * scale / colW; output.Add(new(i.Key, i.Size, x, cy, colW, ih)); cy += ih; }
            x += colW; w -= colW;
        }
        else
        {   // lay the row out as a strip along the top edge
            double rowH = area / w, cx = x;
            foreach (var i in row) { var iw = i.Size * scale / rowH; output.Add(new(i.Key, i.Size, cx, y, iw, rowH)); cx += iw; }
            y += rowH; h -= rowH;
        }
    }
}

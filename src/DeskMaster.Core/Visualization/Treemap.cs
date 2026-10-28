namespace DeskMaster.Core.Visualization;

public readonly record struct TreemapRect(double X, double Y, double Width, double Height)
{
    public double Area => Width * Height;
}

public readonly record struct TreemapTile<T>(T Item, TreemapRect Bounds);

/// <summary>Squarified treemap layout (Bruls, Huizing, van Wijk) keeping tiles close to square.</summary>
public static class Treemap
{
    public static IReadOnlyList<TreemapTile<T>> Layout<T>(IEnumerable<T> items, Func<T, double> weight, TreemapRect bounds)
    {
        var entries = items
            .Select(i => (Item: i, Weight: weight(i)))
            .Where(e => e.Weight > 0)
            .OrderByDescending(e => e.Weight)
            .ToList();

        var result = new List<TreemapTile<T>>(entries.Count);
        var total = entries.Sum(e => e.Weight);
        if (entries.Count == 0 || total <= 0 || bounds.Width <= 0 || bounds.Height <= 0)
            return result;

        // Scale weights to areas.
        var scale = bounds.Area / total;
        var areas = entries.Select(e => (e.Item, Area: e.Weight * scale)).ToList();

        var rect = bounds;
        var row = new List<(T Item, double Area)>();
        var index = 0;
        while (index < areas.Count)
        {
            var side = Math.Min(rect.Width, rect.Height);
            var candidate = areas[index];
            if (row.Count == 0 || Worst(row, side, null) >= Worst(row, side, candidate.Area))
            {
                row.Add(candidate);
                index++;
            }
            else
            {
                rect = PlaceRow(row, rect, result);
                row.Clear();
            }
        }
        if (row.Count > 0)
            PlaceRow(row, rect, result);
        return result;
    }

    /// <summary>Worst aspect ratio of a row, optionally with one extra area appended.</summary>
    private static double Worst<T>(List<(T Item, double Area)> row, double side, double? extra)
    {
        double sum = 0, min = double.MaxValue, max = 0;
        foreach (var (_, area) in row)
        {
            sum += area;
            min = Math.Min(min, area);
            max = Math.Max(max, area);
        }
        if (extra is { } e)
        {
            sum += e;
            min = Math.Min(min, e);
            max = Math.Max(max, e);
        }
        if (sum <= 0 || min <= 0)
            return double.MaxValue;
        var s2 = side * side;
        var sum2 = sum * sum;
        return Math.Max(s2 * max / sum2, sum2 / (s2 * min));
    }

    private static TreemapRect PlaceRow<T>(List<(T Item, double Area)> row, TreemapRect rect, List<TreemapTile<T>> output)
    {
        var rowArea = row.Sum(r => r.Area);
        if (rect.Width >= rect.Height)
        {
            // Lay the row out as a vertical column on the left.
            var columnWidth = rect.Height > 0 ? rowArea / rect.Height : 0;
            var y = rect.Y;
            foreach (var (item, area) in row)
            {
                var h = columnWidth > 0 ? area / columnWidth : 0;
                output.Add(new TreemapTile<T>(item, new TreemapRect(rect.X, y, columnWidth, h)));
                y += h;
            }
            return new TreemapRect(rect.X + columnWidth, rect.Y, Math.Max(0, rect.Width - columnWidth), rect.Height);
        }
        else
        {
            var rowHeight = rect.Width > 0 ? rowArea / rect.Width : 0;
            var x = rect.X;
            foreach (var (item, area) in row)
            {
                var w = rowHeight > 0 ? area / rowHeight : 0;
                output.Add(new TreemapTile<T>(item, new TreemapRect(x, rect.Y, w, rowHeight)));
                x += w;
            }
            return new TreemapRect(rect.X, rect.Y + rowHeight, rect.Width, Math.Max(0, rect.Height - rowHeight));
        }
    }
}

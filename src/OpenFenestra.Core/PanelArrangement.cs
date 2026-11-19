namespace OpenFenestra.Core;

public enum PanelArrangementMode { Free, CompactGrid, Shelf }
public sealed record PanelBounds(double X, double Y, double Width, double Height, bool Collapsed = false);
public sealed record GridArrangement(IReadOnlyList<PanelBounds> Cells, int Capacity);

public static class PanelArrangement
{
    public const double MinimumWidth = 220, MinimumHeight = 180;

    public static GridArrangement Grid(int count, PanelBounds area)
    {
        const double margin = 12, gap = 8;
        var width = area.Width - margin * 2; var height = area.Height - margin * 2;
        var columns = Math.Max(0, (int)Math.Floor((width + gap) / (MinimumWidth + gap)));
        var maxRows = Math.Max(0, (int)Math.Floor((height + gap) / (MinimumHeight + gap)));
        var capacity = columns * maxRows;
        if (count <= 0 || count > capacity) return new([], capacity);
        columns = Math.Min(columns, count);
        var rows = (int)Math.Ceiling((double)count / columns);
        var cellWidth = Math.Min(280, (width - gap * (columns - 1)) / columns);
        var cellHeight = Math.Min(220, (height - gap * (rows - 1)) / rows);
        return new(Enumerable.Range(0, count).Select(i => new PanelBounds(
            area.X + margin + i % columns * (cellWidth + gap), area.Y + margin + i / columns * (cellHeight + gap),
            cellWidth, cellHeight)).ToArray(), capacity);
    }

    public static PanelBounds ShelfPopup(PanelBounds area, PanelBounds shelf, FenceGroup group)
    {
        var width = Math.Clamp(group.Width, MinimumWidth, Math.Max(MinimumWidth, shelf.X - area.X - 24));
        var height = Math.Clamp(group.Height, MinimumHeight, Math.Max(MinimumHeight, area.Height - 24));
        return new(Math.Max(area.X + 12, shelf.X - width - 8),
            Math.Clamp(group.Y, area.Y + 12, Math.Max(area.Y + 12, area.Y + area.Height - height - 12)), width, height);
    }
}

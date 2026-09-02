namespace ShinyGo60.Companion.Core.Presentation;

public static class TaskbarWidgetPlacementCalculator
{
    private const int DefaultDpi = 96;
    private const int HorizontalWidthDip = 252;
    private const int VerticalHeightDip = 116;
    private const int MarginDip = 5;

    public static TaskbarWidgetPlacement Calculate(TaskbarGeometry geometry)
    {
        ArgumentNullException.ThrowIfNull(geometry);
        if (geometry.Dpi == 0)
        {
            throw new ArgumentOutOfRangeException(nameof(geometry), "Taskbar DPI must be greater than zero.");
        }

        PixelRectangle taskbar = geometry.TaskbarBounds;
        int margin = Scale(MarginDip, geometry.Dpi);
        bool isHorizontal = geometry.Edge is TaskbarEdge.Top or TaskbarEdge.Bottom;
        int taskbarThickness = isHorizontal ? taskbar.Height : taskbar.Width;
        if (taskbarThickness == 0)
        {
            return TaskbarWidgetPlacement.Hidden;
        }

        int availableWidth = taskbar.Width - (margin * 2);
        int availableHeight = taskbar.Height - (margin * 2);
        if (availableWidth <= 0 || availableHeight <= 0)
        {
            return TaskbarWidgetPlacement.Hidden;
        }

        int width = isHorizontal
            ? Math.Min(Scale(HorizontalWidthDip, geometry.Dpi), availableWidth)
            : availableWidth;
        int height = isHorizontal
            ? availableHeight
            : Math.Min(Scale(VerticalHeightDip, geometry.Dpi), availableHeight);
        int left = taskbar.Left + margin;
        int top = taskbar.Top + margin;
        return new TaskbarWidgetPlacement(
            true,
            new PixelRectangle(left, top, left + width, top + height));
    }

    private static int Scale(int value, uint dpi)
    {
        return checked((int)Math.Round(value * dpi / (double)DefaultDpi, MidpointRounding.AwayFromZero));
    }
}

namespace ShinyGo60.Companion.Core.Presentation;

public sealed record TaskbarWidgetPlacement(bool IsVisible, PixelRectangle Bounds)
{
    public static TaskbarWidgetPlacement Hidden { get; } = new(false, new PixelRectangle(0, 0, 0, 0));
}

namespace ShinyGo60.Companion.Core.Presentation;

public sealed record TaskbarGeometry(
    PixelRectangle TaskbarBounds,
    TaskbarEdge Edge,
    uint Dpi);

namespace ShinyGo60.Companion.Core.Presentation;

public readonly record struct PixelRectangle(int Left, int Top, int Right, int Bottom)
{
    public int Width => Math.Max(0, this.Right - this.Left);

    public int Height => Math.Max(0, this.Bottom - this.Top);
}

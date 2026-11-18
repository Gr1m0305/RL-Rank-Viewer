namespace RLOverlay.Core.Config;

/// <summary>A rectangle in physical screen pixels.</summary>
public readonly record struct PixelRect(int Left, int Top, int Width, int Height)
{
    public int Right => Left + Width;

    public int Bottom => Top + Height;
}

/// <summary>
/// Where the overlay window goes: the top-right corner of the game's monitor, <see cref="EdgeMargin"/> in from the
/// edges, moved by the user's offset (dragging the screen changes the offset).
/// </summary>
public static class PanelPlacement
{
    /// <summary>Gap between the screen and the monitor's top and right edges, in DIPs.</summary>
    public const double EdgeMargin = 16;

    /// <param name="monitor">Monitor bounds in physical pixels.</param>
    /// <param name="windowWidth">Overlay window width in physical pixels.</param>
    /// <param name="windowHeight">Overlay window height in physical pixels.</param>
    /// <param name="offsetX">User offset in DIPs (negative = further left).</param>
    /// <param name="offsetY">User offset in DIPs (positive = further down).</param>
    /// <param name="dpiScale">Monitor DPI scale (1.0 = 96 DPI).</param>
    /// <param name="shadowMargin">Transparent margin around the panel inside the window, in physical pixels.</param>
    public static (int X, int Y) TopRight(
        PixelRect monitor, int windowWidth, int windowHeight, double offsetX, double offsetY, double dpiScale, int shadowMargin = 0)
    {
        var (anchorX, anchorY) = Anchor(monitor, windowWidth, dpiScale, shadowMargin);
        var x = anchorX + (offsetX * dpiScale);
        var y = anchorY + (offsetY * dpiScale);

        // Keep the panel on the monitor (its shadow may hang over the edge).
        x = Math.Clamp(x, monitor.Left - shadowMargin, Math.Max(monitor.Left - shadowMargin, monitor.Right - windowWidth + shadowMargin));
        y = Math.Clamp(y, monitor.Top - shadowMargin, Math.Max(monitor.Top - shadowMargin, monitor.Bottom - windowHeight + shadowMargin));
        return ((int)Math.Round(x), (int)Math.Round(y));
    }

    /// <summary>The offset (DIPs) that puts a window of this width at (<paramref name="x"/>, <paramref name="y"/>).</summary>
    public static (double OffsetX, double OffsetY) OffsetFor(
        PixelRect monitor, int windowWidth, double x, double y, double dpiScale, int shadowMargin = 0)
    {
        var (anchorX, anchorY) = Anchor(monitor, windowWidth, dpiScale, shadowMargin);
        var scale = dpiScale > 0 ? dpiScale : 1;
        return ((x - anchorX) / scale, (y - anchorY) / scale);
    }

    /// <summary>
    /// Resizing by dragging a corner of the panel. The scale follows the cursor along the diagonal (moving the corner
    /// outwards grows it), stays within <paramref name="minScale"/> to <paramref name="maxScale"/>, and the opposite
    /// corner stays where it was.
    /// </summary>
    /// <param name="window">The window when the drag started, in physical pixels (panel plus shadow margin).</param>
    /// <param name="left">A left corner is dragged (else a right one).</param>
    /// <param name="top">A top corner is dragged (else a bottom one).</param>
    /// <param name="dx">Cursor movement since the drag started, in physical pixels.</param>
    /// <param name="dy">Cursor movement since the drag started, in physical pixels.</param>
    /// <returns>The new scale and the window's new size and position.</returns>
    public static (double Scale, PixelRect Window) ResizeFromCorner(
        PixelRect window, int shadowMargin, double startScale, bool left, bool top, int dx, int dy, double minScale, double maxScale)
    {
        var panelW = Math.Max(1, window.Width - (2 * shadowMargin));
        var panelH = Math.Max(1, window.Height - (2 * shadowMargin));
        var grow = ((left ? -dx : dx) + (top ? -dy : dy)) / (double)(panelW + panelH);
        var scale = Math.Clamp(Math.Round(startScale * (1 + grow), 3), minScale, maxScale);
        var ratio = scale / startScale;
        var w = (int)Math.Round(panelW * ratio) + (2 * shadowMargin);
        var h = (int)Math.Round(panelH * ratio) + (2 * shadowMargin);
        var x = left ? window.Right - w : window.Left;
        var y = top ? window.Bottom - h : window.Top;
        return (scale, new PixelRect(x, y, w, h));
    }

    /// <summary>Window position with no offset: the panel's top-right corner at the margin.</summary>
    private static (double X, double Y) Anchor(PixelRect monitor, int windowWidth, double dpiScale, int shadowMargin)
    {
        var margin = EdgeMargin * dpiScale;
        return (monitor.Right - margin - windowWidth + shadowMargin, monitor.Top + margin - shadowMargin);
    }
}

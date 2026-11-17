namespace RLOverlay.Core.Config;

/// <summary>A rectangle in physical screen pixels.</summary>
public readonly record struct PixelRect(int Left, int Top, int Width, int Height)
{
    public int Right => Left + Width;

    public int Bottom => Top + Height;
}

/// <summary>Where the overlay window goes: top-centre of the game's monitor, just below the scoreboard.</summary>
public static class PanelPlacement
{
    /// <summary>The anchor sits this fraction of the monitor height from the top (below the in-game scoreboard).</summary>
    public const double AnchorFromTop = 0.095;

    /// <param name="monitor">Monitor bounds in physical pixels.</param>
    /// <param name="windowWidth">Overlay window width in physical pixels.</param>
    /// <param name="windowHeight">Overlay window height in physical pixels.</param>
    /// <param name="offsetX">User offset in DIPs.</param>
    /// <param name="offsetY">User offset in DIPs.</param>
    /// <param name="dpiScale">Monitor DPI scale (1.0 = 96 DPI).</param>
    /// <param name="shadowMargin">Transparent margin around the panel inside the window, in physical pixels.</param>
    public static (int X, int Y) TopCenter(
        PixelRect monitor, int windowWidth, int windowHeight, double offsetX, double offsetY, double dpiScale, int shadowMargin = 0)
    {
        var x = monitor.Left + ((monitor.Width - windowWidth) / 2.0) + (offsetX * dpiScale);
        var y = monitor.Top + (monitor.Height * AnchorFromTop) - shadowMargin + (offsetY * dpiScale);

        // Keep the window on the monitor.
        x = Math.Clamp(x, monitor.Left - shadowMargin, Math.Max(monitor.Left - shadowMargin, monitor.Right - windowWidth + shadowMargin));
        y = Math.Clamp(y, monitor.Top - shadowMargin, Math.Max(monitor.Top - shadowMargin, monitor.Bottom - windowHeight + shadowMargin));
        return ((int)Math.Round(x), (int)Math.Round(y));
    }
}

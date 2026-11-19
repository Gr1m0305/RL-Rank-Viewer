using System.Globalization;

namespace RLOverlay.Core.Config;

/// <summary>
/// A connected screen: Windows' device name (<c>\\.\DISPLAY2</c>), the monitor's own name when Windows knows it, its
/// bounds (physical pixels, on the virtual desktop) and whether it is the main screen.
/// </summary>
public sealed record ScreenInfo(string DeviceName, string? FriendlyName, PixelRect Bounds, bool IsPrimary)
{
    /// <summary>The number in the device name (2 for <c>\\.\DISPLAY2</c>), or null when it has none.</summary>
    public int? Number
    {
        get
        {
            var digits = new string(DeviceName.Reverse().TakeWhile(char.IsAsciiDigit).Reverse().ToArray());
            return int.TryParse(digits, NumberStyles.None, CultureInfo.InvariantCulture, out var n) ? n : null;
        }
    }

    /// <summary>"Screen 2 · DELL U2719D · 1920 × 1080 · main" (the parts Windows knows).</summary>
    public string Label
    {
        get
        {
            var parts = new List<string> { Number is { } n ? $"Screen {n}" : DeviceName };
            if (!string.IsNullOrWhiteSpace(FriendlyName))
            {
                parts.Add(FriendlyName.Trim());
            }

            parts.Add($"{Bounds.Width} × {Bounds.Height}");
            if (IsPrimary)
            {
                parts.Add("main");
            }

            return string.Join(" · ", parts);
        }
    }
}

/// <summary>Which screen the overlay goes on (<see cref="OverlaySettings.Screen"/>).</summary>
public static class ScreenChoice
{
    /// <summary>
    /// The chosen screen while it is connected; otherwise (nothing chosen, or that screen is unplugged) the screen
    /// Rocket League is on, and the main screen when the game's window isn't found.
    /// </summary>
    /// <param name="chosen">Device name from the settings, or null for the game's screen.</param>
    /// <param name="screens">The screens connected now.</param>
    /// <param name="gameScreen">The game window's screen, or null; only asked when needed (finding the window costs).</param>
    /// <param name="primary">The main screen.</param>
    public static PixelRect Pick(string? chosen, IEnumerable<ScreenInfo> screens, Func<PixelRect?> gameScreen, PixelRect primary)
    {
        if (Find(chosen, screens) is { } screen)
        {
            return screen.Bounds;
        }

        return gameScreen() ?? primary;
    }

    /// <summary>The connected screen with this device name, or null.</summary>
    public static ScreenInfo? Find(string? deviceName, IEnumerable<ScreenInfo> screens) =>
        string.IsNullOrWhiteSpace(deviceName)
            ? null
            : screens.FirstOrDefault(s => string.Equals(s.DeviceName, deviceName.Trim(), StringComparison.OrdinalIgnoreCase));
}

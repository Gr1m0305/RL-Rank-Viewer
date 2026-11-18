using System.IO;

namespace RLOverlay.App.Services;

/// <summary>Every file location the app uses.</summary>
internal static class AppPaths
{
    public static string Root { get; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "RLOverlay");

    public static string SettingsFile => Path.Combine(Root, "settings.json");

    public static string LogsDir => Path.Combine(Root, "logs");

    /// <summary>Browser profile for the tracker.gg pages on the overlay screen (their cookies and cache).</summary>
    public static string WebViewDataDir => Path.Combine(Root, "webview");
}

using System.IO;

namespace RLOverlay.App.Services;

/// <summary>Every file location the app uses.</summary>
internal static class AppPaths
{
    public static string Root { get; private set; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "RLOverlay");

    /// <summary>Downloaded updates wait here until they are installed.</summary>
    public static string UpdatesDir => Path.Combine(Root, "updates");

    /// <summary>
    /// <c>--data-dir</c> (testing): settings, logs, downloads and the browser profile go in this folder instead, so a test
    /// copy shares nothing with an installed one (not even its browser processes).
    /// </summary>
    public static void UseRoot(string directory) => Root = directory;

    public static string SettingsFile => Path.Combine(Root, "settings.json");

    /// <summary>The players saved with the star (see <see cref="RLOverlay.Core.Players.FavouritesStore"/>).</summary>
    public static string FavouritesFile => Path.Combine(Root, "favourites.json");

    public static string LogsDir => Path.Combine(Root, "logs");

    /// <summary>Browser profile for the tracker.gg pages on the overlay screen (their cookies and cache).</summary>
    public static string WebViewDataDir => Path.Combine(Root, "webview");
}

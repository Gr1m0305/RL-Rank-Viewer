using System.IO;

namespace RLOverlay.App.Services;

/// <summary>Every file location the app uses.</summary>
internal static class AppPaths
{
    public static string Root { get; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "RLOverlay");

    public static string SettingsFile => Path.Combine(Root, "settings.json");

    public static string LogsDir => Path.Combine(Root, "logs");

    public static string ProfileCacheDir => Path.Combine(Root, "cache", "profiles");

    public static string RankCacheDir => Path.Combine(Root, "cache", "ranks");

    /// <summary>User override folder for rank icons (wins over the exe folder).</summary>
    public static string RankOverrideDir => Path.Combine(Root, "ranks");

    /// <summary>Folder that holds RLOverlay.exe (for single-file publish this is the exe's folder, not the temp extract dir).</summary>
    public static string ExeDir { get; } = Path.GetDirectoryName(Environment.ProcessPath) ?? AppContext.BaseDirectory;

    public static string RankAssetsDir => Path.Combine(ExeDir, "assets", "ranks");
}

using System.Text.Json;
using System.Text.RegularExpressions;

namespace RLOverlay.Core.Config;

/// <summary>Where to look for Rocket League. The app fills this from the registry and known folders.</summary>
public sealed record LocatorInputs
{
    /// <summary>Steam's folder (registry <c>HKCU\Software\Valve\Steam\SteamPath</c>).</summary>
    public string? SteamRoot { get; init; }

    /// <summary>Epic Games Launcher manifests (<c>%ProgramData%\Epic\EpicGamesLauncher\Data\Manifests</c>).</summary>
    public string? EpicManifestsDir { get; init; }

    /// <summary>The user's Documents folder (for <c>My Games\Rocket League</c>).</summary>
    public string? DocumentsDir { get; init; }

    /// <summary>Default install folders to try when the launchers are not found.</summary>
    public IReadOnlyList<string> Guesses { get; init; } = [];
}

/// <summary>
/// Finds Rocket League installs and the Stats API ini files, using launcher files only (Steam library list and app
/// manifest, Epic manifests). It never looks at the game process.
/// </summary>
public static partial class RocketLeagueLocator
{
    public const string SteamAppId = "252950";
    public const string EpicAppName = "Sugar";

    public static IReadOnlyList<string> FindInstalls(LocatorInputs inputs)
    {
        var found = new List<string>();
        found.AddRange(FromSteam(inputs.SteamRoot));
        found.AddRange(FromEpic(inputs.EpicManifestsDir));
        found.AddRange(inputs.Guesses);
        return found
            .Where(d => !string.IsNullOrWhiteSpace(d))
            .Select(d => Path.GetFullPath(d.Trim()))
            .Where(LooksLikeInstall)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>An install has the game's <c>TAGame</c> data folder or its exe.</summary>
    public static bool LooksLikeInstall(string dir) =>
        Directory.Exists(Path.Combine(dir, "TAGame")) || File.Exists(Path.Combine(dir, "Binaries", "Win64", "RocketLeague.exe"));

    /// <summary>
    /// Every ini file the game may read for the Stats API, in the order the game prefers them:
    /// <c>&lt;install&gt;\TAGame\Config\TAStatsAPI.ini</c>, the generated copy under Documents, then
    /// <c>&lt;install&gt;\TAGame\Config\DefaultStatsAPI.ini</c>.
    /// </summary>
    public static IReadOnlyList<string> IniCandidates(IReadOnlyList<string> installs, string? documentsDir)
    {
        var list = new List<string>();
        list.AddRange(installs.Select(i => Path.Combine(i, "TAGame", "Config", "TAStatsAPI.ini")));
        if (!string.IsNullOrWhiteSpace(documentsDir))
        {
            list.Add(Path.Combine(documentsDir, "My Games", "Rocket League", "TAGame", "Config", "TAStatsAPI.ini"));
        }

        list.AddRange(installs.Select(i => Path.Combine(i, "TAGame", "Config", "DefaultStatsAPI.ini")));
        return list;
    }

    /// <summary>
    /// The files to edit: every candidate that exists. When none exists, <c>DefaultStatsAPI.ini</c> is created in the
    /// first install (null when no install was found).
    /// </summary>
    public static (IReadOnlyList<string> Edit, string? Create) PlanIniEdit(IReadOnlyList<string> installs, string? documentsDir, Func<string, bool>? exists = null)
    {
        exists ??= File.Exists;
        var edit = IniCandidates(installs, documentsDir).Where(exists).ToList();
        var create = edit.Count == 0 && installs.Count > 0 ? Path.Combine(installs[0], "TAGame", "Config", "DefaultStatsAPI.ini") : null;
        return (edit, create);
    }

    /// <summary>The port in the first ini (by the game's preference) that sets one; null if none is readable.</summary>
    public static int? ConfiguredPort(IReadOnlyList<string> installs, string? documentsDir)
    {
        foreach (var path in IniCandidates(installs, documentsDir))
        {
            if (StatsApiIni.ReadFile(path) is { HasSection: true } values)
            {
                return values.EffectivePort > 0 ? values.EffectivePort : null;
            }
        }

        return null;
    }

    internal static IEnumerable<string> FromSteam(string? steamRoot)
    {
        if (string.IsNullOrWhiteSpace(steamRoot))
        {
            yield break;
        }

        var libraries = new List<string> { steamRoot };
        var vdf = Path.Combine(steamRoot, "steamapps", "libraryfolders.vdf");
        if (TryRead(vdf) is { } vdfText)
        {
            libraries.AddRange(SteamLibraryPaths(vdfText));
        }

        foreach (var library in libraries.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var steamapps = Path.Combine(library, "steamapps");
            var installDir = TryRead(Path.Combine(steamapps, $"appmanifest_{SteamAppId}.acf")) is { } acf
                ? SteamInstallDir(acf) ?? "rocketleague"
                : "rocketleague";
            yield return Path.Combine(steamapps, "common", installDir);
        }
    }

    internal static IEnumerable<string> FromEpic(string? manifestsDir)
    {
        if (string.IsNullOrWhiteSpace(manifestsDir) || !Directory.Exists(manifestsDir))
        {
            yield break;
        }

        foreach (var file in SafeEnumerate(manifestsDir, "*.item"))
        {
            if (TryRead(file) is { } json && EpicInstallLocation(json) is { } location)
            {
                yield return location;
            }
        }
    }

    /// <summary>The <c>"path"</c> entries of Steam's <c>libraryfolders.vdf</c>.</summary>
    internal static IEnumerable<string> SteamLibraryPaths(string vdfText) =>
        VdfPath().Matches(vdfText).Select(m => Unescape(m.Groups[1].Value));

    /// <summary>The <c>"installdir"</c> of a Steam app manifest.</summary>
    internal static string? SteamInstallDir(string acfText) =>
        VdfInstallDir().Match(acfText) is { Success: true } m ? Unescape(m.Groups[1].Value) : null;

    /// <summary>The install folder from an Epic manifest, if it is Rocket League.</summary>
    internal static string? EpicInstallLocation(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json, new JsonDocumentOptions { AllowTrailingCommas = true });
            var root = doc.RootElement;
            string? Get(string name) => root.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
            var isRocketLeague = string.Equals(Get("AppName"), EpicAppName, StringComparison.OrdinalIgnoreCase)
                || (Get("DisplayName")?.Contains("Rocket League", StringComparison.OrdinalIgnoreCase) ?? false);
            return isRocketLeague ? Get("InstallLocation") : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string Unescape(string value) => value.Replace(@"\\", @"\", StringComparison.Ordinal);

    private static string? TryRead(string path)
    {
        try
        {
            return File.Exists(path) ? File.ReadAllText(path) : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static IEnumerable<string> SafeEnumerate(string dir, string pattern)
    {
        try
        {
            return Directory.GetFiles(dir, pattern);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    [GeneratedRegex("\"path\"\\s+\"((?:[^\"\\\\]|\\\\.)*)\"", RegexOptions.IgnoreCase)]
    private static partial Regex VdfPath();

    [GeneratedRegex("\"installdir\"\\s+\"((?:[^\"\\\\]|\\\\.)*)\"", RegexOptions.IgnoreCase)]
    private static partial Regex VdfInstallDir();
}

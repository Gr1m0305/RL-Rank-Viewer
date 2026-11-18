using System.Globalization;

namespace RLOverlay.App.Services;

/// <summary>Command-line flags (see docs/ARCHITECTURE.md, "Launch flags").</summary>
internal sealed record AppArgs
{
    /// <summary>
    /// Play a built-in lobby of made-up players (placeholders where tracker.gg's rows go); no game needed. Implies
    /// <see cref="UngatedHotkeys"/>.
    /// </summary>
    public bool Demo { get; init; }

    /// <summary>Stats API port override.</summary>
    public int? Port { get; init; }

    /// <summary>Hotkeys work even when Rocket League is not the foreground window.</summary>
    public bool UngatedHotkeys { get; init; }

    /// <summary>Log profile URLs instead of opening the browser.</summary>
    public bool DryRunLinks { get; init; }

    /// <summary>Render the settings window and setup to PNGs in this folder and exit.</summary>
    public string? ScreenshotsDir { get; init; }

    /// <summary>Quit after this many seconds (automation).</summary>
    public double? ExitAfterSeconds { get; init; }

    /// <summary>Open the settings window on start.</summary>
    public bool OpenSettings { get; init; }

    /// <summary>Run only the Stats API ini edit (used when relaunched elevated) and exit.</summary>
    public bool EnableStatsApi { get; init; }

    /// <summary>
    /// Connect to the Stats API right away and keep trying, even while Rocket League is not running (testing with
    /// FakeStatsServer). Normally the app waits in standby and connects when the game starts.
    /// </summary>
    public bool AlwaysConnect { get; init; }

    /// <summary>Show the installer (also implied when the exe's file name contains "setup").</summary>
    public bool Setup { get; init; }

    /// <summary>Remove the installed copy (Apps &amp; features runs this).</summary>
    public bool Uninstall { get; init; }

    /// <summary>With <see cref="Setup"/> / <see cref="Uninstall"/>: no windows, default choices.</summary>
    public bool Quiet { get; init; }

    /// <summary>With <see cref="Quiet"/> setup: start the installed app when done (updates use this).</summary>
    public bool LaunchWhenDone { get; init; }

    /// <summary>Setup first waits (up to 20 s) for this process to exit: the copy being updated.</summary>
    public int? WaitForProcess { get; init; }

    /// <summary>
    /// Testing only: setup/uninstall put the install folder, shortcuts and registry entries under this folder and a
    /// scratch registry key instead of the real locations.
    /// </summary>
    public string? SetupSandbox { get; init; }

    /// <summary>Testing: keep settings, logs, downloads and the browser profile in this folder.</summary>
    public string? DataDir { get; init; }

    /// <summary>Testing: read releases from this address (a GitHub-style "latest release" JSON) instead of GitHub.</summary>
    public string? UpdateFeed { get; init; }

    /// <summary>Never look for updates.</summary>
    public bool NoUpdateCheck { get; init; }

    public IReadOnlyList<string> Unknown { get; init; } = [];

    public static AppArgs Parse(IReadOnlyList<string> args)
    {
        var result = new AppArgs();
        var unknown = new List<string>();
        for (var i = 0; i < args.Count; i++)
        {
            var arg = args[i];
            string? Next() => i + 1 < args.Count ? args[++i] : null;
            switch (arg.ToLowerInvariant())
            {
                case "--demo":
                    result = result with { Demo = true };
                    break;
                case "--port":
                    if (int.TryParse(Next(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var port) && port is > 0 and <= 65535)
                    {
                        result = result with { Port = port };
                    }

                    break;
                case "--ungated-hotkeys":
                    result = result with { UngatedHotkeys = true };
                    break;
                case "--dry-run-links":
                    result = result with { DryRunLinks = true };
                    break;
                case "--always-connect":
                    result = result with { AlwaysConnect = true };
                    break;
                case "--setup":
                    result = result with { Setup = true };
                    break;
                case "--uninstall":
                    result = result with { Uninstall = true };
                    break;
                case "--quiet":
                    result = result with { Quiet = true };
                    break;
                case "--launch":
                    result = result with { LaunchWhenDone = true };
                    break;
                case "--wait-for":
                    if (int.TryParse(Next(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var pid) && pid > 0)
                    {
                        result = result with { WaitForProcess = pid };
                    }

                    break;
                case "--setup-sandbox":
                    result = result with { SetupSandbox = Next() };
                    break;
                case "--screenshots":
                    result = result with { ScreenshotsDir = Next() ?? "docs/screenshots" };
                    break;
                case "--exit-after":
                    if (double.TryParse(Next(), NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds) && seconds > 0)
                    {
                        result = result with { ExitAfterSeconds = seconds };
                    }

                    break;
                case "--settings":
                    result = result with { OpenSettings = true };
                    break;
                case "--enable-stats-api":
                    result = result with { EnableStatsApi = true };
                    break;
                case "--data-dir":
                    result = result with { DataDir = Next() };
                    break;
                case "--update-feed":
                    result = result with { UpdateFeed = Next() };
                    break;
                case "--no-update-check":
                    result = result with { NoUpdateCheck = true };
                    break;
                default:
                    unknown.Add(arg);
                    break;
            }
        }

        return result with { Unknown = unknown };
    }
}

using System.Globalization;

namespace RLOverlay.App.Services;

/// <summary>Command-line flags (see docs/ARCHITECTURE.md, "Launch flags").</summary>
internal sealed record AppArgs
{
    /// <summary>Play a built-in lobby with mock stats; no game needed. Implies <see cref="UngatedHotkeys"/>.</summary>
    public bool Demo { get; init; }

    /// <summary>Force the mock stats provider (for testing against FakeStatsServer).</summary>
    public bool MockProvider { get; init; }

    /// <summary>Stats API port override.</summary>
    public int? Port { get; init; }

    /// <summary>Hotkeys work even when Rocket League is not the foreground window.</summary>
    public bool UngatedHotkeys { get; init; }

    /// <summary>Log profile URLs instead of opening the browser.</summary>
    public bool DryRunLinks { get; init; }

    /// <summary>Render the panel states to PNGs in this folder and exit.</summary>
    public string? ScreenshotsDir { get; init; }

    /// <summary>
    /// With <see cref="ScreenshotsDir"/>: draw the official rank icons (downloaded to the cache) instead of the
    /// placeholders. Off by default so the committed docs screenshots contain no rank artwork.
    /// </summary>
    public bool OfficialIcons { get; init; }

    /// <summary>Quit after this many seconds (automation).</summary>
    public double? ExitAfterSeconds { get; init; }

    /// <summary>Open the settings window on start.</summary>
    public bool OpenSettings { get; init; }

    /// <summary>Run only the Stats API ini edit (used when relaunched elevated) and exit.</summary>
    public bool EnableStatsApi { get; init; }

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
                case "--provider":
                    var name = Next();
                    if (string.Equals(name, "mock", StringComparison.OrdinalIgnoreCase))
                    {
                        result = result with { MockProvider = true };
                    }
                    else
                    {
                        unknown.Add($"--provider {name}");
                    }

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
                case "--screenshots":
                    result = result with { ScreenshotsDir = Next() ?? "docs/screenshots" };
                    break;
                case "--official-icons":
                    result = result with { OfficialIcons = true };
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
                default:
                    unknown.Add(arg);
                    break;
            }
        }

        return result with { Unknown = unknown };
    }
}

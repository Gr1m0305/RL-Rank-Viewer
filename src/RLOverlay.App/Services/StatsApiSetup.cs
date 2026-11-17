using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using Microsoft.Extensions.Logging;
using Microsoft.Win32;
using RLOverlay.App.Overlay;
using RLOverlay.Core.Config;

namespace RLOverlay.App.Services;

/// <summary>One Stats API ini file and what it currently says (null values = file missing or unreadable).</summary>
public sealed record StatsApiFileStatus(string Path, StatsApiIniValues? Values)
{
    public bool Exists => Values is not null;
}

/// <summary>The outcome of "Enable Stats API" for the UI.</summary>
public sealed record StatsApiEnableOutcome(bool Success, bool AnyChange, bool NeedsRestart, string Message, IReadOnlyList<StatsApiIniEditResult> Files);

/// <summary>
/// Finds Rocket League (Steam library files, Epic manifests, default folders; never the game process) and enables the
/// Stats API in its ini files on request, with a backup. When the install folder needs admin rights, the app relaunches
/// itself elevated with <c>--enable-stats-api</c> for just that edit.
/// </summary>
public sealed class StatsApiSetup
{
    private readonly ILogger _logger;

    public StatsApiSetup(ILogger<StatsApiSetup> logger) => _logger = logger;

    public static string DocumentsDir => Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);

    public IReadOnlyList<string> FindInstalls()
    {
        var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        var programFilesX86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
        var inputs = new LocatorInputs
        {
            SteamRoot = ReadSteamRoot(),
            EpicManifestsDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Epic", "EpicGamesLauncher", "Data", "Manifests"),
            DocumentsDir = DocumentsDir,
            Guesses =
            [
                Path.Combine(programFiles, "Epic Games", "rocketleague"),
                Path.Combine(programFilesX86, "Steam", "steamapps", "common", "rocketleague"),
            ],
        };
        var installs = RocketLeagueLocator.FindInstalls(inputs);
        _logger.LogInformation("Rocket League installs found: {Installs}", installs.Count == 0 ? "none" : string.Join("; ", installs));
        return installs;
    }

    public IReadOnlyList<StatsApiFileStatus> Status(IReadOnlyList<string> installs) =>
        RocketLeagueLocator.IniCandidates(installs, DocumentsDir)
            .Select(path => new StatsApiFileStatus(path, StatsApiIni.ReadFile(path)))
            .ToList();

    /// <summary>The port the game is configured to listen on, if an ini says so.</summary>
    public int? ConfiguredPort()
    {
        try
        {
            return RocketLeagueLocator.ConfiguredPort(FindInstalls(), DocumentsDir);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            _logger.LogInformation("Could not read the Stats API port: {Error}", ex.Message);
            return null;
        }
    }

    /// <summary>Edits the files in this process. Throws <see cref="UnauthorizedAccessException"/> if admin rights are needed.</summary>
    public StatsApiEnableOutcome EnableHere(IReadOnlyList<string> installs)
    {
        var (edit, create) = RocketLeagueLocator.PlanIniEdit(installs, DocumentsDir);
        if (edit.Count == 0 && create is null)
        {
            return new StatsApiEnableOutcome(false, false, false,
                "Rocket League wasn't found (looked in the Steam libraries, the Epic Games Launcher and the default folders).", []);
        }

        var now = DateTimeOffset.Now;
        var results = new List<StatsApiIniEditResult>();
        foreach (var path in edit)
        {
            results.Add(StatsApiIni.EnableFile(path, now));
        }

        if (create is not null)
        {
            results.Add(StatsApiIni.EnableFile(create, now, createIfMissing: true));
        }

        foreach (var r in results.Where(r => r.Changed))
        {
            _logger.LogInformation("Stats API enabled in {Path} ({Changes}); backup {Backup}", r.Path, string.Join(", ", r.Changes), r.BackupPath ?? "none (new file)");
        }

        var changed = results.Any(r => r.Changed);
        return new StatsApiEnableOutcome(true, changed, changed, changed ? Summary(results) : "The Stats API is already enabled. Nothing was changed.", results);
    }

    /// <summary>
    /// Edits the files, relaunching elevated (UAC prompt) if this process may not write them. The elevated copy runs
    /// only the edit and exits; its exit code says whether it worked.
    /// </summary>
    public async Task<StatsApiEnableOutcome> EnableAsync()
    {
        var installs = FindInstalls();
        try
        {
            return EnableHere(installs);
        }
        catch (UnauthorizedAccessException ex)
        {
            _logger.LogInformation("Ini folder needs admin rights ({Error}); relaunching elevated", ex.Message);
        }
        catch (IOException ex)
        {
            _logger.LogWarning(ex, "Could not edit the Stats API ini");
            return new StatsApiEnableOutcome(false, false, false, $"Couldn't edit the ini file: {ex.Message}", []);
        }

        try
        {
            using var process = Process.Start(new ProcessStartInfo
            {
                FileName = Environment.ProcessPath!,
                Arguments = "--enable-stats-api",
                UseShellExecute = true,
                Verb = "runas",
            });
            if (process is null)
            {
                return new StatsApiEnableOutcome(false, false, false, "Couldn't start the elevated helper.", []);
            }

            await process.WaitForExitAsync();
            return process.ExitCode switch
            {
                0 => Recheck(installs, "The Stats API was enabled (with admin rights)."),
                1 => Recheck(installs, "The Stats API is already enabled. Nothing was changed."),
                _ => new StatsApiEnableOutcome(false, false, false, "The elevated helper couldn't edit the ini file. See the log for details.", []),
            };
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            return new StatsApiEnableOutcome(false, false, false, "Cancelled: Windows didn't get permission to edit the game folder.", []);
        }
    }

    /// <summary>
    /// Entry for <c>--enable-stats-api</c> (the elevated relaunch): 0 = changed, 1 = nothing to change, 2 = failed.
    /// </summary>
    public int RunElevatedEdit()
    {
        try
        {
            var outcome = EnableHere(FindInstalls());
            return !outcome.Success ? 2 : outcome.AnyChange ? 0 : 1;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogError(ex, "Elevated Stats API edit failed");
            return 2;
        }
    }

    private StatsApiEnableOutcome Recheck(IReadOnlyList<string> installs, string message)
    {
        var enabled = Status(installs).Any(s => s.Values?.IsEnabled == true);
        return new StatsApiEnableOutcome(enabled, true, true, message, []);
    }

    private static string Summary(IReadOnlyList<StatsApiIniEditResult> results)
    {
        var lines = new List<string> { "The Stats API is now enabled." };
        foreach (var r in results.Where(r => r.Changed))
        {
            lines.Add(r.Created ? $"Created {r.Path}" : $"Updated {r.Path} (backup: {Path.GetFileName(r.BackupPath)})");
        }

        return string.Join(Environment.NewLine, lines);
    }

    /// <summary>A short status line for the settings window and tray.</summary>
    public static string Describe(IReadOnlyList<string> installs, IReadOnlyList<StatsApiFileStatus> files)
    {
        if (installs.Count == 0 && !files.Any(f => f.Exists))
        {
            return "Rocket League wasn't found on this PC.";
        }

        var active = files.FirstOrDefault(f => f.Values?.HasSection == true);
        if (active?.Values is not { } values)
        {
            return "Off: no Stats API settings yet.";
        }

        return values.IsEnabled
            ? $"On: {values.PacketSendRate:0.##} updates/s on port {values.EffectivePort}."
            : "Off: PacketSendRate is 0, so the game sends nothing.";
    }

    /// <summary>Process-list snapshot only (no handle to the game is opened).</summary>
    public static bool IsGameRunning()
    {
        var processes = Process.GetProcessesByName(GameWindowTracker.GameProcessName);
        foreach (var p in processes)
        {
            p.Dispose();
        }

        return processes.Length > 0;
    }

    private static string? ReadSteamRoot()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam");
            return key?.GetValue("SteamPath") is string path ? path.Replace('/', '\\') : null;
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}

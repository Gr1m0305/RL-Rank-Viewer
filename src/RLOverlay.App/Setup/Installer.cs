using System.IO;
using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Win32;
using RLOverlay.App.Services;

namespace RLOverlay.App.Setup;

/// <summary>
/// Where an install goes. <see cref="Real"/> is a per-user install (no admin rights): the app folder under
/// %LOCALAPPDATA%\Programs, a Start menu shortcut, an Apps &amp; features entry and the Run value, all under HKCU.
/// <see cref="Sandbox"/> moves every one of those under a test folder and a scratch registry key.
/// </summary>
public sealed record InstallLocations
{
    public const string ExeName = "RLOverlay.exe";
    public const string UninstallKeyName = "Gr1msRLOverlay";

    public required string DefaultInstallDir { get; init; }

    public required string StartMenuDir { get; init; }

    public required string DesktopDir { get; init; }

    /// <summary>Under HKEY_CURRENT_USER.</summary>
    public required string UninstallKeyPath { get; init; }

    /// <summary>Under HKEY_CURRENT_USER.</summary>
    public required string RunKeyPath { get; init; }

    /// <summary>Settings, logs and caches (removed on uninstall only when the user asks).</summary>
    public required string UserDataDir { get; init; }

    /// <summary>Set for test installs; also passed on to the registered uninstall command.</summary>
    public string? SandboxRoot { get; init; }

    public string StartMenuShortcut => Path.Combine(StartMenuDir, AppInfo.Name + ".lnk");

    public string DesktopShortcut => Path.Combine(DesktopDir, AppInfo.Name + ".lnk");

    public static InstallLocations Real() => new()
    {
        DefaultInstallDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", AppInfo.Name),
        StartMenuDir = Environment.GetFolderPath(Environment.SpecialFolder.Programs),
        DesktopDir = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
        UninstallKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\" + UninstallKeyName,
        RunKeyPath = StartupRegistration.DefaultKeyPath,
        UserDataDir = AppPaths.Root,
    };

    public static InstallLocations Sandbox(string root)
    {
        root = Path.GetFullPath(root);
        var id = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(root.ToUpperInvariant())))[..12];
        var keyBase = $@"{SandboxRegistryRoot}\{id}";
        return new InstallLocations
        {
            DefaultInstallDir = Path.Combine(root, "Programs", AppInfo.Name),
            StartMenuDir = Path.Combine(root, "Start Menu"),
            DesktopDir = Path.Combine(root, "Desktop"),
            UninstallKeyPath = $@"{keyBase}\Uninstall\{UninstallKeyName}",
            RunKeyPath = $@"{keyBase}\Run",
            UserDataDir = Path.Combine(root, "UserData"),
            SandboxRoot = root,
        };
    }

    /// <summary>All sandbox registry keys live under this HKCU key (tests delete it).</summary>
    public const string SandboxRegistryRoot = @"Software\RLOverlay.Tests\Sandbox";
}

public sealed record InstallOptions(string InstallDir, bool StartWithWindows, bool DesktopShortcut);

public sealed record InstalledInfo(string InstallDir, string ExePath, string? Version, bool StartWithWindows, bool HasDesktopShortcut);

/// <param name="LeftBehind">Folder that could not be removed (files the user added), or null.</param>
public sealed record UninstallResult(bool ExeRemovalScheduled, string? LeftBehind);

/// <summary>Install, update and uninstall. No UI; every location comes from <see cref="InstallLocations"/>.</summary>
public sealed class Installer(InstallLocations locations, ILogger<Installer>? logger = null)
{
    private const string LicenceResource = "RLOverlay.Licenses.Inter-OFL.txt";
    private const string RankReadmeResource = "RLOverlay.Assets.Ranks.README.md";

    private static readonly string[] OwnFiles = [Path.Combine("licenses", "Inter-OFL.txt"), Path.Combine("assets", "ranks", "README.md")];
    private static readonly string[] OwnDirs = [Path.Combine("assets", "ranks"), "assets", "licenses"];

    private readonly ILogger _logger = (ILogger?)logger ?? NullLogger.Instance;

    public InstallLocations Locations => locations;

    /// <summary>The current install for this Windows account, from its Apps &amp; features entry.</summary>
    public InstalledInfo? FindExisting()
    {
        using var key = Registry.CurrentUser.OpenSubKey(locations.UninstallKeyPath);
        if (key?.GetValue("InstallLocation") is not string dir || dir.Length == 0)
        {
            return null;
        }

        var exe = Path.Combine(dir, InstallLocations.ExeName);
        if (!File.Exists(exe))
        {
            return null;
        }

        return new InstalledInfo(dir, exe, key.GetValue("DisplayVersion") as string, Startup(exe).IsEnabled, IsOurShortcut(locations.DesktopShortcut, exe));
    }

    /// <summary>A folder the user picked: the app goes into its own "Gr1m's RL Overlay" subfolder unless already named so.</summary>
    public static string NormalizeInstallDir(string chosen)
    {
        var full = Path.GetFullPath(chosen.Trim()).TrimEnd(Path.DirectorySeparatorChar);
        return string.Equals(Path.GetFileName(full), AppInfo.Name, StringComparison.OrdinalIgnoreCase) ? full : Path.Combine(full, AppInfo.Name);
    }

    /// <summary>Copies the exe and its two text files, then shortcuts, the Apps &amp; features entry and the Run value.</summary>
    /// <returns>The installed exe.</returns>
    public string Install(string sourceExe, InstallOptions options)
    {
        var dir = options.InstallDir;
        Directory.CreateDirectory(dir);
        var exe = Path.Combine(dir, InstallLocations.ExeName);
        if (!string.Equals(Path.GetFullPath(sourceExe), Path.GetFullPath(exe), StringComparison.OrdinalIgnoreCase))
        {
            var temp = exe + ".new";
            File.Copy(sourceExe, temp, overwrite: true);
            try
            {
                File.Move(temp, exe, overwrite: true);
            }
            catch
            {
                TryDelete(temp);
                throw;
            }
        }

        WriteResource(LicenceResource, Path.Combine(dir, OwnFiles[0]));
        WriteResource(RankReadmeResource, Path.Combine(dir, OwnFiles[1]));

        ShellLink.Create(locations.StartMenuShortcut, exe, dir, AppInfo.Name);
        if (options.DesktopShortcut)
        {
            ShellLink.Create(locations.DesktopShortcut, exe, dir, AppInfo.Name);
        }
        else if (IsOurShortcut(locations.DesktopShortcut, exe))
        {
            File.Delete(locations.DesktopShortcut);
        }

        using (var key = Registry.CurrentUser.CreateSubKey(locations.UninstallKeyPath, writable: true))
        {
            var sandbox = locations.SandboxRoot is { } root ? $" --setup-sandbox \"{root}\"" : "";
            key.SetValue("DisplayName", AppInfo.Name);
            key.SetValue("DisplayVersion", AppInfo.Version);
            key.SetValue("Publisher", AppInfo.Publisher);
            key.SetValue("DisplayIcon", exe + ",0");
            key.SetValue("InstallLocation", dir);
            key.SetValue("UninstallString", $"\"{exe}\" --uninstall{sandbox}");
            key.SetValue("QuietUninstallString", $"\"{exe}\" --uninstall --quiet{sandbox}");
            key.SetValue("InstallDate", DateTime.Now.ToString("yyyyMMdd", CultureInfo.InvariantCulture));
            key.SetValue("EstimatedSize", (int)Math.Min(int.MaxValue, new FileInfo(exe).Length / 1024), RegistryValueKind.DWord);
            key.SetValue("NoModify", 1, RegistryValueKind.DWord);
            key.SetValue("NoRepair", 1, RegistryValueKind.DWord);
        }

        Startup(exe).Set(options.StartWithWindows);
        _logger.LogInformation("Installed {Version} to {Dir} (start with Windows: {Startup}, desktop shortcut: {Desktop})",
            AppInfo.Version, dir, options.StartWithWindows, options.DesktopShortcut);
        return exe;
    }

    /// <summary>
    /// Removes the Run value (if it points at this install), the shortcuts, the Apps &amp; features entry, the app's own
    /// files and, if asked, the user data. Only files the installer created are deleted, and folders only when empty,
    /// so nothing the user put in the folder is lost. When the exe being removed is the one running, it is deleted by
    /// a short-lived cmd.exe right after this process exits.
    /// </summary>
    public UninstallResult Uninstall(string installDir, bool removeUserData, Action? beforeUserDataDelete = null)
    {
        var exe = Path.Combine(installDir, InstallLocations.ExeName);
        var startup = Startup(exe);
        if (startup.IsEnabled)
        {
            startup.Set(false);
        }

        foreach (var shortcut in new[] { locations.StartMenuShortcut, locations.DesktopShortcut })
        {
            if (IsOurShortcut(shortcut, exe))
            {
                File.Delete(shortcut);
            }
        }

        Registry.CurrentUser.DeleteSubKeyTree(locations.UninstallKeyPath, throwOnMissingSubKey: false);

        foreach (var file in OwnFiles)
        {
            TryDelete(Path.Combine(installDir, file));
        }

        foreach (var sub in OwnDirs)
        {
            TryDeleteEmptyDir(Path.Combine(installDir, sub));
        }

        if (removeUserData && Directory.Exists(locations.UserDataDir))
        {
            beforeUserDataDelete?.Invoke();
            try
            {
                Directory.Delete(locations.UserDataDir, recursive: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _logger.LogWarning(ex, "Could not delete all user data in {Dir}", locations.UserDataDir);
            }
        }

        var running = Environment.ProcessPath is { } self
            && string.Equals(Path.GetFullPath(self), Path.GetFullPath(exe), StringComparison.OrdinalIgnoreCase);
        if (running)
        {
            var scheduled = ScheduleRemovalAfterExit(exe, installDir);
            return new UninstallResult(scheduled, scheduled ? null : installDir);
        }

        TryDelete(exe);
        TryDeleteEmptyDir(installDir);
        return new UninstallResult(false, Directory.Exists(installDir) ? installDir : null);
    }

    private StartupRegistration Startup(string exe) =>
        new(NullLogger<StartupRegistration>.Instance, locations.RunKeyPath, exe);

    private static bool IsOurShortcut(string shortcut, string exe)
    {
        if (!File.Exists(shortcut))
        {
            return false;
        }

        var target = ShellLink.ReadTarget(shortcut);
        return target is null || string.Equals(target, exe, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// A running exe can't delete itself: cmd.exe waits ~3 s for this process to exit, deletes the exe and removes the
    /// folder if it is then empty (plain rmdir, never recursive).
    /// </summary>
    private bool ScheduleRemovalAfterExit(string exe, string dir)
    {
        // cmd would expand %...% inside the quoted paths; such paths are left for the user to delete.
        if (exe.Contains('%') || exe.Contains('"'))
        {
            return false;
        }

        try
        {
            var cmd = Path.Combine(Environment.SystemDirectory, "cmd.exe");
            var args = $"/d /c ping -n 4 127.0.0.1 >nul & del /f /q \"{exe}\" & rmdir \"{dir}\"";
            using var _ = Process.Start(new ProcessStartInfo(cmd, args)
            {
                CreateNoWindow = true,
                UseShellExecute = false,
                WindowStyle = ProcessWindowStyle.Hidden,
                WorkingDirectory = Path.GetTempPath(),
            });
            _logger.LogInformation("Scheduled removal of {Exe} after exit", exe);
            return true;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            _logger.LogWarning(ex, "Could not schedule removal of {Exe}", exe);
            return false;
        }
    }

    private static void WriteResource(string name, string path)
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(name)
            ?? throw new InvalidOperationException($"Missing embedded resource {name}");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var file = File.Create(path);
        stream.CopyTo(file);
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    private static void TryDeleteEmptyDir(string path)
    {
        try
        {
            if (Directory.Exists(path) && !Directory.EnumerateFileSystemEntries(path).Any())
            {
                Directory.Delete(path);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }
}

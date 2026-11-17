using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RLOverlay.App.Services;

namespace RLOverlay.App.Setup;

public enum SetupPage
{
    Options,
    Working,
    Done,
    Failed,
}

/// <summary>
/// The installer / uninstaller window: one options page, a short working page, then done (or failed with a retry).
/// The work itself is <see cref="Installer"/>; closing a running copy and launching the installed one are passed in.
/// </summary>
public sealed partial class SetupViewModel : ObservableObject
{
    private readonly Installer _installer;
    private readonly string _sourceExe;
    private readonly Func<bool> _closeRunningApp;
    private readonly Action<string> _launch;
    private readonly Action? _beforeUserDataDelete;
    private readonly InstalledInfo? _existing;
    private string? _installedExe;

    public SetupViewModel(Installer installer, bool uninstall, string sourceExe, Func<bool> closeRunningApp, Action<string> launch,
        Action? beforeUserDataDelete = null)
    {
        _installer = installer;
        _sourceExe = sourceExe;
        _closeRunningApp = closeRunningApp;
        _launch = launch;
        _beforeUserDataDelete = beforeUserDataDelete;
        IsUninstall = uninstall;
        _existing = installer.FindExisting();

        _installDir = _existing?.InstallDir ?? installer.Locations.DefaultInstallDir;
        _startWithWindows = _existing?.StartWithWindows ?? true;
        _desktopShortcut = _existing?.HasDesktopShortcut ?? false;
        _launchWhenDone = !uninstall;

        if (uninstall && _existing is null)
        {
            _page = SetupPage.Failed;
            _error = $"{AppInfo.Name} isn't installed for this Windows account, so there is nothing to remove.";
        }
    }

    public event Action? CloseRequested;

    /// <summary>The view shows a folder picker: gets the current folder, returns the chosen one (or null).</summary>
    public Func<string, string?>? PickFolder { get; set; }

    public bool IsUninstall { get; }

    public bool IsUpdate => !IsUninstall && _existing is not null;

    public string Title =>
        IsUninstall ? $"Uninstall {AppInfo.Name}"
        : IsUpdate ? $"Update {AppInfo.Name}"
        : $"Install {AppInfo.Name}";

    public string Subtitle =>
        IsUninstall ? $"Version {_existing?.Version ?? "?"}"
        : IsUpdate ? $"Installed: version {_existing!.Version ?? "?"} · this setup: version {AppInfo.Version}"
        : $"Version {AppInfo.Version} · for this Windows account, no admin rights needed";

    public string ActionLabel => IsUninstall ? "Uninstall" : IsUpdate ? "Update" : "Install";

    public bool CanChangeFolder => !IsUninstall && !IsUpdate;

    public string FolderHint =>
        IsUpdate ? "Already installed here. Uninstall first to move it somewhere else."
        : "The app gets its own folder. Your settings stay in %LOCALAPPDATA%\\RLOverlay.";

    /// <summary>0 = done, 1 = failed or cancelled (exit code for the setup process).</summary>
    public int ExitCode { get; private set; } = 1;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsOptions), nameof(IsWorking), nameof(IsDone), nameof(IsFailed))]
    private SetupPage _page;

    public bool IsOptions => Page == SetupPage.Options;

    public bool IsWorking => Page == SetupPage.Working;

    public bool IsDone => Page == SetupPage.Done;

    public bool IsFailed => Page == SetupPage.Failed;

    [ObservableProperty]
    private string _installDir;

    [ObservableProperty]
    private bool _startWithWindows;

    [ObservableProperty]
    private bool _desktopShortcut;

    [ObservableProperty]
    private bool _launchWhenDone;

    [ObservableProperty]
    private bool _removeUserData;

    [ObservableProperty]
    private string _status = "";

    [ObservableProperty]
    private string _doneTitle = "";

    [ObservableProperty]
    private string _doneText = "";

    [ObservableProperty]
    private string _error = "";

    /// <summary>Folder actually used: the picked one gets its own app subfolder.</summary>
    public string TargetDir => IsUpdate ? _existing!.InstallDir : Installer.NormalizeInstallDir(InstallDir);

    [RelayCommand]
    private void ChangeFolder()
    {
        if (PickFolder?.Invoke(InstallDir) is { Length: > 0 } picked)
        {
            InstallDir = Installer.NormalizeInstallDir(picked);
        }
    }

    [RelayCommand]
    private async Task RunAsync()
    {
        Page = SetupPage.Working;
        try
        {
            await Task.Run(DoWork);
            ExitCode = 0;
            Page = SetupPage.Done;
        }
        catch (Exception ex)
        {
            Error = Explain(ex);
            Page = SetupPage.Failed;
        }
    }

    /// <summary><c>--quiet</c>: the same work with the current choices, no window.</summary>
    public bool RunQuiet()
    {
        if (IsFailed)
        {
            return false;
        }

        try
        {
            DoWork();
            ExitCode = 0;
            return true;
        }
        catch (Exception ex)
        {
            Error = Explain(ex);
            return false;
        }
    }

    [RelayCommand]
    private void Finish()
    {
        if (!IsUninstall && LaunchWhenDone && _installedExe is not null)
        {
            _launch(_installedExe);
        }

        CloseRequested?.Invoke();
    }

    [RelayCommand]
    private void Cancel() => CloseRequested?.Invoke();

    [RelayCommand]
    private void TryAgain() => Page = SetupPage.Options;

    public bool CanTryAgain => !(IsUninstall && _existing is null);

    private void DoWork()
    {
        Status = $"Closing {AppInfo.Name} if it is running…";
        if (!_closeRunningApp())
        {
            throw new SetupException($"{AppInfo.Name} is still running. Right-click its icon in the notification area, choose Quit, then try again.");
        }

        if (IsUninstall)
        {
            Status = "Removing…";
            var result = _installer.Uninstall(_existing!.InstallDir, RemoveUserData, _beforeUserDataDelete);
            DoneTitle = $"{AppInfo.Name} has been removed";
            DoneText = (RemoveUserData ? "The app, its shortcuts and your settings are gone." : "The app and its shortcuts are gone. Your settings are kept in case you install it again.")
                + " Rocket League's Stats API stays turned on (the original ini files are backed up next to them)."
                + (result.LeftBehind is { } left ? $"{Environment.NewLine}{Environment.NewLine}Files you added are still in {left}." : "");
            return;
        }

        Status = IsUpdate ? "Updating…" : "Installing…";
        _installedExe = _installer.Install(_sourceExe, new InstallOptions(TargetDir, StartWithWindows, DesktopShortcut));
        DoneTitle = IsUpdate ? $"{AppInfo.Name} is up to date" : $"{AppInfo.Name} is installed";
        DoneText = "It waits in the notification area (by the clock) and switches on by itself when you open Rocket League. "
            + "You'll also find it in the Start menu."
            + Environment.NewLine + Environment.NewLine
            + "First time? Open its Settings, click Enable Stats API and restart Rocket League, and set the game to Borderless.";
    }

    private static string Explain(Exception ex) => ex switch
    {
        SetupException s => s.Message,
        UnauthorizedAccessException or IOException => $"Windows wouldn't let setup write a file ({ex.Message}). If {AppInfo.Name} is open, quit it from the tray and try again.",
        _ => $"Something went wrong: {ex.Message}",
    };

    private sealed class SetupException(string message) : Exception(message);
}

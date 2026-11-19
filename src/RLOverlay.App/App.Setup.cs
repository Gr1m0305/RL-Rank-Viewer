using System.IO;
using System.Diagnostics;
using Microsoft.Extensions.Logging;
using RLOverlay.App.Services;
using RLOverlay.App.Setup;
using Serilog;

namespace RLOverlay.App;

/// <summary>Setup mode: install / update (<c>--setup</c> or a "…setup….exe" file name) and <c>--uninstall</c>.</summary>
public partial class App
{
    private static bool IsSetupFileName() =>
        Environment.ProcessPath is { } path
        && Path.GetFileNameWithoutExtension(path).Contains("setup", StringComparison.OrdinalIgnoreCase);

    private void RunSetup()
    {
        var sandbox = _args.SetupSandbox;
        var locations = sandbox is null ? InstallLocations.Real() : InstallLocations.Sandbox(sandbox);
        var installer = new Installer(locations, LoggerFactory.CreateLogger<Installer>());
        _log.LogInformation("{Mode} ({Locations})", _args.Uninstall ? "Uninstall" : "Setup", sandbox is null ? "real" : $"sandbox {locations.SandboxRoot}");

        // A sandbox install never closes or signals the real app: the copy it starts is a test run (--port skips the
        // single-instance mutex, so it can't reach a running copy) that quits by itself after a few seconds.
        Func<bool> closeRunning = sandbox is null ? () => InstanceSignals.CloseRunningApp(TimeSpan.FromSeconds(10)) : () => true;
        var dataDir = _args.DataDir is { } data ? $" --data-dir \"{Path.GetFullPath(data)}\"" : "";
        void Start(string exe, bool skipUpdates) => Process.Start(new ProcessStartInfo(exe)
        {
            UseShellExecute = true,
            WorkingDirectory = Path.GetDirectoryName(exe)!,
            Arguments = sandbox is null ? ((skipUpdates ? "--no-update-check" : "") + dataDir).TrimStart()
                : $"--exit-after 8 --port 49999 --no-update-check{dataDir}",
        })?.Dispose();
        void Launch(string exe) => Start(exe, skipUpdates: false);

        // An update: the copy being replaced quits right after starting setup; wait until its exe is free.
        if (_args.WaitForProcess is { } pid)
        {
            WaitForExit(pid);
        }

        var vm = new SetupViewModel(installer, _args.Uninstall, Environment.ProcessPath!, closeRunning, Launch,
            beforeUserDataDelete: () =>
            {
                // Our own log file lives in the folder that is about to go.
                _log = Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance;
                Log.CloseAndFlush();
            });

        if (_args.Quiet)
        {
            var ok = vm.RunQuiet();
            if (!ok)
            {
                _log.LogError("Setup failed: {Error}", vm.Error);
            }

            // An update (--launch) always brings the app back: the new version, or the old one if this failed. The old
            // one then skips update checks for that run, so a lasting problem doesn't repeat every minute.
            if (_args.LaunchWhenDone && !_args.Uninstall && vm.AppToStart is { } app)
            {
                _log.LogInformation("Setup: starting {Exe}{Old}", app, ok ? "" : " (the copy that was there before)");
                Start(app, skipUpdates: !ok);
            }

            Shutdown(ok ? 0 : 1);
            return;
        }

        var window = new SetupWindow(vm);
        window.Closed += (_, _) => Shutdown(vm.ExitCode);
        window.Show();
    }

    private void WaitForExit(int pid)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            if (!process.WaitForExit(TimeSpan.FromSeconds(20)))
            {
                _log.LogWarning("Setup: process {Pid} is still running after 20 s", pid);
            }
        }
        catch (ArgumentException)
        {
            // Already gone.
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            _log.LogDebug(ex, "Setup: couldn't wait for process {Pid}", pid);
        }
    }
}

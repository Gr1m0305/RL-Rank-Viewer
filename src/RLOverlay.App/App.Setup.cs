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

        // A sandbox install never closes or starts the real app (the launched copy quits by itself after a few seconds).
        Func<bool> closeRunning = sandbox is null ? () => InstanceSignals.CloseRunningApp(TimeSpan.FromSeconds(10)) : () => true;
        void Launch(string exe) => Process.Start(new ProcessStartInfo(exe)
        {
            UseShellExecute = true,
            WorkingDirectory = Path.GetDirectoryName(exe)!,
            Arguments = sandbox is null ? "" : "--exit-after 8",
        })?.Dispose();

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

            Shutdown(ok ? 0 : 1);
            return;
        }

        var window = new SetupWindow(vm);
        window.Closed += (_, _) => Shutdown(vm.ExitCode);
        window.Show();
    }
}

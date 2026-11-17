using System.Windows;

namespace RLOverlay.App;

/// <summary>
/// Application entry. Composition happens here (no DI container): settings, logging,
/// Stats API client, stats provider, lobby state machine, overlay window and tray icon.
/// Filled in over Parts 2–6.
/// </summary>
public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        Shutdown();
    }
}

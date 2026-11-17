namespace RLOverlay.App.Services;

/// <summary>
/// The name users see. Internal identifiers (exe name, %LOCALAPPDATA%\RLOverlay, mutex/event names, the Run value
/// and the HTTP User-Agent) keep the plain "RLOverlay" so existing settings carry over and nothing personal is sent
/// to web services.
/// </summary>
public static class AppInfo
{
    public const string Name = "Gr1m's RL Overlay";

    public const string Publisher = "Gr1m";

    public static string Version => typeof(AppInfo).Assembly.GetName().Version?.ToString(3) ?? "0.0.0";
}

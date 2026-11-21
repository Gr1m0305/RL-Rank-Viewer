using RLOverlay.App.ViewModels;
using RLOverlay.Core.Config;

namespace RLOverlay.App.Services;

/// <summary>
/// A stand-in <see cref="ISettingsHost"/> for the <c>--screenshots</c> renderer: default settings, a typical Epic
/// install path and an enabled Stats API. It reads and changes nothing on the machine.
/// </summary>
internal sealed class SampleSettingsHost : ISettingsHost
{
    private const string Install = @"C:\Program Files\Epic Games\rocketleague";

    public OverlaySettings Current { get; } = new();

    public bool RunAtStartup => true;

    public string ConnectionStatus => "Connected to Rocket League (port 49123).";

    public IReadOnlyList<ScreenInfo> Screens() =>
    [
        new(@"\\.\DISPLAY1", null, new PixelRect(0, 0, 1920, 1080), true),
        new(@"\\.\DISPLAY2", null, new PixelRect(-1920, 0, 1920, 1080), false),
    ];

    public void SetPreview(bool on, OverlaySettings settings)
    {
    }

    public void PreviewSettings(OverlaySettings settings)
    {
    }

    public void SuspendHotkeys(bool suspended)
    {
    }

    public void Save(OverlaySettings settings, bool runAtStartup)
    {
    }

    public (string Summary, IReadOnlyList<StatsApiFileStatus> Files) StatsApiStatus()
    {
        var on = new StatsApiIniValues(30, 49123, 49124, HasSection: true);
        IReadOnlyList<StatsApiFileStatus> files =
        [
            new(Install + @"\TAGame\Config\TAStatsAPI.ini", null),
            new(Install + @"\TAGame\Config\DefaultStatsAPI.ini", on),
        ];
        return ("On: 30 updates/s on port 49123.", files);
    }

    public Task<StatsApiEnableOutcome> EnableStatsApiAsync() =>
        Task.FromResult(new StatsApiEnableOutcome(true, false, false, "The Stats API is already enabled. Nothing was changed.", []));

    public void OpenFolder(string path)
    {
    }

    public UpdateState UpdateState { get; } = new($"You have the latest version ({AppInfo.Version}). Last checked at 18:42.", null, true);

    public event Action? UpdateStateChanged
    {
        add { }
        remove { }
    }

    public Task CheckForUpdatesAsync() => Task.CompletedTask;

    public void InstallUpdate()
    {
    }

    public (bool Added, string Message) AddFavouriteFromLink(string link) => (false, "");
}

using System.ComponentModel;
using Microsoft.Extensions.Logging;
using RLOverlay.App.ViewModels;
using RLOverlay.App.Views;

namespace RLOverlay.App;

/// <summary>The Ctrl+T lobby window with each player's tracker.gg row (see <see cref="TrackerLobbyWindow"/>).</summary>
public partial class App
{
    private TrackerLobbyWindow? _trackerWindow;

    /// <summary>True while the tracker window is the active window (the overlay's shortcuts keep working then).</summary>
    private bool TrackerWindowInFront => _trackerWindow is { IsVisible: true, IsActive: true };

    /// <summary>Ctrl+T and the tray item: opens the window, or hides it when it is open.</summary>
    internal void ToggleTrackerWindow()
    {
        if (_coordinator is null)
        {
            return;
        }

        if (_trackerWindow is { IsVisible: true } open)
        {
            _log.LogInformation("Tracker window hidden");
            open.Hide();
            return;
        }

        if (_trackerWindow is null)
        {
            _trackerWindow = new TrackerLobbyWindow(url => _links?.Open(url), LoggerFactory.CreateLogger<TrackerLobbyWindow>());
            _trackerWindow.Activated += (_, _) => UpdateForegroundGate();
            _trackerWindow.Deactivated += (_, _) => UpdateForegroundGate();
            _trackerWindow.IsVisibleChanged += (_, _) => UpdateForegroundGate();
        }

        SyncTrackerWindow();
        _log.LogInformation("Tracker window shown ({Players} players)", _coordinator.Lobby.Players.Count);
        _trackerWindow.ShowWithoutFocus();
    }

    /// <summary>Brings the window up to date with the lobby, the selected playlist and the settings.</summary>
    private void SyncTrackerWindow()
    {
        if (_trackerWindow is not { } window || _coordinator is null || _panel is null)
        {
            return;
        }

        window.SetHints(_settings.CycleHotkey, _settings.TrackerHotkey);
        window.SetZoom(_settings.Scale);
        window.SetPlaylist(_panel.Playlist);
        window.SetPlayers(_coordinator.Lobby.Players, _coordinator.IsPreviewing);
    }

    private void WireTrackerWindow()
    {
        _coordinator!.LobbyChanged += SyncTrackerWindow;
        _panel!.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(PanelViewModel.Playlist))
            {
                _trackerWindow?.SetPlaylist(_panel.Playlist);
            }
        };
    }
}

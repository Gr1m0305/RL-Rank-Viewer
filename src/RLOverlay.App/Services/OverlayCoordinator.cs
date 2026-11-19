using System.Windows.Threading;
using Microsoft.Extensions.Logging;
using RLOverlay.App.Overlay;
using RLOverlay.App.ViewModels;
using RLOverlay.Core.Config;
using RLOverlay.Core.Lobby;
using RLOverlay.Core.Playlists;
using RLOverlay.Core.StatsApi;

namespace RLOverlay.App.Services;

/// <summary>
/// The glue between the lobby state machine, the screen's view model and the overlay window. Runs on the UI thread
/// only: Stats API messages are marshalled here by the caller.
/// </summary>
internal sealed class OverlayCoordinator : IDisposable
{
    public static readonly TimeSpan StripDuration = TimeSpan.FromMilliseconds(1500);

    /// <summary>The screen holds still this long after it starts hiding (its fade-out), then follows the lobby again.</summary>
    private static readonly TimeSpan HoldWhileHiding = TimeSpan.FromMilliseconds(450);

    private readonly PanelViewModel _vm;
    private readonly OverlayWindow _window;
    private readonly ILogger _logger;
    private readonly DispatcherTimer _stripTimer;
    private readonly DispatcherTimer _holdTimer;
    private LobbyStateMachine _lobby;
    private OverlaySettings _settings;
    private bool _userCycled;

    public OverlayCoordinator(
        OverlaySettings settings,
        PanelViewModel vm,
        OverlayWindow window,
        ILogger<OverlayCoordinator> logger)
    {
        _settings = settings;
        _lobby = new LobbyStateMachine { KeepOpenAtKickoff = settings.KeepOpenAtKickoff };
        _vm = vm;
        _window = window;
        _logger = logger;
        _stripTimer = new DispatcherTimer { Interval = StripDuration };
        _stripTimer.Tick += (_, _) => HideStrip();
        _holdTimer = new DispatcherTimer { Interval = HoldWhileHiding };
        _holdTimer.Tick += (_, _) => FollowLobbyWhileHidden();
        _vm.ApplySettings(settings);
        _vm.Playlist = ResolveInitialPlaylist();
    }

    public LobbyStateMachine Lobby => _lobby;

    public PanelViewModel Panel => _vm;

    /// <summary>Raised after the roster may have changed (players joined, left or changed team; lobby cleared).</summary>
    public event Action? LobbyChanged;

    public void Handle(StatsMessage message) => ApplyEffects(_lobby.Apply(message));

    public void OnConnectionChanged(StatsConnectionState state)
    {
        if (state == StatsConnectionState.Disconnected && _lobby.Phase != MatchPhase.Idle)
        {
            _logger.LogInformation("Stats API connection lost; clearing the lobby");
            ApplyEffects(_lobby.OnDisconnected());
        }
    }

    /// <summary>Ctrl+R.</summary>
    public void TogglePanel() => ApplyEffects(_lobby.TogglePanel());

    /// <summary>Ctrl+G: next enabled playlist, every row moves its crop to it, the strip shows for 1.5 s.</summary>
    public void CyclePlaylist()
    {
        var next = PlaylistCycler.Next(_vm.Playlist, _settings.EnabledPlaylists);
        _userCycled = true;
        _vm.Playlist = next;
        _vm.ShowSwitchedNote = true;
        _window.SetStripVisible(true);
        _stripTimer.Stop();
        _stripTimer.Start();
        _logger.LogInformation("Playlist → {Playlist}", next);
    }

    public void ApplySettings(OverlaySettings settings)
    {
        _settings = settings;
        _lobby.KeepOpenAtKickoff = settings.KeepOpenAtKickoff;
        _vm.ApplySettings(settings);
        if (!settings.EnabledPlaylists.Contains(_vm.Playlist))
        {
            _vm.Playlist = ResolveInitialPlaylist();
        }
    }

    /// <summary>True while the settings window's Preview runs (real Stats API messages are ignored meanwhile).</summary>
    public bool IsPreviewing { get; private set; }

    /// <summary>Settings Preview: forget the real lobby; the made-up players that follow show placeholders.</summary>
    public void BeginPreview()
    {
        IsPreviewing = true;
        Reset();
        _vm.IsSample = true;
    }

    /// <summary>Ends the Preview: the screen is cleared and waits for the real lobby again.</summary>
    public void EndPreview()
    {
        IsPreviewing = false;
        Reset();
        _vm.IsSample = SampleLobby;
    }

    /// <summary>The lobby is always made up (<c>--demo</c>).</summary>
    public bool SampleLobby { get; set; }

    /// <summary>Forget the current lobby (used before and after a Preview).</summary>
    public void Reset()
    {
        HideStrip();
        _lobby = new LobbyStateMachine { KeepOpenAtKickoff = _settings.KeepOpenAtKickoff };
        _userCycled = false;
        _vm.Phase = MatchPhase.Idle;
        _vm.Clear();
        _vm.Playlist = ResolveInitialPlaylist();
        ShowPanel(false);
        LobbyChanged?.Invoke();
    }

    /// <summary>
    /// Shows or hides the panel. While it fades out the view model holds still, so nothing changes mid-fade; after that
    /// it follows the lobby again, out of sight (see <see cref="FollowLobbyWhileHidden"/>).
    /// </summary>
    private void ShowPanel(bool visible)
    {
        _holdTimer.Stop();
        if (visible)
        {
            _vm.IsFrozen = false;
            _vm.Phase = _lobby.Phase;
            _vm.Refresh();
        }
        else
        {
            _vm.IsFrozen = true;
            _holdTimer.Start();
        }

        _window.SetPanelVisible(visible);
    }

    /// <summary>
    /// The fade-out is over: the hidden screen follows the lobby again. Pages of players who are gone (a new match, the
    /// game closed) are closed instead of running on unseen, and players who join after kickoff start loading.
    /// </summary>
    private void FollowLobbyWhileHidden()
    {
        _holdTimer.Stop();
        if (_window.PanelOn)
        {
            return;
        }

        _vm.IsFrozen = false;
        _vm.Phase = _lobby.Phase;
        _vm.Refresh();
    }

    private void ApplyEffects(IReadOnlyList<LobbyEffect> effects)
    {
        if (effects.Count == 0)
        {
            return;
        }

        foreach (var effect in effects)
        {
            switch (effect)
            {
                case MatchStartedEffect started:
                    _logger.LogInformation("Match started ({Guid}, {Phase})", started.MatchGuid, started.Phase);
                    _userCycled = false;
                    _vm.Phase = started.Phase;
                    _vm.NewMatch();
                    _vm.Playlist = ResolveInitialPlaylist();
                    break;

                case MatchPlaylistEffect playlist:
                    _vm.MatchTeamSize = PlaylistCatalog.TeamSizeForGameId(playlist.PlaylistId);
                    if (!_userCycled)
                    {
                        _vm.Playlist = ResolveInitialPlaylist();
                    }

                    break;

                case PlayerAddedEffect added:
                    _vm.AddOrUpdate(added.Player);
                    break;

                case PlayerChangedEffect changed:
                    _vm.AddOrUpdate(changed.After);
                    break;

                case PlayerRemovedEffect removed:
                    _vm.Remove(removed.Player.Key);
                    break;

                case PanelVisibilityEffect visibility:
                    _logger.LogInformation("Panel {State} ({Reason})", visibility.Visible ? "shown" : "hidden", visibility.Reason);
                    ShowPanel(visibility.Visible);
                    if (!visibility.Visible && visibility.Reason == PanelChangeReason.Kickoff)
                    {
                        HideStrip();
                    }

                    break;

                case LobbyClearedEffect cleared:
                    _logger.LogInformation("Lobby cleared ({Reason})", cleared.Reason);
                    HideStrip();
                    if (cleared.Reason != PanelChangeReason.MatchStarted)
                    {
                        ShowPanel(false); // a new match decides for itself (it may keep a Ctrl+R screen open)
                    }

                    _vm.Phase = MatchPhase.Idle;
                    _vm.Clear();
                    break;
            }
        }

        _vm.Phase = _lobby.Phase;
        _vm.Refresh();
        LobbyChanged?.Invoke();
    }

    private Playlist ResolveInitialPlaylist() =>
        PlaylistCycler.ResolveInitial(_settings.DefaultPlaylist, _settings.EnabledPlaylists, _lobby.MatchPlaylistId, _settings.FollowMatchPlaylist);

    private void HideStrip()
    {
        _stripTimer.Stop();
        _vm.ShowSwitchedNote = false;
        if (_window.StripOn)
        {
            _window.SetStripVisible(false);
        }
    }

    public void Dispose()
    {
        _stripTimer.Stop();
        _holdTimer.Stop();
    }
}

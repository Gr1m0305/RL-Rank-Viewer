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
/// The glue between the lobby state machine, the screens' view models (the lobby and the favourites) and the overlay
/// window. Runs on the UI thread only: Stats API messages are marshalled here by the caller.
/// </summary>
internal sealed class OverlayCoordinator : IDisposable
{
    public static readonly TimeSpan StripDuration = TimeSpan.FromMilliseconds(1500);

    /// <summary>The screen holds still this long after it starts hiding (its fade-out), then follows the lobby again.</summary>
    private static readonly TimeSpan HoldWhileHiding = TimeSpan.FromMilliseconds(450);

    private readonly PanelViewModel _vm;
    private readonly PanelViewModel _favourites;
    private readonly OverlayWindow _window;
    private readonly ILogger _logger;
    private readonly DispatcherTimer _stripTimer;
    private readonly DispatcherTimer _holdTimer;
    private readonly DispatcherTimer _favouritesTimer;
    private LobbyStateMachine _lobby;
    private OverlaySettings _settings;
    private MatchPhase _phaseSeen = MatchPhase.Idle;
    private bool _userCycled;

    public OverlayCoordinator(
        OverlaySettings settings,
        PanelViewModel vm,
        PanelViewModel favourites,
        OverlayWindow window,
        ILogger<OverlayCoordinator> logger)
    {
        _settings = settings;
        _lobby = new LobbyStateMachine { KeepOpenAtKickoff = settings.KeepOpenAtKickoff };
        _vm = vm;
        _favourites = favourites;
        _window = window;
        _logger = logger;
        _stripTimer = new DispatcherTimer { Interval = StripDuration };
        _stripTimer.Tick += (_, _) => HideStrip();
        _holdTimer = new DispatcherTimer { Interval = HoldWhileHiding };
        _holdTimer.Tick += (_, _) => FollowLobbyWhileHidden();
        _favouritesTimer = new DispatcherTimer { Interval = HoldWhileHiding };
        _favouritesTimer.Tick += (_, _) => ClearFavouritesWhenHidden();
        _vm.ApplySettings(settings);
        _favourites.ApplySettings(settings);
        _vm.PropertyChanged += (_, e) =>
        {
            // The favourites show the same playlist as the lobby (Ctrl+G switches both).
            if (e.PropertyName == nameof(PanelViewModel.Playlist))
            {
                _favourites.Playlist = _vm.Playlist;
            }
        };
        _vm.Playlist = ResolveInitialPlaylist();
        _favourites.Playlist = _vm.Playlist;
    }

    public LobbyStateMachine Lobby => _lobby;

    public PanelViewModel Panel => _vm;

    /// <summary>Raised after the roster may have changed (players joined, left or changed team; lobby cleared).</summary>
    public event Action? LobbyChanged;

    /// <summary>A player joined the real lobby or changed (so a favourite's saved name can follow a new one).</summary>
    public event Action<LobbyPlayer>? PlayerSeen;

    /// <summary>True while the favourites screen is open.</summary>
    public bool FavouritesOpen => _window.FavouritesOn;

    public void Handle(StatsMessage message) => ApplyEffects(_lobby.Apply(message));

    public void OnConnectionChanged(StatsConnectionState state)
    {
        if (state == StatsConnectionState.Disconnected && _lobby.Phase != MatchPhase.Idle)
        {
            _logger.LogInformation("Stats API connection lost; clearing the lobby");
            ApplyEffects(_lobby.OnDisconnected());
        }
    }

    /// <summary>Ctrl+R. On the favourites screen it goes back to the lobby (and shows it if it was hidden).</summary>
    public void TogglePanel()
    {
        if (_window.FavouritesOn)
        {
            if (!_window.PanelOn)
            {
                ApplyEffects(_lobby.TogglePanel());
            }

            CloseFavourites("back to the lobby");
            return;
        }

        ApplyEffects(_lobby.TogglePanel());
    }

    /// <summary>Ctrl+F: opens the favourites screen on its first page, or closes it.</summary>
    public void ToggleFavourites()
    {
        if (_window.FavouritesOn)
        {
            CloseFavourites("shortcut");
        }
        else
        {
            OpenFavourites();
        }
    }

    private void OpenFavourites()
    {
        _favouritesTimer.Stop();
        _favourites.Playlist = _vm.Playlist;
        _favourites.IsSample = SampleLobby;
        _favourites.ShowFavourites();
        _window.SetFavouritesVisible(true);
        _logger.LogInformation("Favourites opened ({Count} saved, {PerPage} per page)", _favourites.FavouriteCount, _favourites.PerPage);
    }

    /// <summary>
    /// Closes the favourites screen: the lobby takes its place if it is showing, else the overlay fades out. Once out of
    /// sight its rows go, so their pages are closed.
    /// </summary>
    public void CloseFavourites(string reason)
    {
        if (!_window.FavouritesOn)
        {
            return;
        }

        _window.SetFavouritesVisible(false);
        _favouritesTimer.Stop();
        _favouritesTimer.Start();
        _logger.LogInformation("Favourites closed ({Reason})", reason);
    }

    private void ClearFavouritesWhenHidden()
    {
        _favouritesTimer.Stop();
        if (!_window.FavouritesOn)
        {
            _favourites.ClearFavourites();
        }
    }

    /// <summary>Ctrl+G: next enabled playlist, every row moves its crop to it, the strip shows for 1.5 s.</summary>
    public void CyclePlaylist()
    {
        var next = PlaylistCycler.Next(_vm.Playlist, _settings.EnabledPlaylists);
        _userCycled = true;
        _vm.Playlist = next; // the favourites follow (see the constructor)
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
        _favourites.ApplySettings(settings);
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
        CloseFavourites("preview");
        _lobby = new LobbyStateMachine { KeepOpenAtKickoff = _settings.KeepOpenAtKickoff };
        _phaseSeen = MatchPhase.Idle;
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
            CloseFavouritesAtKickoff();
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
                    Seen(added.Player);
                    break;

                case PlayerChangedEffect changed:
                    _vm.AddOrUpdate(changed.After);
                    Seen(changed.After);
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

                    if (visibility.Visible && visibility.Reason == PanelChangeReason.MatchStarted)
                    {
                        CloseFavourites("a new lobby"); // the new lobby is what the user wants to see
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
        CloseFavouritesAtKickoff();
    }

    /// <summary>
    /// At kickoff the favourites screen closes, like the lobby does (opened during the match, it stays until closed).
    /// </summary>
    private void CloseFavouritesAtKickoff()
    {
        var kickoff = _lobby.Phase == MatchPhase.Live && _phaseSeen != MatchPhase.Live;
        _phaseSeen = _lobby.Phase;
        if (kickoff)
        {
            CloseFavourites("kickoff");
        }
    }

    private void Seen(LobbyPlayer player)
    {
        if (!IsPreviewing)
        {
            PlayerSeen?.Invoke(player);
        }
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
        _favouritesTimer.Stop();
    }
}

using System.Windows.Threading;
using Microsoft.Extensions.Logging;
using RLOverlay.App.Overlay;
using RLOverlay.App.ViewModels;
using RLOverlay.Core.Config;
using RLOverlay.Core.Lobby;
using RLOverlay.Core.Playlists;
using RLOverlay.Core.Providers;
using RLOverlay.Core.StatsApi;

namespace RLOverlay.App.Services;

/// <summary>
/// The glue between the lobby state machine, the stats provider, the panel view model and the overlay window. Runs on
/// the UI thread only: Stats API messages are marshalled here by the caller.
/// </summary>
internal sealed class OverlayCoordinator : IDisposable
{
    public static readonly TimeSpan StripDuration = TimeSpan.FromMilliseconds(1500);

    private readonly PanelViewModel _vm;
    private readonly OverlayWindow _window;
    private readonly LinkOpener _links;
    private readonly ILogger _logger;
    private readonly DispatcherTimer _stripTimer;
    private LobbyStateMachine _lobby = new();
    private CancellationTokenSource _matchCts = new();
    private OverlaySettings _settings;
    private bool _userCycled;
    private IPlayerStatsProvider? _providerBeforePreview;

    public OverlayCoordinator(
        OverlaySettings settings,
        IPlayerStatsProvider provider,
        PanelViewModel vm,
        OverlayWindow window,
        LinkOpener links,
        ILogger<OverlayCoordinator> logger)
    {
        _settings = settings;
        Provider = provider;
        _vm = vm;
        _window = window;
        _links = links;
        _logger = logger;
        _stripTimer = new DispatcherTimer { Interval = StripDuration };
        _stripTimer.Tick += (_, _) => HideStrip();
        _vm.ApplySettings(settings);
        _vm.Playlist = ResolveInitialPlaylist();
    }

    /// <summary>The (caching) provider used for new lookups. Swapped when settings change.</summary>
    public IPlayerStatsProvider Provider { get; set; }

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

    /// <summary>Ctrl+G: next enabled playlist, every row re-renders, the strip shows for 1.5 s.</summary>
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

    public void OpenProfile(PlayerRowViewModel row)
    {
        if (row.ProfileUrl is { } url)
        {
            _links.Open(url);
        }
    }

    public void ApplySettings(OverlaySettings settings)
    {
        _settings = settings;
        _vm.ApplySettings(settings);
        if (!settings.EnabledPlaylists.Contains(_vm.Playlist))
        {
            _vm.Playlist = ResolveInitialPlaylist();
        }
    }

    /// <summary>True while the settings window's Preview runs (real Stats API messages are ignored meanwhile).</summary>
    public bool IsPreviewing => _providerBeforePreview is not null;

    /// <summary>Settings Preview: forget the real lobby and look players up with <paramref name="previewProvider"/>.</summary>
    public void BeginPreview(IPlayerStatsProvider previewProvider)
    {
        _providerBeforePreview ??= Provider;
        Provider = previewProvider;
        Reset();
    }

    /// <summary>Ends the Preview: the panel is cleared and the real provider is used again.</summary>
    public void EndPreview()
    {
        Reset();
        if (_providerBeforePreview is { } real)
        {
            Provider = real;
            _providerBeforePreview = null;
        }
    }

    /// <summary>Swaps the real provider after a settings change (kept aside while a Preview runs).</summary>
    public void ReplaceProvider(IPlayerStatsProvider provider)
    {
        if (_providerBeforePreview is not null)
        {
            _providerBeforePreview = provider;
        }
        else
        {
            Provider = provider;
        }
    }

    /// <summary>Forget the current lobby (used before a Preview starts).</summary>
    public void Reset()
    {
        CancelLookups();
        HideStrip();
        _lobby = new LobbyStateMachine();
        _userCycled = false;
        _vm.Phase = MatchPhase.Idle;
        _vm.Clear();
        _vm.Playlist = ResolveInitialPlaylist();
        ShowPanel(false);
        LobbyChanged?.Invoke();
    }

    /// <summary>Shows or hides the panel; the view model is frozen while hidden so nothing changes during the fade.</summary>
    private void ShowPanel(bool visible)
    {
        if (visible)
        {
            _vm.IsFrozen = false;
            _vm.Phase = _lobby.Phase;
            _vm.Refresh();
        }
        else
        {
            _vm.IsFrozen = true;
        }

        _window.SetPanelVisible(visible);
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
                    CancelLookups();
                    _userCycled = false;
                    _vm.Phase = started.Phase;
                    _vm.Clear();
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
                    var row = _vm.AddOrUpdate(added.Player);
                    if (row.State == RowState.Loading)
                    {
                        StartLookup(added.Player);
                    }

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
                    CancelLookups();
                    HideStrip();
                    ShowPanel(false);
                    _vm.Phase = MatchPhase.Idle;
                    _vm.Clear();
                    break;
            }
        }

        _vm.Phase = _lobby.Phase;
        _vm.Refresh();
        LobbyChanged?.Invoke();
    }

    private async void StartLookup(LobbyPlayer player)
    {
        var token = _matchCts.Token;
        try
        {
            var profile = await Provider.GetProfileAsync(player.Id, token);
            if (!token.IsCancellationRequested)
            {
                _vm.SetProfile(player.Key, profile);
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            // Match ended while the lookup was running.
        }
        catch (Exception ex)
        {
            // The caching provider already turns provider failures into null; this is a last line of defence.
            _logger.LogWarning(ex, "Lookup failed for {Player}", player.Name);
            if (!token.IsCancellationRequested)
            {
                _vm.SetProfile(player.Key, null);
            }
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

    private void CancelLookups()
    {
        _matchCts.Cancel();
        _matchCts.Dispose();
        _matchCts = new CancellationTokenSource();
    }

    public void Dispose()
    {
        _stripTimer.Stop();
        _matchCts.Cancel();
        _matchCts.Dispose();
    }
}

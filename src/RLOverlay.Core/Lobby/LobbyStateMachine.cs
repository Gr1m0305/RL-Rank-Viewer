using RLOverlay.Core.Players;
using RLOverlay.Core.StatsApi;

namespace RLOverlay.Core.Lobby;

/// <summary>
/// Pure match-lifecycle and roster state. Feed it Stats API messages and user toggles; it returns the effects for the
/// UI to apply. Not thread-safe: call from one thread (the UI thread). See docs/ARCHITECTURE.md for the diagram.
/// </summary>
public sealed class LobbyStateMachine
{
    /// <summary>A player missing from this many consecutive UpdateState ticks is treated as gone.</summary>
    public const int MissedTicksBeforeRemoval = 3;

    private readonly Dictionary<string, Entry> _players = new(StringComparer.Ordinal);
    private long _joinSequence;

    // Ended phase: the game has destroyed the match (back in the menus). From then on, lobby activity means a new match.
    private bool _destroyed;

    // The panel is open because the user pressed Ctrl+R (not auto-shown at load-in).
    private bool _openedManually;

    public MatchPhase Phase { get; private set; } = MatchPhase.Idle;

    public bool PanelVisible { get; private set; }

    public string? MatchGuid { get; private set; }

    public int? MatchPlaylistId { get; private set; }

    /// <summary>Players in join order.</summary>
    public IReadOnlyList<LobbyPlayer> Players =>
        _players.Values.OrderBy(e => e.Sequence).Select(e => e.Player).ToList();

    public IReadOnlyList<LobbyEffect> Apply(StatsMessage message)
    {
        var effects = new List<LobbyEffect>();
        switch (message)
        {
            case UpdateStateMessage update:
                ApplyUpdate(update, effects);
                break;
            case PlayerPresenceMessage presence:
                ApplyPresence(presence, effects);
                break;
            case MatchEventMessage evt:
                ApplyEvent(evt, effects);
                break;
        }

        return effects;
    }

    /// <summary>Ctrl+R: toggle the panel in any phase. A manual open lasts until Ctrl+R or the next kickoff.</summary>
    public IReadOnlyList<LobbyEffect> TogglePanel()
    {
        PanelVisible = !PanelVisible;
        _openedManually = PanelVisible;
        return [new PanelVisibilityEffect(PanelVisible, PanelChangeReason.Manual)];
    }

    /// <summary>The Stats API socket closed (game exited or crashed): hide and clear.</summary>
    public IReadOnlyList<LobbyEffect> OnDisconnected()
    {
        var effects = new List<LobbyEffect>();
        EndMatch(PanelChangeReason.Disconnected, effects);
        return effects;
    }

    private void ApplyUpdate(UpdateStateMessage update, List<LobbyEffect> effects)
    {
        if (Phase == MatchPhase.Ended)
        {
            // Match over: the roster stays exactly as it was (players leaving the podium stay listed) until a new match.
            if (update.Players.Count == 0 || !IsNewMatch(update.MatchGuid))
            {
                return;
            }

            StartMatch(update.MatchGuid, autoShow: update.Game.BallIsUntouched, effects);
        }
        else if (Phase != MatchPhase.Idle && update.MatchGuid is not null && MatchGuid is not null
            && update.MatchGuid != MatchGuid)
        {
            // A different match without a MatchDestroyed in between.
            StartMatch(update.MatchGuid, autoShow: update.Game.BallIsUntouched, effects);
        }

        if (Phase == MatchPhase.Idle)
        {
            if (update.Players.Count == 0)
            {
                return;
            }

            // Ball already touched means we connected mid-match: track it but don't pop the panel up.
            StartMatch(update.MatchGuid, autoShow: update.Game.BallIsUntouched, effects);
        }

        MatchGuid ??= update.MatchGuid;

        if (update.Game.PlaylistId is { } playlistId && playlistId != MatchPlaylistId)
        {
            MatchPlaylistId = playlistId;
            effects.Add(new MatchPlaylistEffect(playlistId));
        }

        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var p in update.Players)
        {
            var name = string.IsNullOrWhiteSpace(p.Name) ? "Player" : p.Name;
            var key = LobbyPlayer.KeyFor(p.PrimaryId, name);
            if (!seen.Add(key))
            {
                continue;
            }

            var id = PlatformId.Parse(p.PrimaryId, name);
            if (_players.TryGetValue(key, out var entry))
            {
                entry.MissedTicks = 0;
                var team = p.TeamNum >= 0 ? p.TeamNum : entry.Player.TeamNum;
                if (entry.Player.TeamNum != team || entry.Player.Name != name)
                {
                    var before = entry.Player;
                    entry.Player = before with { Name = name, TeamNum = team, Id = id };
                    effects.Add(new PlayerChangedEffect(before, entry.Player));
                }
            }
            else
            {
                AddPlayer(new LobbyPlayer(key, name, id, p.TeamNum), effects);
            }
        }

        foreach (var entry in _players.Values.Where(e => !seen.Contains(e.Player.Key)).ToList())
        {
            entry.MissedTicks++;
            if (entry.MissedTicks >= MissedTicksBeforeRemoval)
            {
                _players.Remove(entry.Player.Key);
                effects.Add(new PlayerRemovedEffect(entry.Player));
            }
        }
    }

    private void ApplyPresence(PlayerPresenceMessage presence, List<LobbyEffect> effects)
    {
        var name = string.IsNullOrWhiteSpace(presence.PlayerName) ? "Player" : presence.PlayerName;
        var key = LobbyPlayer.KeyFor(presence.PrimaryId, name);

        if (Phase == MatchPhase.Ended)
        {
            // Leaving after the match keeps the player listed; only a join for a new match starts over.
            if (!presence.Joined || !IsNewMatch(presence.MatchGuid))
            {
                return;
            }

            StartMatch(presence.MatchGuid, autoShow: true, effects);
        }

        if (presence.Joined)
        {
            if (Phase == MatchPhase.Idle)
            {
                StartMatch(presence.MatchGuid, autoShow: true, effects);
            }

            if (!_players.ContainsKey(key))
            {
                AddPlayer(new LobbyPlayer(key, name, PlatformId.Parse(presence.PrimaryId, name), -1), effects);
            }

            return;
        }

        if (_players.Remove(key, out var gone))
        {
            effects.Add(new PlayerRemovedEffect(gone.Player));
        }
    }

    private void ApplyEvent(MatchEventMessage evt, List<LobbyEffect> effects)
    {
        switch (evt.Kind)
        {
            case MatchEventKind.MatchCreated:
                if (Phase is MatchPhase.Idle or MatchPhase.Live or MatchPhase.Ended
                    || (evt.MatchGuid is not null && MatchGuid is not null && evt.MatchGuid != MatchGuid))
                {
                    StartMatch(evt.MatchGuid, autoShow: true, effects);
                }

                break;

            case MatchEventKind.MatchInitialized:
                if (Phase == MatchPhase.Idle || (Phase == MatchPhase.Ended && IsNewMatch(evt.MatchGuid)))
                {
                    StartMatch(evt.MatchGuid, autoShow: true, effects);
                }

                break;

            case MatchEventKind.RoundStarted:
                // Kickoff. Also ends a manual Ctrl+R open.
                if (Phase == MatchPhase.Ended)
                {
                    if (!IsNewMatch(evt.MatchGuid))
                    {
                        break;
                    }

                    StartMatch(evt.MatchGuid, autoShow: false, effects);
                }
                else if (Phase == MatchPhase.Idle)
                {
                    MatchGuid = evt.MatchGuid;
                }

                Phase = MatchPhase.Live;
                if (PanelVisible)
                {
                    PanelVisible = false;
                    effects.Add(new PanelVisibilityEffect(false, PanelChangeReason.Kickoff));
                }

                break;

            case MatchEventKind.MatchEnded:
            case MatchEventKind.PodiumStart:
                if (Phase is MatchPhase.PreKickoff or MatchPhase.Live)
                {
                    Phase = MatchPhase.Ended;
                    _destroyed = false;
                }

                break;

            case MatchEventKind.MatchDestroyed:
                if (_players.Count == 0)
                {
                    EndMatch(PanelChangeReason.MatchDestroyed, effects);
                    break;
                }

                // Keep the roster for after the game. A panel the game popped up (lobby left before kickoff) goes
                // away; one the user opened with Ctrl+R stays open.
                if (Phase != MatchPhase.Idle)
                {
                    Phase = MatchPhase.Ended;
                }

                _destroyed = true;
                if (PanelVisible && !_openedManually)
                {
                    PanelVisible = false;
                    effects.Add(new PanelVisibilityEffect(false, PanelChangeReason.MatchDestroyed));
                }

                break;
        }
    }

    /// <summary>In the Ended phase: does this activity belong to a different match than the one that ended?</summary>
    private bool IsNewMatch(string? matchGuid) =>
        (matchGuid is not null && MatchGuid is not null && matchGuid != MatchGuid)
        || (_destroyed && (matchGuid is null || MatchGuid is null));

    private void StartMatch(string? matchGuid, bool autoShow, List<LobbyEffect> effects)
    {
        if (_players.Count > 0 || Phase != MatchPhase.Idle)
        {
            _players.Clear();
            effects.Add(new LobbyClearedEffect(PanelChangeReason.MatchStarted));
        }

        MatchGuid = matchGuid;
        MatchPlaylistId = null;
        _destroyed = false;
        Phase = autoShow ? MatchPhase.PreKickoff : MatchPhase.Live;
        effects.Add(new MatchStartedEffect(matchGuid, Phase));

        if (autoShow && !PanelVisible)
        {
            PanelVisible = true;
            _openedManually = false;
            effects.Add(new PanelVisibilityEffect(true, PanelChangeReason.MatchStarted));
        }
    }

    private void EndMatch(PanelChangeReason reason, List<LobbyEffect> effects)
    {
        if (PanelVisible)
        {
            PanelVisible = false;
            effects.Add(new PanelVisibilityEffect(false, reason));
        }

        if (Phase != MatchPhase.Idle || _players.Count > 0)
        {
            _players.Clear();
            effects.Add(new LobbyClearedEffect(reason));
        }

        Phase = MatchPhase.Idle;
        MatchGuid = null;
        MatchPlaylistId = null;
        _destroyed = false;
    }

    private void AddPlayer(LobbyPlayer player, List<LobbyEffect> effects)
    {
        _players[player.Key] = new Entry(player, _joinSequence++);
        effects.Add(new PlayerAddedEffect(player));
    }

    private sealed class Entry(LobbyPlayer player, long sequence)
    {
        public LobbyPlayer Player { get; set; } = player;

        public long Sequence { get; } = sequence;

        public int MissedTicks { get; set; }
    }
}

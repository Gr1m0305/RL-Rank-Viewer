using RLOverlay.Core.Players;
using RLOverlay.Core.Playlists;
using RLOverlay.Core.StatsApi;

namespace RLOverlay.Core.Lobby;

/// <summary>
/// Pure match-lifecycle and roster state. Feed it Stats API messages and user toggles; it returns the effects for the
/// UI to apply. Not thread-safe: call from one thread (the UI thread).
/// </summary>
public sealed class LobbyStateMachine
{
    /// <summary>A player missing from this many consecutive UpdateState ticks has left (they stay listed as left).</summary>
    public const int MissedTicksBeforeRemoval = 3;

    /// <summary>
    /// A new game needs this many players before it replaces the lobby kept after a game. Freeplay, training and other
    /// sessions with just the local player leave the kept lobby alone.
    /// </summary>
    public const int PlayersForNewGame = 2;

    private readonly Dictionary<string, Entry> _players = new(StringComparer.Ordinal);
    private long _joinSequence;

    // Ended phase: the game has destroyed the match (back in the menus). From then on, lobby activity means a new match.
    private bool _destroyed;

    // The panel is open because the user pressed Ctrl+R (not auto-shown at load-in).
    private bool _openedManually;

    // While a finished game's lobby is kept, the session after it is followed here, out of sight, until it has
    // PlayersForNewGame players (then it becomes the lobby) or ends without them (freeplay, training: dropped).
    private LobbyStateMachine? _next;

    // The phase before the session after it was followed: restored if that session is the same match again.
    private MatchPhase _phaseBeforeNext;

    // The last UpdateState had Game.bReplay set: a goal replay or a Match History replay is playing.
    private bool _replayPlaying;

    /// <summary>
    /// The "Keep the lobby open when the match starts" setting: kickoff no longer hides the panel (whether the game
    /// popped it up or Ctrl+R opened it). Ctrl+R still hides it, and leaving the match still hides a panel the game
    /// popped up.
    /// </summary>
    public bool KeepOpenAtKickoff { get; set; }

    public MatchPhase Phase { get; private set; } = MatchPhase.Idle;

    public bool PanelVisible { get; private set; }

    public string? MatchGuid { get; private set; }

    public int? MatchPlaylistId { get; private set; }

    /// <summary>Players in join order.</summary>
    public IReadOnlyList<LobbyPlayer> Players =>
        _players.Values.OrderBy(e => e.Sequence).Select(e => e.Player).ToList();

    public IReadOnlyList<LobbyEffect> Apply(StatsMessage message)
    {
        if (message is UpdateStateMessage tick)
        {
            _replayPlaying = tick.Game.Replay == true;
        }

        if (_next is not null || (Holding && StartsNewSession(message)))
        {
            return FollowNext(message);
        }

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

    /// <summary>
    /// Ctrl+R: toggle the panel in any phase. A manual open lasts until Ctrl+R or the next kickoff (only Ctrl+R with
    /// <see cref="KeepOpenAtKickoff"/>).
    /// </summary>
    public IReadOnlyList<LobbyEffect> TogglePanel()
    {
        PanelVisible = !PanelVisible;
        _openedManually = PanelVisible;
        return [new PanelVisibilityEffect(PanelVisible, PanelChangeReason.Manual)];
    }

    /// <summary>A game with at least two players is over (or was left): its lobby stays until the next game.</summary>
    private bool Holding => Phase is MatchPhase.Ended or MatchPhase.Live && _players.Count >= PlayersForNewGame;

    /// <summary>Would this message start a new match? (The same tests as the Ended and Live handling below.)</summary>
    private bool StartsNewSession(StatsMessage message) => message switch
    {
        UpdateStateMessage u => u.Players.Count > 0 && (Phase == MatchPhase.Ended
            ? IsNewMatch(u.MatchGuid)
            : u.MatchGuid is not null && MatchGuid is not null && u.MatchGuid != MatchGuid),
        PlayerPresenceMessage p => Phase == MatchPhase.Ended && p.Joined && IsNewMatch(p.MatchGuid),
        MatchEventMessage { Kind: MatchEventKind.MatchCreated } evt => !IsSameMatch(evt.MatchGuid),
        MatchEventMessage { Kind: MatchEventKind.MatchInitialized or MatchEventKind.RoundStarted } evt =>
            Phase == MatchPhase.Ended && IsNewMatch(evt.MatchGuid),
        _ => false,
    };

    /// <summary>
    /// The session after a kept lobby. It runs in its own state machine whose effects aren't shown, so the kept lobby
    /// stays on screen; once it has two players it takes over, and if it ends first it is forgotten.
    /// </summary>
    private IReadOnlyList<LobbyEffect> FollowNext(StatsMessage message)
    {
        if (_next is null)
        {
            _next = new LobbyStateMachine { KeepOpenAtKickoff = KeepOpenAtKickoff };
            _phaseBeforeNext = Phase;
            Phase = MatchPhase.Ended; // whatever was kept is over now
        }

        _next.Apply(message);
        if (_next._players.Values.Count(p => !p.Player.HasLeft) >= PlayersForNewGame)
        {
            return IsSameSession(_next) ? Merge(_next) : Adopt(_next);
        }

        if (_next.Phase is MatchPhase.Idle or MatchPhase.Ended)
        {
            // Freeplay, training or a lone private match ended: keep showing the last game's lobby.
            _next = null;
            _destroyed = true;
        }

        return [];
    }

    /// <summary>
    /// Is the followed session the kept match itself? Skipping through a replay makes the game create the match again,
    /// with the same MatchGuid (or, without one, with the same players while a replay plays).
    /// </summary>
    private bool IsSameSession(LobbyStateMachine next) =>
        IsSameMatch(next.MatchGuid)
        || (_replayPlaying && next.MatchGuid is null && MatchGuid is null && next._players.Keys.All(_players.ContainsKey));

    /// <summary>
    /// The followed session is the kept match again (a skip in a replay): its players are merged in. The lobby isn't
    /// cleared, the screen stays as it was (shown or hidden), and the players' pages stay loaded.
    /// </summary>
    private List<LobbyEffect> Merge(LobbyStateMachine next)
    {
        _next = null;
        _destroyed = false;
        Phase = _phaseBeforeNext is MatchPhase.PreKickoff ? MatchPhase.PreKickoff : MatchPhase.Live;
        MatchGuid ??= next.MatchGuid;
        var effects = new List<LobbyEffect>();
        if (next.MatchPlaylistId is { } playlistId && playlistId != MatchPlaylistId)
        {
            MatchPlaylistId = playlistId;
            effects.Add(new MatchPlaylistEffect(playlistId));
        }

        foreach (var entry in next._players.Values.OrderBy(e => e.Sequence))
        {
            var player = entry.Player;
            if (!_players.TryGetValue(player.Key, out var known))
            {
                AddPlayer(player, effects);
                continue;
            }

            known.MissedTicks = 0;
            var team = player.TeamNum >= 0 ? player.TeamNum : known.Player.TeamNum;
            if (known.Player.TeamNum != team || known.Player.Name != player.Name || known.Player.HasLeft)
            {
                var before = known.Player;
                known.Player = before with { Name = player.Name, TeamNum = team, Id = player.Id, HasLeft = false };
                effects.Add(new PlayerChangedEffect(before, known.Player));
            }
        }

        return effects;
    }

    /// <summary>
    /// The same match was created again (MatchCreated with its MatchGuid): a skip in a replay. The
    /// lobby and the screen stay as they are; after the replay had ended or been left, its roster follows the game again.
    /// </summary>
    private void Resume()
    {
        if (Phase == MatchPhase.Ended)
        {
            Phase = MatchPhase.Live;
        }

        _destroyed = false;
    }

    /// <summary>The followed session has enough players: it replaces the kept lobby, as if it had started normally.</summary>
    private List<LobbyEffect> Adopt(LobbyStateMachine next)
    {
        _next = null;
        var effects = new List<LobbyEffect>();
        if (_players.Count > 0 || Phase != MatchPhase.Idle)
        {
            _players.Clear();
            effects.Add(new LobbyClearedEffect(PanelChangeReason.MatchStarted));
        }

        MatchGuid = next.MatchGuid;
        MatchPlaylistId = next.MatchPlaylistId;
        _destroyed = next._destroyed;
        Phase = next.Phase;
        effects.Add(new MatchStartedEffect(MatchGuid, Phase));
        if (MatchPlaylistId is { } playlistId)
        {
            effects.Add(new MatchPlaylistEffect(playlistId));
        }

        foreach (var entry in next._players.Values.OrderBy(e => e.Sequence))
        {
            _players[entry.Player.Key] = new Entry(entry.Player, _joinSequence++) { MissedTicks = entry.MissedTicks };
            effects.Add(new PlayerAddedEffect(entry.Player));
        }

        if (next.PanelVisible && !PanelVisible)
        {
            PanelVisible = true;
            _openedManually = false;
            effects.Add(new PanelVisibilityEffect(true, PanelChangeReason.MatchStarted));
        }
        else if (!next.PanelVisible && PanelVisible && Phase == MatchPhase.Live && !KeepOpenAtKickoff)
        {
            PanelVisible = false;
            effects.Add(new PanelVisibilityEffect(false, PanelChangeReason.Kickoff));
        }

        return effects;
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

        if (update.Players.Count == 0)
        {
            // Nobody at all: the game is rebuilding the match (a skip in a replay), not everyone leaving at once.
            return;
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
                if (entry.Player.TeamNum != team || entry.Player.Name != name || entry.Player.HasLeft)
                {
                    var before = entry.Player;
                    entry.Player = before with { Name = name, TeamNum = team, Id = id, HasLeft = false };
                    effects.Add(new PlayerChangedEffect(before, entry.Player));
                }
            }
            else
            {
                AddPlayer(new LobbyPlayer(key, name, id, p.TeamNum), effects);
            }
        }

        foreach (var entry in _players.Values.Where(e => !seen.Contains(e.Player.Key) && !e.Player.HasLeft).ToList())
        {
            entry.MissedTicks++;
            if (entry.MissedTicks >= MissedTicksBeforeRemoval)
            {
                MarkLeft(entry, effects);
            }
        }
    }

    /// <summary>
    /// A player left mid-match: they stay listed, marked as left, until the next game. Someone who left before their
    /// team was known never really played in it and is removed.
    /// </summary>
    private void MarkLeft(Entry entry, List<LobbyEffect> effects)
    {
        if (entry.Player.TeamNum is not (0 or 1))
        {
            _players.Remove(entry.Player.Key);
            effects.Add(new PlayerRemovedEffect(entry.Player));
            return;
        }

        var before = entry.Player;
        entry.Player = before with { HasLeft = true };
        effects.Add(new PlayerChangedEffect(before, entry.Player));
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

            if (!_players.TryGetValue(key, out var known))
            {
                AddPlayer(new LobbyPlayer(key, name, PlatformId.Parse(presence.PrimaryId, name), -1), effects);
            }
            else if (known.Player.HasLeft)
            {
                // Back in the same match.
                known.MissedTicks = 0;
                var before = known.Player;
                known.Player = before with { HasLeft = false };
                effects.Add(new PlayerChangedEffect(before, known.Player));
            }

            return;
        }

        if (_players.TryGetValue(key, out var gone) && !gone.Player.HasLeft)
        {
            MarkLeft(gone, effects);
        }
    }

    private void ApplyEvent(MatchEventMessage evt, List<LobbyEffect> effects)
    {
        switch (evt.Kind)
        {
            case MatchEventKind.MatchCreated:
                if (IsSameMatch(evt.MatchGuid))
                {
                    Resume();
                    break;
                }

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
                // Kickoff. Also ends a manual Ctrl+R open (unless the panel is kept open at kickoff).
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
                if (PanelVisible && !KeepOpenAtKickoff)
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
                    Finished(effects);
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
                if (Phase == MatchPhase.Live)
                {
                    Finished(effects); // left (or forfeited) after kickoff: it counts
                }

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

    /// <summary>
    /// A match was played to its end or left after kickoff: the players' ratings may have moved. Not for a replay
    /// of one (the game marks every tick of a replay with bReplay), an offline game (no MatchGuid) or a private
    /// match or other playlist without a rating (a playlist id the catalog doesn't map); an unknown playlist counts.
    /// </summary>
    private void Finished(List<LobbyEffect> effects)
    {
        var rated = MatchGuid is not null
            && !(MatchPlaylistId is { } playlistId && PlaylistCatalog.FromGameId(playlistId) is null);
        if (!_replayPlaying && rated && _players.Count > 0)
        {
            effects.Add(new MatchFinishedEffect(MatchGuid));
        }
    }

    /// <summary>Activity with the MatchGuid of the match in the lobby (both known).</summary>
    private bool IsSameMatch(string? matchGuid) => matchGuid is not null && MatchGuid is not null && matchGuid == MatchGuid;

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
        _next = null;
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

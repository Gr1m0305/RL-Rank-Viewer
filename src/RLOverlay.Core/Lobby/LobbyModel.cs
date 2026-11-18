using RLOverlay.Core.Players;

namespace RLOverlay.Core.Lobby;

public enum MatchPhase
{
    /// <summary>No match (menus, or not connected).</summary>
    Idle,

    /// <summary>Match exists, players loading in, before the first kickoff. Panel auto-shows.</summary>
    PreKickoff,

    /// <summary>After kickoff. Panel hidden unless opened manually.</summary>
    Live,

    /// <summary>
    /// Match over (MatchEnded, podium or MatchDestroyed). The roster is frozen so the last lobby can be reopened with
    /// Ctrl+R and its profiles opened after the game; the next game with at least two players or a lost connection (game
    /// closed) clears it. Freeplay and training in between don't.
    /// </summary>
    Ended,
}

public enum PanelChangeReason
{
    MatchStarted,
    Kickoff,
    Manual,
    MatchDestroyed,
    Disconnected,
}

/// <summary>A player in the current match.</summary>
/// <param name="Key">Stable roster key: the normalised PrimaryId, or <c>name:&lt;Name&gt;</c> for bots / id-less players.</param>
/// <param name="TeamNum">0 = Blue, 1 = Orange, -1 = not known yet (joined, no UpdateState tick seen).</param>
/// <param name="HasLeft">
/// The player left the match. They stay listed (marked as left) until the next game, so someone who quit early can
/// still be looked up; coming back clears it.
/// </param>
public sealed record LobbyPlayer(string Key, string Name, PlatformId Id, int TeamNum, bool HasLeft = false)
{
    public static string KeyFor(string? primaryId, string name)
    {
        var id = PlatformId.Parse(primaryId, name);
        return id.IsBot ? $"name:{name}" : id.CacheKey;
    }
}

public abstract record LobbyEffect;

/// <summary>A new match began; any previous roster has been cleared.</summary>
public sealed record MatchStartedEffect(string? MatchGuid, MatchPhase Phase) : LobbyEffect;

public sealed record PlayerAddedEffect(LobbyPlayer Player) : LobbyEffect;

/// <summary>
/// A player who joined and left before their team was known (they were never really in the match). Players who were
/// in the match aren't removed when they leave: they change to <see cref="LobbyPlayer.HasLeft"/>.
/// </summary>
public sealed record PlayerRemovedEffect(LobbyPlayer Player) : LobbyEffect;

/// <summary>
/// Team, display name or left/back changed (e.g. team learned from the first UpdateState after PlayerJoined, or the
/// player quit).
/// </summary>
public sealed record PlayerChangedEffect(LobbyPlayer Before, LobbyPlayer After) : LobbyEffect;

public sealed record PanelVisibilityEffect(bool Visible, PanelChangeReason Reason) : LobbyEffect;

/// <summary>The game reported the match's playlist id (Game.PlaylistId).</summary>
public sealed record MatchPlaylistEffect(int PlaylistId) : LobbyEffect;

/// <summary>Roster cleared: a new match is starting, or the connection was lost (game closed). Back to Idle.</summary>
public sealed record LobbyClearedEffect(PanelChangeReason Reason) : LobbyEffect;

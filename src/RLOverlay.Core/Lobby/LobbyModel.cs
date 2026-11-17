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
    /// Ctrl+R and its profiles opened after the game; the next match or a lost connection (game closed) clears it.
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
public sealed record LobbyPlayer(string Key, string Name, PlatformId Id, int TeamNum)
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

public sealed record PlayerRemovedEffect(LobbyPlayer Player) : LobbyEffect;

/// <summary>Team or display name changed (e.g. team learned from the first UpdateState after PlayerJoined).</summary>
public sealed record PlayerChangedEffect(LobbyPlayer Before, LobbyPlayer After) : LobbyEffect;

public sealed record PanelVisibilityEffect(bool Visible, PanelChangeReason Reason) : LobbyEffect;

/// <summary>The game reported the match's playlist id (Game.PlaylistId).</summary>
public sealed record MatchPlaylistEffect(int PlaylistId) : LobbyEffect;

/// <summary>Roster cleared: a new match is starting, or the connection was lost (game closed). Back to Idle.</summary>
public sealed record LobbyClearedEffect(PanelChangeReason Reason) : LobbyEffect;

namespace RLOverlay.Core.StatsApi;

/// <summary>A parsed Stats API message. See docs/STATS_API_NOTES.md for the wire format.</summary>
public abstract record StatsMessage(string EventName, string? MatchGuid);

/// <summary>The periodic tick (<c>PacketSendRate</c> times per second).</summary>
public sealed record UpdateStateMessage(string? MatchGuid, IReadOnlyList<StatsPlayer> Players, GameState Game)
    : StatsMessage(StatsEventNames.UpdateState, MatchGuid);

/// <summary>One entry of <c>UpdateState.Players</c>. Only the fields the overlay uses are kept.</summary>
public sealed record StatsPlayer(
    string Name,
    string? PrimaryId,
    int? Shortcut,
    int TeamNum,
    int? Score,
    int? Goals,
    int? Shots,
    int? Assists,
    int? Saves,
    int? Demos);

/// <summary><c>UpdateState.Game</c>.</summary>
/// <param name="BallLastTouchTeam"><c>Game.Ball.TeamNum</c>: last team to touch the ball, 255 if untouched.</param>
public sealed record GameState(
    int? PlaylistId,
    int? TimeSeconds,
    bool? Overtime,
    int? BallLastTouchTeam,
    bool? Replay,
    bool? HasWinner,
    string? Arena)
{
    public const int BallUntouched = 255;

    public static GameState Empty { get; } = new(null, null, null, null, null, null, null);

    /// <summary>True when the ball has not been touched yet (pre-kickoff), or the field is missing.</summary>
    public bool BallIsUntouched => BallLastTouchTeam is null or BallUntouched;
}

public enum MatchEventKind
{
    MatchCreated,
    MatchInitialized,
    CountdownBegin,
    RoundStarted,
    MatchDestroyed,
    MatchEnded,
    PodiumStart,
    MatchPaused,
    MatchUnpaused,
    GoalScored,
    GoalReplayStart,
    GoalReplayWillEnd,
    GoalReplayEnd,
    ReplayCreated,
    ClockUpdatedSeconds,
    BallHit,
    BoostPickup,
    CrossbarHit,
    StatfeedEvent,
}

/// <summary>A discrete match event. <paramref name="WinnerTeamNum"/> is only set for <c>MatchEnded</c>.</summary>
public sealed record MatchEventMessage(MatchEventKind Kind, string? MatchGuid, int? WinnerTeamNum = null)
    : StatsMessage(Kind.ToString(), MatchGuid);

/// <summary><c>PlayerJoined</c> / <c>PlayerLeft</c>.</summary>
public sealed record PlayerPresenceMessage(bool Joined, string PlayerName, string? PrimaryId, string? MatchGuid)
    : StatsMessage(Joined ? StatsEventNames.PlayerJoined : StatsEventNames.PlayerLeft, MatchGuid);

/// <summary>An event name this version of the app does not know. Harmless; ignored.</summary>
public sealed record UnknownStatsMessage(string Name, string? MatchGuid) : StatsMessage(Name, MatchGuid);

public static class StatsEventNames
{
    public const string UpdateState = "UpdateState";
    public const string PlayerJoined = "PlayerJoined";
    public const string PlayerLeft = "PlayerLeft";
}

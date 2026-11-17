using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace RLOverlay.Core.Scenarios;

/// <summary>
/// A scripted Stats API session, used by tools/FakeStatsServer and by <c>--demo</c>.
/// Steps mutate a roster or emit events at given times; <see cref="ScenarioPlayer"/> also emits UpdateState ticks
/// with the current roster at <see cref="TickRate"/> while a match is active.
/// </summary>
public sealed class Scenario
{
    public string Name { get; set; } = "";

    public string? MatchGuid { get; set; }

    public int? PlaylistId { get; set; }

    public string Arena { get; set; } = "Stadium_P";

    /// <summary>UpdateState ticks per second (like the ini's PacketSendRate).</summary>
    public double TickRate { get; set; } = 10;

    /// <summary>Send <c>Data</c> as an escaped JSON string (some game builds do this).</summary>
    public bool DataAsString { get; set; }

    public List<ScenarioStep> Steps { get; set; } = [];

    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    public static Scenario Load(string path) => Parse(File.ReadAllText(path));

    public static Scenario Parse(string json) =>
        JsonSerializer.Deserialize<Scenario>(json, Options) ?? throw new InvalidDataException("Empty scenario");
}

public sealed class ScenarioStep
{
    /// <summary>Milliseconds from scenario start.</summary>
    public int At { get; set; }

    /// <summary>Emit a discrete event by name (MatchCreated, RoundStarted, MatchDestroyed, ...).</summary>
    public string? Event { get; set; }

    /// <summary>Extra Data fields for <see cref="Event"/> (MatchGuid is added automatically).</summary>
    public JsonObject? Data { get; set; }

    /// <summary>Add a player to the roster (also emits PlayerJoined).</summary>
    public ScenarioPlayerInfo? Join { get; set; }

    /// <summary>Remove a player by PrimaryId or name (also emits PlayerLeft).</summary>
    public string? Leave { get; set; }

    /// <summary>Move a player (PrimaryId or name) to a team.</summary>
    public ScenarioTeamChange? SetTeam { get; set; }

    /// <summary>Set Game.Ball.TeamNum (last touch). 255 = untouched.</summary>
    public int? TouchBall { get; set; }

    /// <summary>Send an arbitrary envelope verbatim.</summary>
    public JsonObject? Raw { get; set; }

    /// <summary>Start/stop UpdateState ticks explicitly (they start with the first join or MatchCreated
    /// and stop at MatchDestroyed by default).</summary>
    [JsonPropertyName("ticks")]
    public bool? Ticks { get; set; }
}

public sealed class ScenarioPlayerInfo
{
    public string Name { get; set; } = "";

    public string? PrimaryId { get; set; }

    public int Team { get; set; }

    /// <summary>Delay the team assignment: emit PlayerJoined first and only include the player in ticks
    /// after this many milliseconds (mimics a player still connecting).</summary>
    public int ConnectingMs { get; set; }
}

public sealed class ScenarioTeamChange
{
    public string Player { get; set; } = "";

    public int Team { get; set; }
}

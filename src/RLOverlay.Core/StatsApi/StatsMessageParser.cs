using System.Globalization;
using System.Text.Json;

namespace RLOverlay.Core.StatsApi;

/// <summary>
/// Parses one Stats API envelope (<c>{"Event": "...", "Data": {...}}</c>) into a typed <see cref="StatsMessage"/>.
/// Tolerant by design: every field is optional, <c>Data</c> may be an object or a JSON-encoded string,
/// key lookup falls back to case-insensitive, and numbers/booleans may arrive as strings.
/// </summary>
public static class StatsMessageParser
{
    private static readonly JsonDocumentOptions DocumentOptions = new()
    {
        AllowTrailingCommas = true,
        CommentHandling = JsonCommentHandling.Skip,
        MaxDepth = 128,
    };

    public static StatsMessage? TryParse(string json, out string? error) =>
        TryParse(System.Text.Encoding.UTF8.GetBytes(json), out error);

    public static StatsMessage? TryParse(ReadOnlyMemory<byte> json, out string? error)
    {
        error = null;
        try
        {
            using var doc = JsonDocument.Parse(json, DocumentOptions);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                error = "Envelope is not a JSON object.";
                return null;
            }

            var eventName = root.GetStringOrNull("Event");
            if (string.IsNullOrWhiteSpace(eventName))
            {
                error = "Envelope has no Event name.";
                return null;
            }

            JsonDocument? innerDoc = null;
            try
            {
                var data = default(JsonElement);
                if (root.TryGetPropertyLoose("Data", out var rawData))
                {
                    if (rawData.ValueKind == JsonValueKind.String)
                    {
                        // Some game builds send Data as an escaped JSON string: parse it again.
                        var inner = rawData.GetString();
                        if (!string.IsNullOrWhiteSpace(inner))
                        {
                            innerDoc = JsonDocument.Parse(inner, DocumentOptions);
                            data = innerDoc.RootElement;
                        }
                    }
                    else
                    {
                        data = rawData;
                    }
                }

                return Build(eventName.Trim(), data);
            }
            finally
            {
                innerDoc?.Dispose();
            }
        }
        catch (JsonException ex)
        {
            error = ex.Message;
            return null;
        }
    }

    private static StatsMessage Build(string eventName, JsonElement data)
    {
        var guid = data.ValueKind == JsonValueKind.Object ? data.GetStringOrNull("MatchGuid") : null;
        if (string.IsNullOrEmpty(guid))
        {
            guid = null;
        }

        if (eventName.Equals(StatsEventNames.UpdateState, StringComparison.OrdinalIgnoreCase))
        {
            return ParseUpdateState(guid, data);
        }

        if (eventName.Equals(StatsEventNames.PlayerJoined, StringComparison.OrdinalIgnoreCase)
            || eventName.Equals(StatsEventNames.PlayerLeft, StringComparison.OrdinalIgnoreCase))
        {
            var joined = eventName.Equals(StatsEventNames.PlayerJoined, StringComparison.OrdinalIgnoreCase);
            var name = data.GetStringOrNull("PlayerName") ?? data.GetStringOrNull("Name") ?? "";
            return new PlayerPresenceMessage(joined, name, data.GetStringOrNull("PrimaryId"), guid);
        }

        if (TryMapEvent(eventName, out var kind))
        {
            int? winner = kind == MatchEventKind.MatchEnded ? data.GetIntOrNull("WinnerTeamNum") : null;
            return new MatchEventMessage(kind, guid, winner);
        }

        return new UnknownStatsMessage(eventName, guid);
    }

    private static bool TryMapEvent(string name, out MatchEventKind kind)
    {
        // Aliases seen in community clients for older builds.
        var normalized = name switch
        {
            "ReplayStart" => nameof(MatchEventKind.GoalReplayStart),
            "ReplayWillEnd" => nameof(MatchEventKind.GoalReplayWillEnd),
            "ReplayEnd" => nameof(MatchEventKind.GoalReplayEnd),
            _ => name,
        };
        return Enum.TryParse(normalized, ignoreCase: true, out kind) && Enum.IsDefined(kind);
    }

    private static UpdateStateMessage ParseUpdateState(string? guid, JsonElement data)
    {
        var players = new List<StatsPlayer>();
        var game = GameState.Empty;
        if (data.ValueKind != JsonValueKind.Object)
        {
            return new UpdateStateMessage(guid, players, game);
        }

        if (data.TryGetPropertyLoose("Players", out var list) && list.ValueKind == JsonValueKind.Array)
        {
            foreach (var p in list.EnumerateArray())
            {
                if (p.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                players.Add(new StatsPlayer(
                    Name: p.GetStringOrNull("Name") ?? "",
                    PrimaryId: p.GetStringOrNull("PrimaryId"),
                    Shortcut: p.GetIntOrNull("Shortcut"),
                    TeamNum: p.GetIntOrNull("TeamNum") ?? -1,
                    Score: p.GetIntOrNull("Score"),
                    Goals: p.GetIntOrNull("Goals"),
                    Shots: p.GetIntOrNull("Shots"),
                    Assists: p.GetIntOrNull("Assists"),
                    Saves: p.GetIntOrNull("Saves"),
                    Demos: p.GetIntOrNull("Demos")));
            }
        }

        if (data.TryGetPropertyLoose("Game", out var g) && g.ValueKind == JsonValueKind.Object)
        {
            int? ballTeam = null;
            if (g.TryGetPropertyLoose("Ball", out var ball) && ball.ValueKind == JsonValueKind.Object)
            {
                ballTeam = ball.GetIntOrNull("TeamNum");
            }

            game = new GameState(
                PlaylistId: g.GetIntOrNull("PlaylistId"),
                TimeSeconds: g.GetIntOrNull("TimeSeconds"),
                Overtime: g.GetBoolOrNull("bOvertime"),
                BallLastTouchTeam: ballTeam,
                Replay: g.GetBoolOrNull("bReplay"),
                HasWinner: g.GetBoolOrNull("bHasWinner"),
                Arena: g.GetStringOrNull("Arena"));
        }

        return new UpdateStateMessage(guid, players, game);
    }
}

/// <summary>Lenient <see cref="JsonElement"/> accessors used by the parser.</summary>
internal static class LooseJson
{
    public static bool TryGetPropertyLoose(this JsonElement element, string name, out JsonElement value)
    {
        value = default;
        if (element.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        if (element.TryGetProperty(name, out value))
        {
            return true;
        }

        foreach (var property in element.EnumerateObject())
        {
            if (property.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
            {
                value = property.Value;
                return true;
            }
        }

        return false;
    }

    public static string? GetStringOrNull(this JsonElement element, string name)
    {
        if (!element.TryGetPropertyLoose(name, out var v))
        {
            return null;
        }

        return v.ValueKind switch
        {
            JsonValueKind.String => v.GetString(),
            JsonValueKind.Number => v.GetRawText(),
            JsonValueKind.True => "true",
            JsonValueKind.False => "false",
            _ => null,
        };
    }

    public static int? GetIntOrNull(this JsonElement element, string name)
    {
        if (!element.TryGetPropertyLoose(name, out var v))
        {
            return null;
        }

        switch (v.ValueKind)
        {
            case JsonValueKind.Number:
                if (v.TryGetInt32(out var i))
                {
                    return i;
                }

                if (v.TryGetDouble(out var d) && d is >= int.MinValue and <= int.MaxValue)
                {
                    return (int)Math.Round(d);
                }

                return null;
            case JsonValueKind.String:
                var s = v.GetString();
                if (int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var si))
                {
                    return si;
                }

                if (double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var sd)
                    && sd is >= int.MinValue and <= int.MaxValue)
                {
                    return (int)Math.Round(sd);
                }

                return null;
            case JsonValueKind.True:
                return 1;
            case JsonValueKind.False:
                return 0;
            default:
                return null;
        }
    }

    public static bool? GetBoolOrNull(this JsonElement element, string name)
    {
        if (!element.TryGetPropertyLoose(name, out var v))
        {
            return null;
        }

        return v.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.Number => v.TryGetDouble(out var d) ? d != 0 : null,
            JsonValueKind.String => v.GetString()?.Trim().ToLowerInvariant() switch
            {
                "true" or "1" or "yes" => true,
                "false" or "0" or "no" => false,
                _ => null,
            },
            _ => null,
        };
    }
}

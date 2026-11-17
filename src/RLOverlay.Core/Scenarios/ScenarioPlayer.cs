using System.Text.Json;
using System.Text.Json.Nodes;

namespace RLOverlay.Core.Scenarios;

/// <summary>A Stats API envelope to send at <paramref name="AtMs"/> milliseconds after the scenario starts.</summary>
public sealed record TimedMessage(int AtMs, string Json);

/// <summary>
/// Turns a <see cref="Scenario"/> into a timeline of Stats API envelopes, exactly as the game would send them
/// (<c>{"Event": ..., "Data": {...}}</c>). Pure and deterministic, so it is unit-tested.
/// </summary>
public static class ScenarioPlayer
{
    public static IReadOnlyList<TimedMessage> BuildTimeline(Scenario scenario)
    {
        var steps = scenario.Steps.OrderBy(s => s.At).ToList();
        var tickInterval = scenario.TickRate > 0 ? (int)Math.Max(1, Math.Round(1000 / scenario.TickRate)) : 0;
        var end = steps.Count == 0 ? 0 : steps.Max(s => s.At);

        var roster = new List<RosterEntry>();
        var ballTeam = 255;
        var ticking = false;
        var output = new List<TimedMessage>();
        var nextTick = 0;
        var stepIndex = 0;

        // Walk time forward, interleaving steps and ticks. Steps at the same instant run before that instant's tick.
        while (stepIndex < steps.Count || (ticking && tickInterval > 0 && nextTick <= end))
        {
            var nextStepAt = stepIndex < steps.Count ? steps[stepIndex].At : int.MaxValue;
            var tickDue = ticking && tickInterval > 0 && nextTick <= end ? nextTick : int.MaxValue;

            if (nextStepAt <= tickDue)
            {
                var step = steps[stepIndex++];
                ApplyStep(scenario, step, roster, ref ballTeam, ref ticking, output);
                if (ticking && nextTick < step.At)
                {
                    nextTick = step.At;
                }

                continue;
            }

            output.Add(new TimedMessage(tickDue, Envelope(scenario, "UpdateState", UpdateStateData(scenario, roster, ballTeam, tickDue))));
            nextTick = tickDue + tickInterval;
        }

        return output;
    }

    private static void ApplyStep(
        Scenario scenario,
        ScenarioStep step,
        List<RosterEntry> roster,
        ref int ballTeam,
        ref bool ticking,
        List<TimedMessage> output)
    {
        if (step.Ticks is { } t)
        {
            ticking = t;
        }

        if (step.TouchBall is { } team)
        {
            ballTeam = team;
        }

        if (step.Join is { } join)
        {
            var entry = new RosterEntry(join.Name, join.PrimaryId ?? "", join.Team, step.At + Math.Max(0, join.ConnectingMs), roster.Count + 1);
            roster.Add(entry);
            var data = new JsonObject
            {
                ["PlayerName"] = join.Name,
                ["PrimaryId"] = join.PrimaryId ?? "",
            };
            output.Add(new TimedMessage(step.At, Envelope(scenario, "PlayerJoined", WithGuid(scenario, data))));
            ticking = true;
        }

        if (step.Leave is { } leave)
        {
            var entry = Find(roster, leave);
            if (entry is not null)
            {
                roster.Remove(entry);
                var data = new JsonObject
                {
                    ["PlayerName"] = entry.Name,
                    ["PrimaryId"] = entry.PrimaryId,
                };
                output.Add(new TimedMessage(step.At, Envelope(scenario, "PlayerLeft", WithGuid(scenario, data))));
            }
        }

        if (step.SetTeam is { } change && Find(roster, change.Player) is { } moved)
        {
            moved.Team = change.Team;
        }

        if (step.Event is { Length: > 0 } evt)
        {
            var data = step.Data is null ? new JsonObject() : (JsonObject)step.Data.DeepClone();
            output.Add(new TimedMessage(step.At, Envelope(scenario, evt, WithGuid(scenario, data))));
            switch (evt)
            {
                case "MatchCreated":
                    ticking = true;
                    ballTeam = 255;
                    break;
                case "MatchDestroyed":
                    ticking = false;
                    roster.Clear();
                    ballTeam = 255;
                    break;
            }
        }

        if (step.Raw is { } raw)
        {
            output.Add(new TimedMessage(step.At, raw.ToJsonString()));
        }
    }

    private static RosterEntry? Find(List<RosterEntry> roster, string idOrName) =>
        roster.FirstOrDefault(r => r.PrimaryId.Equals(idOrName, StringComparison.OrdinalIgnoreCase))
        ?? roster.FirstOrDefault(r => r.Name.Equals(idOrName, StringComparison.OrdinalIgnoreCase));

    private static JsonObject WithGuid(Scenario scenario, JsonObject data)
    {
        if (scenario.MatchGuid is { } guid && !data.ContainsKey("MatchGuid"))
        {
            var copy = new JsonObject { ["MatchGuid"] = guid };
            foreach (var (k, v) in data)
            {
                copy[k] = v?.DeepClone();
            }

            return copy;
        }

        return data;
    }

    private static JsonObject UpdateStateData(Scenario scenario, List<RosterEntry> roster, int ballTeam, int atMs)
    {
        var players = new JsonArray();
        foreach (var p in roster.Where(r => r.VisibleFromMs <= atMs))
        {
            players.Add(new JsonObject
            {
                ["Name"] = p.Name,
                ["PrimaryId"] = p.PrimaryId,
                ["Shortcut"] = p.Shortcut,
                ["TeamNum"] = p.Team,
                ["Score"] = 0,
                ["Goals"] = 0,
                ["Shots"] = 0,
                ["Assists"] = 0,
                ["Saves"] = 0,
                ["Touches"] = 0,
                ["CarTouches"] = 0,
                ["Demos"] = 0,
            });
        }

        var game = new JsonObject
        {
            ["Teams"] = new JsonArray(
                new JsonObject { ["Name"] = "Blue", ["TeamNum"] = 0, ["Score"] = 0, ["ColorPrimary"] = "1873FF", ["ColorSecondary"] = "E5E5E5" },
                new JsonObject { ["Name"] = "Orange", ["TeamNum"] = 1, ["Score"] = 0, ["ColorPrimary"] = "C26418", ["ColorSecondary"] = "E5E5E5" }),
            ["TimeSeconds"] = 300,
            ["bOvertime"] = false,
            ["Ball"] = new JsonObject { ["Speed"] = 0, ["TeamNum"] = ballTeam },
            ["bReplay"] = false,
            ["bHasWinner"] = false,
            ["Winner"] = "",
            ["Arena"] = scenario.Arena,
            ["bHasTarget"] = false,
        };
        if (scenario.PlaylistId is { } playlistId)
        {
            game["PlaylistId"] = playlistId;
        }

        return WithGuid(scenario, new JsonObject { ["Players"] = players, ["Game"] = game });
    }

    private static string Envelope(Scenario scenario, string eventName, JsonObject data)
    {
        var envelope = new JsonObject { ["Event"] = eventName };
        envelope["Data"] = scenario.DataAsString ? JsonValue.Create(data.ToJsonString()) : data;
        return envelope.ToJsonString(new JsonSerializerOptions { WriteIndented = false });
    }

    private sealed class RosterEntry(string name, string primaryId, int team, int visibleFromMs, int shortcut)
    {
        public string Name { get; } = name;

        public string PrimaryId { get; } = primaryId;

        public int Team { get; set; } = team;

        public int VisibleFromMs { get; } = visibleFromMs;

        public int Shortcut { get; } = shortcut;
    }
}

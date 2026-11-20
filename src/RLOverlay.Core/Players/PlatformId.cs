using System.Text.Json.Serialization;

namespace RLOverlay.Core.Players;

public enum Platform
{
    Unknown,
    Steam,
    Epic,
    PlayStation,
    Xbox,
    Switch,
}

/// <summary>
/// A player's platform identity, parsed from the Stats API <c>PrimaryId</c>
/// (official format <c>Platform|Uid|Splitscreen</c>, e.g. <c>Steam|76561198000000000|0</c>).
/// The display name is carried along because most tracker lookups (Epic, PSN, Xbox, Switch) are by name.
/// </summary>
public sealed record PlatformId(Platform Platform, string Uid, int Splitscreen, string DisplayName, string RawPrimaryId)
{
    /// <summary>Bots and players without a usable id.</summary>
    [JsonIgnore]
    public bool IsBot => string.IsNullOrWhiteSpace(RawPrimaryId) || Uid is "" or "0";

    /// <summary>Split-screen guests share the host's account, so their stats cannot be looked up.</summary>
    [JsonIgnore]
    public bool IsSplitscreenGuest => Splitscreen > 0;

    /// <summary>True when a stats lookup and a profile link make sense for this player.</summary>
    [JsonIgnore]
    public bool CanLookup => !IsBot && !IsSplitscreenGuest && Platform != Platform.Unknown;

    /// <summary>Stable key for caches: normalised platform, uid and split-screen index.</summary>
    [JsonIgnore]
    public string CacheKey => $"{Platform}|{Uid}|{Splitscreen}".ToLowerInvariant();

    /// <summary>Short label shown next to the name (STEAM, EPIC, PSN, XBL, SWITCH).</summary>
    [JsonIgnore]
    public string BadgeText => Platform switch
    {
        Platform.Steam => "STEAM",
        Platform.Epic => "EPIC",
        Platform.PlayStation => "PSN",
        Platform.Xbox => "XBL",
        Platform.Switch => "SWITCH",
        _ => IsBot ? "BOT" : "",
    };

    public static PlatformId Parse(string? primaryId, string? displayName)
    {
        var raw = primaryId?.Trim() ?? "";
        var name = displayName ?? "";
        if (raw.Length == 0)
        {
            return new PlatformId(Platform.Unknown, "", 0, name, "");
        }

        var parts = raw.Split('|');
        var platform = ParsePlatform(parts[0]);
        var uid = parts.Length > 1 ? parts[1].Trim() : "";
        var split = 0;
        if (parts.Length > 2 && int.TryParse(parts[2], out var s))
        {
            split = s;
        }

        return new PlatformId(platform, uid, split, name, raw);
    }

    /// <summary>
    /// Only "Steam" and "Epic" appear in the official docs. The other spellings are best guesses and are matched
    /// case-insensitively.
    /// </summary>
    public static Platform ParsePlatform(string? value)
    {
        var v = (value ?? "").Trim().ToLowerInvariant();
        return v switch
        {
            "steam" => Platform.Steam,
            "epic" or "epicgames" => Platform.Epic,
            "ps4" or "ps5" or "psn" or "playstation" or "sony" => Platform.PlayStation,
            "xboxone" or "xbox" or "xbl" or "xboxseries" or "dingo" or "live" => Platform.Xbox,
            "switch" or "nintendo" or "nnx" => Platform.Switch,
            _ => Platform.Unknown,
        };
    }

    public override string ToString() => RawPrimaryId.Length > 0 ? RawPrimaryId : $"(no id) {DisplayName}";
}

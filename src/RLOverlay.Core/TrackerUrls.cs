using RLOverlay.Core.Players;

namespace RLOverlay.Core;

/// <summary>
/// Every tracker.gg / Tracker Network URL format in one place, so it is easy to fix if they change.
/// </summary>
public static class TrackerUrls
{
    public const string ProfileBase = "https://rocketleague.tracker.network/rocket-league/profile";

    /// <summary>Tracker Network's official API (see docs/STATS_API_NOTES.md: Rocket League is very likely not offered).</summary>
    public const string ApiProfileBase = "https://public-api.tracker.gg/v2/rocket-league/standard/profile";

    /// <summary>steam | epic | psn | xbl | switch.</summary>
    public static string? PlatformSlug(Platform platform) => platform switch
    {
        Platform.Steam => "steam",
        Platform.Epic => "epic",
        Platform.PlayStation => "psn",
        Platform.Xbox => "xbl",
        Platform.Switch => "switch",
        _ => null,
    };

    /// <summary>
    /// The identifier tracker.gg uses in URLs: the SteamID64 for Steam, the display name for every other platform
    /// (the Stats API's Epic/PSN/Xbox uids are account ids tracker.gg does not resolve). Falls back to the uid when
    /// there is no name.
    /// </summary>
    public static string? Identifier(PlatformId id)
    {
        var value = id.Platform == Platform.Steam || string.IsNullOrWhiteSpace(id.DisplayName) ? id.Uid : id.DisplayName.Trim();
        return string.IsNullOrWhiteSpace(value) ? null : value;
    }

    /// <summary>
    /// e.g. <c>https://rocketleague.tracker.network/rocket-league/profile/steam/76561198012345678/overview</c>.
    /// Null for bots, split-screen guests and unknown platforms.
    /// </summary>
    public static string? Profile(PlatformId id)
    {
        if (!id.CanLookup || PlatformSlug(id.Platform) is not { } slug || Identifier(id) is not { } identifier)
        {
            return null;
        }

        return $"{ProfileBase}/{slug}/{Uri.EscapeDataString(identifier)}/overview";
    }

    /// <summary>Tracker Network's CDN folder with the official in-game rank icons (the images tracker.gg shows).</summary>
    public const string RankIconBase = "https://trackercdn.com/cdn/tracker.gg/rocket-league/ranks/";

    /// <summary>
    /// The official icon for a rank tier (0 = Unranked … 22 = Supersonic Legend). Bronze to Champion III use the
    /// long-standing <c>s4-N.png</c> set; Grand Champion I–III and Supersonic Legend use the Season 15 redesign
    /// (<c>s15rankN.png</c>, when Grand Champion got three divisions). Checked against the CDN in November 2026.
    /// </summary>
    public static string RankIcon(int tier)
    {
        tier = RankTier.Clamp(tier);
        return tier >= 19 ? $"{RankIconBase}s15rank{tier}.png" : $"{RankIconBase}s4-{tier}.png";
    }

    /// <summary>e.g. <c>https://public-api.tracker.gg/v2/rocket-league/standard/profile/epic/Zephyr</c>.</summary>
    public static string? ApiProfile(PlatformId id)
    {
        if (!id.CanLookup || PlatformSlug(id.Platform) is not { } slug || Identifier(id) is not { } identifier)
        {
            return null;
        }

        return $"{ApiProfileBase}/{slug}/{Uri.EscapeDataString(identifier)}";
    }
}

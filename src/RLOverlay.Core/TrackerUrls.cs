using RLOverlay.Core.Players;

namespace RLOverlay.Core;

/// <summary>
/// Every tracker.gg / Tracker Network URL format in one place, so it is easy to fix if they change.
/// </summary>
public static class TrackerUrls
{
    public const string ProfileBase = "https://rocketleague.tracker.network/rocket-league/profile";

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
}

using RLOverlay.Core.Players;

namespace RLOverlay.Core;

/// <summary>Why a pasted tracker.gg link can't be used (see <see cref="TrackerUrls.ParseProfile"/>).</summary>
public enum ProfileLinkProblem
{
    None,
    Empty,
    NotAProfileLink,

    /// <summary>A Steam profile link without the 17-digit SteamID64 (a custom name instead).</summary>
    SteamNeedsId,
}

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

    /// <summary>
    /// A Rocket League profile link copied from tracker.gg (rocketleague.tracker.network or tracker.gg, any page of the
    /// profile, with or without https://): its platform and the identifier tracker.gg uses, the SteamID64 for Steam and
    /// the name for every other platform. The reverse of <see cref="Profile"/>.
    /// </summary>
    public static ProfileLinkProblem ParseProfile(string? text, out Platform platform, out string identifier)
    {
        platform = Platform.Unknown;
        identifier = "";
        var value = (text ?? "").Trim().Trim('"', '\'', '<', '>').Trim();
        if (value.Length == 0)
        {
            return ProfileLinkProblem.Empty;
        }

        if (!value.Contains("://", StringComparison.Ordinal))
        {
            value = "https://" + value;
        }

        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https") || !IsTrackerHost(uri.Host))
        {
            return ProfileLinkProblem.NotAProfileLink;
        }

        // rocket-league / profile / {steam|epic|psn|xbl|switch} / {id} / [overview, mmr, matches, …]
        var segments = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length < 4
            || !segments[0].Equals("rocket-league", StringComparison.OrdinalIgnoreCase)
            || !segments[1].Equals("profile", StringComparison.OrdinalIgnoreCase))
        {
            return ProfileLinkProblem.NotAProfileLink;
        }

        var parsedPlatform = PlatformId.ParsePlatform(segments[2]);
        var id = Uri.UnescapeDataString(segments[3]).Trim();
        if (parsedPlatform == Platform.Unknown || id.Length == 0)
        {
            return ProfileLinkProblem.NotAProfileLink;
        }

        if (parsedPlatform == Platform.Steam && !(id.Length == 17 && id.All(char.IsAsciiDigit)))
        {
            return ProfileLinkProblem.SteamNeedsId;
        }

        platform = parsedPlatform;
        identifier = id;
        return ProfileLinkProblem.None;
    }

    private static bool IsTrackerHost(string host) =>
        host.Equals("rocketleague.tracker.network", StringComparison.OrdinalIgnoreCase)
        || host.Equals("tracker.gg", StringComparison.OrdinalIgnoreCase)
        || host.Equals("www.tracker.gg", StringComparison.OrdinalIgnoreCase);
}

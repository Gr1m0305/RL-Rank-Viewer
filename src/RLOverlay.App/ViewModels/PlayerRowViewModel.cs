using System.Globalization;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using RLOverlay.App.Services;
using RLOverlay.Core;
using RLOverlay.Core.Lobby;
using RLOverlay.Core.Players;
using RLOverlay.Core.Playlists;

namespace RLOverlay.App.ViewModels;

public enum RowState
{
    /// <summary>Empty seat: the team is not full yet ("Joining…").</summary>
    Joining,

    /// <summary>Player known, stats lookup in flight ("Fetching rank…").</summary>
    Loading,

    Loaded,

    /// <summary>Lookup finished without data (provider error, unknown player, no API access).</summary>
    NoData,

    /// <summary>Bot or split-screen guest: nothing to look up.</summary>
    NoLookup,
}

public enum StreakKind
{
    None,
    Win,
    Loss,
}

/// <summary>One row of the panel: a player (or an empty seat) and the stats for the selected playlist.</summary>
public sealed partial class PlayerRowViewModel : ObservableObject
{
    public const string Dash = "–";

    [ObservableProperty]
    private string _name = "";

    [ObservableProperty]
    private string? _badge;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanOpenProfile))]
    private string? _profileUrl;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsSkeleton), nameof(IsPending))]
    private RowState _state;

    [ObservableProperty]
    private string? _statusText;

    [ObservableProperty]
    private ImageSource? _rankIcon;

    [ObservableProperty]
    private int _tier;

    [ObservableProperty]
    private string _mmr = Dash;

    [ObservableProperty]
    private string _wins = Dash;

    [ObservableProperty]
    private string _streak = Dash;

    [ObservableProperty]
    private StreakKind _streakKind;

    [ObservableProperty]
    private string _peak = Dash;

    [ObservableProperty]
    private string _goals = Dash;

    [ObservableProperty]
    private string _shots = Dash;

    [ObservableProperty]
    private string _assists = Dash;

    [ObservableProperty]
    private string _saves = Dash;

    [ObservableProperty]
    private string _mvps = Dash;

    private PlayerRowViewModel(string key, int teamNum)
    {
        Key = key;
        TeamNum = teamNum;
    }

    public string Key { get; }

    public int TeamNum { get; private set; }

    public LobbyPlayer? Player { get; private set; }

    public PlayerProfile? Profile { get; private set; }

    public bool IsPlaceholder => Player is null;

    public bool CanOpenProfile => ProfileUrl is not null;

    /// <summary>Stat cells show shimmering bars instead of numbers.</summary>
    public bool IsSkeleton => State is RowState.Joining or RowState.Loading;

    /// <summary>Counts as "not loaded yet" for the footer.</summary>
    public bool IsPending => IsSkeleton;

    /// <summary>The provider's rank icon URL for the displayed tier (offered to <see cref="RankIconService"/>).</summary>
    public string? RankIconUrl { get; private set; }

    public static PlayerRowViewModel ForPlayer(LobbyPlayer player)
    {
        var row = new PlayerRowViewModel(player.Key, player.TeamNum);
        row.UpdatePlayer(player);
        row.State = player.Id.CanLookup ? RowState.Loading : RowState.NoLookup;
        row.StatusText = player.Id.CanLookup ? "Fetching rank…" : null;
        return row;
    }

    public static PlayerRowViewModel Seat(int teamNum, int index) =>
        new($"seat:{teamNum}:{index}", teamNum) { State = RowState.Joining, StatusText = "Joining…" };

    public void UpdatePlayer(LobbyPlayer player)
    {
        Player = player;
        TeamNum = player.TeamNum;
        Name = player.Name;
        Badge = player.Id.IsSplitscreenGuest ? "GUEST" : player.Id.BadgeText;
        ProfileUrl = TrackerUrls.Profile(player.Id);
    }

    /// <summary>Lookup finished (profile null = no data).</summary>
    public void SetProfile(PlayerProfile? profile)
    {
        Profile = profile;
        State = profile is null ? RowState.NoData : RowState.Loaded;
        StatusText = profile is null ? "No data" : null;
    }

    /// <summary>Fills the cells for <paramref name="playlist"/>. Empty values show "–".</summary>
    public void Render(Playlist playlist, RankIconService icons)
    {
        if (State is RowState.Joining or RowState.Loading)
        {
            Tier = RankTier.Unranked;
            RankIcon = null;
            ClearNumbers();
            return;
        }

        var stats = Profile?.For(playlist);
        var lifetime = Profile?.Lifetime;
        var ranked = PlaylistCatalog.Get(playlist).IsRanked;

        // Casual has no rank: always the unranked icon. A ranked playlist the player never placed in is unranked too.
        Tier = ranked && stats?.Tier is { } tier ? RankTier.Clamp(tier) : RankTier.Unranked;
        RankIconUrl = ranked ? stats?.RankIconUrl : null;
        icons.OfferProviderIcon(Tier, RankIconUrl);
        RankIcon = icons.Get(Tier);

        Mmr = Format(stats?.Mmr);
        Peak = Format(stats?.PeakMmr);

        // tracker.gg reports wins/goals/shots/assists/saves/MVPs per account; use the playlist's own value when a
        // provider has one, otherwise the account total (see PROGRESS.md, "Stats fallback").
        Wins = Format(stats?.Wins ?? lifetime?.Wins);
        Goals = Format(stats?.Goals ?? lifetime?.Goals);
        Shots = Format(stats?.Shots ?? lifetime?.Shots);
        Assists = Format(stats?.Assists ?? lifetime?.Assists);
        Saves = Format(stats?.Saves ?? lifetime?.Saves);
        Mvps = Format(stats?.Mvps ?? lifetime?.Mvps);

        (Streak, StreakKind) = stats?.WinStreak switch
        {
            > 0 and var w => ($"W{w}", StreakKind.Win),
            < 0 and var l => ($"L{-l}", StreakKind.Loss),
            _ => (Dash, StreakKind.None),
        };
    }

    /// <summary>Re-reads the icon after <see cref="RankIconService.IconUpdated"/>.</summary>
    public void RefreshIcon(RankIconService icons)
    {
        if (RankIcon is not null)
        {
            RankIcon = icons.Get(Tier);
        }
    }

    private void ClearNumbers()
    {
        Mmr = Wins = Streak = Peak = Goals = Shots = Assists = Saves = Mvps = Dash;
        StreakKind = StreakKind.None;
    }

    private static string Format(int? value) => value?.ToString(CultureInfo.InvariantCulture) ?? Dash;
}

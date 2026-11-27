using CommunityToolkit.Mvvm.ComponentModel;
using RLOverlay.Core;
using RLOverlay.Core.Config;
using RLOverlay.Core.Lobby;
using RLOverlay.Core.Players;
using RLOverlay.Core.Playlists;

namespace RLOverlay.App.ViewModels;

/// <summary>
/// What an overlay screen shows: the lobby (in join order) or, for the favourites screen (<see cref="IsFavourites"/>),
/// one page of the saved players; the selected playlist, the stats picked in the settings, and the header and footer
/// text. The <see cref="Services.OverlayCoordinator"/> feeds it lobby changes; the <see cref="Views.TrackerPanelView"/>
/// turns each player into a row with their tracker.gg page.
/// </summary>
public sealed partial class PanelViewModel : ObservableObject
{
    private readonly List<LobbyPlayer> _players = [];
    private readonly bool _isFavouritesScreen;
    private IReadOnlyList<FavouritePlayer> _favourites = [];

    [ObservableProperty]
    private Playlist _playlist = Playlist.Doubles;

    [ObservableProperty]
    private string _title = "";

    [ObservableProperty]
    private string _subtitle = "";

    [ObservableProperty]
    private string _footerLeft = "";

    [ObservableProperty]
    private string _footerRight = "";

    [ObservableProperty]
    private bool _isWaiting;

    [ObservableProperty]
    private StatColumns _columns = new();

    [ObservableProperty]
    private IReadOnlyList<string> _cycleKeys = ["Ctrl", "G"];

    [ObservableProperty]
    private IReadOnlyList<string> _toggleKeys = ["Ctrl", "R"];

    [ObservableProperty]
    private IReadOnlyList<string> _favouritesKeys = ["Ctrl", "F"];

    /// <summary>Favourites screen: the page shown, from 0.</summary>
    [ObservableProperty]
    private int _page;

    /// <summary>Favourites screen: players per page (5, 10 or 20).</summary>
    [ObservableProperty]
    private int _perPage = FavouritePages.DefaultSize;

    /// <summary>Favourites screen: "Page 2 of 3".</summary>
    [ObservableProperty]
    private string _pageText = "";

    /// <summary>Favourites screen: "6–10 of 12", next to the list's heading.</summary>
    [ObservableProperty]
    private string _countText = "";

    [ObservableProperty]
    private bool _canGoBack;

    [ObservableProperty]
    private bool _canGoForward;

    [ObservableProperty]
    private bool _hasFavourites;

    [ObservableProperty]
    private bool _showSwitchedNote;

    /// <summary>
    /// The settings Preview (or <c>--demo</c>): the players are made up, so rows show where tracker.gg's rows go
    /// instead of loading pages.
    /// </summary>
    [ObservableProperty]
    private bool _isSample;

    public PanelViewModel() => Refresh();

    /// <summary>The favourites screen (Ctrl+F) instead of the lobby. Set once, when it is made.</summary>
    public bool IsFavourites
    {
        get => _isFavouritesScreen;
        init
        {
            _isFavouritesScreen = value;
            Refresh();
        }
    }

    /// <summary>Favourites screen: where the saved players come from (read again on every page change).</summary>
    public Func<IReadOnlyList<FavouritePlayer>>? FavouriteSource { get; init; }

    /// <summary>Favourites screen: how many players are saved.</summary>
    public int FavouriteCount => _favourites.Count;

    /// <summary>Favourites screen: the user picked another page size on it (the app saves it).</summary>
    public event Action<int>? PerPageChosen;

    // Favourites screen: what a pasted link did, in the footer until the page changes or the app clears it.
    private string? _notice;

    /// <summary>The label next to the favourites shortcut in the header.</summary>
    public string FavouritesHint => IsFavourites ? "Close" : "Favourites";

    /// <summary>The label next to the show / hide shortcut: on the favourites screen it goes to the lobby.</summary>
    public string ToggleHint => IsFavourites ? "Lobby" : "Hide";

    public PlaylistStripViewModel Strip { get; } = new();

    public MatchPhase Phase { get; set; } = MatchPhase.Idle;

    /// <summary>Players per team for the match being played (from the game's playlist id), if known.</summary>
    public int? MatchTeamSize { get; set; }

    public IReadOnlyList<Playlist> EnabledPlaylists { get; private set; } = Enum.GetValues<Playlist>();

    /// <summary>The lobby stays open after kickoff (the footer says how to hide it instead of "Hides at kickoff").</summary>
    public bool KeepOpenAtKickoff { get; private set; }

    /// <summary>tracker.gg's label for the selected playlist's row ("Ranked Doubles 2v2").</summary>
    public string RowLabel => TrackerPage.RowLabel(Playlist);

    /// <summary>What to crop next to each rank, from <see cref="Columns"/>.</summary>
    public TrackerCropOptions CropOptions { get; private set; } = TrackerCropOptions.From(new StatColumns());

    /// <summary>The players the screen shows (updated by <see cref="Refresh"/>, so it holds still while frozen).</summary>
    public IReadOnlyList<LobbyPlayer> ShownPlayers { get; private set; } = [];

    /// <summary>Goes up with every new match, so the screen can reload pages that are older than the last match.</summary>
    public int MatchNumber { get; private set; }

    /// <summary>Raised by <see cref="Refresh"/> after <see cref="ShownPlayers"/> was recomputed.</summary>
    public event Action? RosterRefreshed;

    /// <summary>
    /// The match the shown players were in has finished (or was left after kickoff), so their ratings may have moved:
    /// pages loaded before it are loaded again when they are shown for another match.
    /// </summary>
    public event Action? MatchFinished;

    /// <summary>See <see cref="MatchFinished"/>.</summary>
    public void FinishMatch() => MatchFinished?.Invoke();

    public void ApplySettings(OverlaySettings settings)
    {
        Columns = new StatColumns
        {
            Wins = settings.Columns.Wins,
            Streak = settings.Columns.Streak,
            Peak = settings.Columns.Peak,
            AllTimePeak = settings.Columns.AllTimePeak,
            Goals = settings.Columns.Goals,
            Shots = settings.Columns.Shots,
            Assists = settings.Columns.Assists,
            Saves = settings.Columns.Saves,
            Mvps = settings.Columns.Mvps,
        };
        CycleKeys = HotkeyGesture.TryParse(settings.CycleHotkey, out var cycle) ? cycle.DisplayParts : ["Ctrl", "G"];
        ToggleKeys = HotkeyGesture.TryParse(settings.ToggleHotkey, out var toggle) ? toggle.DisplayParts : ["Ctrl", "R"];
        FavouritesKeys = HotkeyGesture.TryParse(settings.FavouritesHotkey, out var favourites) ? favourites.DisplayParts : ["Ctrl", "F"];
        var perPage = FavouritePages.NormalizeSize(settings.FavouritesPerPage);
        if (perPage != PerPage)
        {
            Page = FavouritePages.Resized(Page, PerPage, perPage);
            PerPage = perPage;
        }

        EnabledPlaylists = settings.EnabledPlaylists.ToList();
        KeepOpenAtKickoff = settings.KeepOpenAtKickoff;
        Strip.Update(Playlist, EnabledPlaylists);
        Refresh();
    }

    partial void OnColumnsChanged(StatColumns value) => CropOptions = TrackerCropOptions.From(value);

    /// <summary>A new match: forget the old roster.</summary>
    public void NewMatch()
    {
        MatchNumber++;
        Clear();
    }

    public void Clear()
    {
        _players.Clear();
        MatchTeamSize = null;
        ShowSwitchedNote = false;
        Refresh();
    }

    public void AddOrUpdate(LobbyPlayer player)
    {
        var index = _players.FindIndex(p => p.Key == player.Key);
        if (index < 0)
        {
            _players.Add(player);
        }
        else
        {
            _players[index] = player;
        }

        Refresh();
    }

    public void Remove(string key)
    {
        if (_players.RemoveAll(p => p.Key == key) > 0)
        {
            Refresh();
        }
    }

    partial void OnPlaylistChanged(Playlist value)
    {
        OnPropertyChanged(nameof(RowLabel));
        Strip.Update(value, EnabledPlaylists);
        Refresh();
    }

    partial void OnShowSwitchedNoteChanged(bool value) => Refresh();

    /// <summary>Favourites screen: opens on the first page with the list as it is now.</summary>
    public void ShowFavourites()
    {
        Page = 0;
        _notice = null;
        ReloadFavourites();
    }

    /// <summary>Favourites screen closed: no rows, so their pages are closed.</summary>
    public void ClearFavourites()
    {
        _favourites = [];
        Page = 0;
        _notice = null;
        Refresh();
    }

    public void NextPage()
    {
        Page++;
        _notice = null;
        ReloadFavourites();
    }

    public void PreviousPage()
    {
        Page--;
        _notice = null;
        ReloadFavourites();
    }

    /// <summary>
    /// Favourites screen: what a pasted link did, shown in the footer. After an add the first page shows, where the new
    /// player is (newest first).
    /// </summary>
    public void ShowNotice(string text, bool added)
    {
        _notice = text;
        if (added)
        {
            Page = 0;
        }

        ReloadFavourites();
    }

    /// <summary>The footer's usual text again.</summary>
    public void ClearNotice()
    {
        if (_notice is not null)
        {
            _notice = null;
            Refresh();
        }
    }

    /// <summary>
    /// Another page size, picked on the favourites screen: the page that holds the same first player is shown, and the
    /// choice is passed on to be saved.
    /// </summary>
    public void ChoosePerPage(int perPage)
    {
        perPage = FavouritePages.NormalizeSize(perPage);
        if (perPage == PerPage)
        {
            return;
        }

        Page = FavouritePages.Resized(Page, PerPage, perPage);
        PerPage = perPage;
        _notice = null;
        ReloadFavourites();
        PerPageChosen?.Invoke(perPage);
    }

    /// <summary>
    /// Reads the saved players again. Stars clicked off on this page stay listed until the page changes, so a slip of
    /// the mouse can be undone with another click.
    /// </summary>
    private void ReloadFavourites()
    {
        _favourites = FavouriteSource?.Invoke().ToList() ?? [];
        Refresh();
    }

    partial void OnIsSampleChanged(bool value) => Refresh();

    /// <summary>
    /// While true, <see cref="Refresh"/> leaves the screen as it is. Set while it fades out, so rows and text don't
    /// change mid-fade (e.g. the footer flipping at kickoff); cleared before it shows.
    /// </summary>
    public bool IsFrozen { get; set; }

    /// <summary>Recomputes the roster, header and footer text.</summary>
    public void Refresh()
    {
        if (IsFrozen)
        {
            return;
        }

        if (IsFavourites)
        {
            RefreshFavourites();
            return;
        }

        var info = PlaylistCatalog.Get(Playlist);
        Title = info.Title;
        Subtitle = info.Subtitle + (ShowSwitchedNote ? $" · switched with {string.Join("+", CycleKeys)}" : " · tracker.gg");

        var blue = _players.Count(p => p.TeamNum == 0 && !p.HasLeft);
        var orange = _players.Count(p => p.TeamNum == 1 && !p.HasLeft);
        var teamSize = MatchTeamSize ?? Math.Max(1, Math.Max(blue, orange));
        var expected = Phase == MatchPhase.PreKickoff ? teamSize * 2 : 0;
        IsWaiting = Phase != MatchPhase.Idle && blue + orange < expected;
        var hide = $"{string.Join("+", ToggleKeys)} to hide";

        if (Phase == MatchPhase.Idle && _players.Count == 0)
        {
            FooterLeft = "Not in a match. Every player's tracker.gg row appears here when you load into one.";
            FooterRight = hide;
        }
        else if (IsSample)
        {
            FooterLeft = "Preview with made-up players · in a match each row is that player's tracker.gg page";
            FooterRight = hide;
        }
        else if (IsWaiting)
        {
            FooterLeft = $"Waiting for players · {blue + orange} of {expected} in";
            FooterRight = "Updates live";
        }
        else
        {
            FooterLeft = Phase == MatchPhase.Ended
                ? "Match over · click a name for the full tracker.gg profile"
                : "Rows from tracker.gg · click a name for the full profile";
            FooterRight = Phase == MatchPhase.PreKickoff && !KeepOpenAtKickoff ? "Hides at kickoff" : hide;
        }

        ShownPlayers = _players.ToList();
        RosterRefreshed?.Invoke();
    }

    /// <summary>The favourites screen: header, one page of players, the page buttons' text and state.</summary>
    private void RefreshFavourites()
    {
        Title = "Favourites";
        Subtitle = $"{RowLabel} · tracker.gg";
        IsWaiting = false;
        var total = _favourites.Count;
        var pages = FavouritePages.Count(total, PerPage);
        Page = FavouritePages.Clamp(Page, total, PerPage);
        var shown = FavouritePages.Slice(_favourites, Page, PerPage);
        var first = Page * PerPage;
        HasFavourites = total > 0;
        PageText = $"Page {Page + 1} of {pages}";
        CountText = total == 0 ? "" : $"{first + 1}–{first + shown.Count} of {total}";
        CanGoBack = Page > 0;
        CanGoForward = Page < pages - 1;
        FooterLeft = _notice ?? (total == 0
            ? "No favourites yet · click the star next to a name in the lobby, or copy a tracker.gg profile link and click Paste link"
            : "Newest first · click a name for the full profile");
        FooterRight = "";
        ShownPlayers = shown
            .Select(f => f.ToPlatformId())
            .Select(id => new LobbyPlayer(id.CacheKey, id.DisplayName, id, -1))
            .ToList();
        RosterRefreshed?.Invoke();
    }

    /// <summary>Expected players per team for the team headers ("1 / 2").</summary>
    public int TeamSize(int blueCount, int orangeCount) => MatchTeamSize ?? Math.Max(1, Math.Max(blueCount, orangeCount));
}

using CommunityToolkit.Mvvm.ComponentModel;
using RLOverlay.App.Services;
using RLOverlay.Core.Config;
using RLOverlay.Core.Lobby;
using RLOverlay.Core.Players;
using RLOverlay.Core.Playlists;

namespace RLOverlay.App.ViewModels;

/// <summary>
/// Everything the panel shows. The <see cref="Services.OverlayCoordinator"/> feeds it lobby changes and profiles; it
/// keeps rows in join order, adds "Joining…" seats while the lobby fills, and re-renders every row when the playlist
/// changes.
/// </summary>
public sealed partial class PanelViewModel : ObservableObject
{
    private readonly RankIconService _icons;
    private readonly List<PlayerRowViewModel> _players = [];
    private readonly Dictionary<int, List<PlayerRowViewModel>> _seats = new() { [0] = [], [1] = [] };

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
    private bool _showSwitchedNote;

    public PanelViewModel(RankIconService icons)
    {
        _icons = icons;
        Refresh();
    }

    public TeamViewModel Blue { get; } = new(0);

    public TeamViewModel Orange { get; } = new(1);

    public PlaylistStripViewModel Strip { get; } = new();

    public MatchPhase Phase { get; set; } = MatchPhase.Idle;

    /// <summary>Players per team for the match being played (from the game's playlist id), if known.</summary>
    public int? MatchTeamSize { get; set; }

    public IReadOnlyList<Playlist> EnabledPlaylists { get; private set; } = Enum.GetValues<Playlist>();

    public IReadOnlyList<PlayerRowViewModel> Players => _players;

    public void ApplySettings(OverlaySettings settings)
    {
        Columns = new StatColumns
        {
            Wins = settings.Columns.Wins,
            Streak = settings.Columns.Streak,
            Peak = settings.Columns.Peak,
            Goals = settings.Columns.Goals,
            Shots = settings.Columns.Shots,
            Assists = settings.Columns.Assists,
            Saves = settings.Columns.Saves,
            Mvps = settings.Columns.Mvps,
        };
        CycleKeys = HotkeyGesture.TryParse(settings.CycleHotkey, out var cycle) ? cycle.DisplayParts : ["Ctrl", "G"];
        ToggleKeys = HotkeyGesture.TryParse(settings.ToggleHotkey, out var toggle) ? toggle.DisplayParts : ["Ctrl", "R"];
        EnabledPlaylists = settings.EnabledPlaylists.ToList();
        Strip.Update(Playlist, EnabledPlaylists);
        Refresh();
    }

    public void Clear()
    {
        _players.Clear();
        MatchTeamSize = null;
        ShowSwitchedNote = false;
        Refresh();
    }

    public PlayerRowViewModel? Find(string key) => _players.Find(p => p.Key == key);

    public PlayerRowViewModel AddOrUpdate(LobbyPlayer player)
    {
        var row = Find(player.Key);
        if (row is null)
        {
            row = PlayerRowViewModel.ForPlayer(player);
            _players.Add(row);
            row.Render(Playlist, _icons);
        }
        else
        {
            row.UpdatePlayer(player);
        }

        Refresh();
        return row;
    }

    public void Remove(string key)
    {
        if (_players.RemoveAll(p => p.Key == key) > 0)
        {
            Refresh();
        }
    }

    public void SetProfile(string key, PlayerProfile? profile)
    {
        if (Find(key) is not { } row)
        {
            return;
        }

        row.SetProfile(profile);
        row.Render(Playlist, _icons);
        Refresh();
    }

    public void OnIconUpdated(int tier)
    {
        foreach (var row in _players.Where(r => r.Tier == tier))
        {
            row.RefreshIcon(_icons);
        }
    }

    partial void OnPlaylistChanged(Playlist value)
    {
        foreach (var row in _players)
        {
            row.Render(value, _icons);
        }

        Strip.Update(value, EnabledPlaylists);
        Refresh();
    }

    partial void OnShowSwitchedNoteChanged(bool value) => Refresh();

    /// <summary>
    /// While true, <see cref="Refresh"/> leaves the panel as it is. Set while the panel fades out, so the text and
    /// rows don't change mid-fade (e.g. the footer flipping to "Ctrl+R to hide" at kickoff); cleared before it shows.
    /// </summary>
    public bool IsFrozen { get; set; }

    /// <summary>Recomputes team rows, seats, counts, header and footer text.</summary>
    public void Refresh()
    {
        if (IsFrozen)
        {
            return;
        }

        var info = PlaylistCatalog.Get(Playlist);
        Title = info.Title;
        var season = _players.Select(p => p.Profile?.Season).Max();
        Subtitle = info.Subtitle + (ShowSwitchedNote
            ? $" · switched with {string.Join("+", CycleKeys)}"
            : season is { } s ? $" · Season {s}" : "");

        var blue = _players.Where(p => p.TeamNum == 0).ToList();
        var orange = _players.Where(p => p.TeamNum == 1).ToList();
        var teamSize = MatchTeamSize ?? Math.Max(1, Math.Max(blue.Count, orange.Count));
        var showSeats = Phase == MatchPhase.PreKickoff;

        Layout(Blue, blue, showSeats ? teamSize - blue.Count : 0);
        Layout(Orange, orange, showSeats ? teamSize - orange.Count : 0);
        Blue.CountText = Phase == MatchPhase.Idle ? "" : $"{blue.Count} / {Math.Max(teamSize, blue.Count)}";
        Orange.CountText = Phase == MatchPhase.Idle ? "" : $"{orange.Count} / {Math.Max(teamSize, orange.Count)}";

        var seated = blue.Count + orange.Count;
        var loaded = blue.Concat(orange).Count(p => !p.IsPending);
        var total = Math.Max(seated, showSeats ? teamSize * 2 : 0);
        IsWaiting = Phase != MatchPhase.Idle && loaded < total;

        if (Phase == MatchPhase.Idle)
        {
            FooterLeft = "Not in a match. The panel fills in when you load into one.";
            FooterRight = $"{string.Join("+", ToggleKeys)} to hide";
        }
        else if (IsWaiting)
        {
            FooterLeft = $"{(seated < total ? "Waiting for players" : "Fetching ranks")} · {loaded} of {total} loaded";
            FooterRight = "Updates live";
        }
        else
        {
            FooterLeft = "Click a name to open their tracker profile";
            FooterRight = Phase == MatchPhase.PreKickoff ? "Hides at kickoff" : $"{string.Join("+", ToggleKeys)} to hide";
        }
    }

    private void Layout(TeamViewModel team, List<PlayerRowViewModel> players, int seatCount)
    {
        var seats = _seats[team.TeamNum];
        while (seats.Count < seatCount)
        {
            seats.Add(PlayerRowViewModel.Seat(team.TeamNum, seats.Count));
        }

        var desired = new List<PlayerRowViewModel>(players);
        desired.AddRange(seats.Take(Math.Max(0, seatCount)));
        team.SyncRows(desired);
    }
}

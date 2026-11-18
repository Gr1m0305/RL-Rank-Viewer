using CommunityToolkit.Mvvm.ComponentModel;
using RLOverlay.Core;
using RLOverlay.Core.Config;
using RLOverlay.Core.Lobby;
using RLOverlay.Core.Playlists;

namespace RLOverlay.App.ViewModels;

/// <summary>
/// What the overlay screen shows: the lobby (in join order), the selected playlist, the stats picked in the settings,
/// and the header and footer text. The <see cref="Services.OverlayCoordinator"/> feeds it lobby changes; the
/// <see cref="Views.TrackerPanelView"/> turns each player into a row with their tracker.gg page.
/// </summary>
public sealed partial class PanelViewModel : ObservableObject
{
    private readonly List<LobbyPlayer> _players = [];

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

    /// <summary>
    /// The settings Preview (or <c>--demo</c>): the players are made up, so rows show where tracker.gg's rows go
    /// instead of loading pages.
    /// </summary>
    [ObservableProperty]
    private bool _isSample;

    public PanelViewModel() => Refresh();

    public PlaylistStripViewModel Strip { get; } = new();

    public MatchPhase Phase { get; set; } = MatchPhase.Idle;

    /// <summary>Players per team for the match being played (from the game's playlist id), if known.</summary>
    public int? MatchTeamSize { get; set; }

    public IReadOnlyList<Playlist> EnabledPlaylists { get; private set; } = Enum.GetValues<Playlist>();

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
        EnabledPlaylists = settings.EnabledPlaylists.ToList();
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

        var info = PlaylistCatalog.Get(Playlist);
        Title = info.Title;
        Subtitle = info.Subtitle + (ShowSwitchedNote ? $" · switched with {string.Join("+", CycleKeys)}" : " · tracker.gg");

        var blue = _players.Count(p => p.TeamNum == 0);
        var orange = _players.Count(p => p.TeamNum == 1);
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
            FooterRight = Phase == MatchPhase.PreKickoff ? "Hides at kickoff" : hide;
        }

        ShownPlayers = _players.ToList();
        RosterRefreshed?.Invoke();
    }

    /// <summary>Expected players per team for the team headers ("1 / 2").</summary>
    public int TeamSize(int blueCount, int orangeCount) => MatchTeamSize ?? Math.Max(1, Math.Max(blueCount, orangeCount));
}

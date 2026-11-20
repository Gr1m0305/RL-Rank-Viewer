using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using Microsoft.Extensions.Logging;
using Microsoft.Web.WebView2.Core;
using RLOverlay.App.Overlay;
using RLOverlay.App.Services;
using RLOverlay.App.ViewModels;
using RLOverlay.Core;
using RLOverlay.Core.Lobby;

namespace RLOverlay.App.Views;

/// <summary>
/// The overlay screen: every player in the lobby, by team, with their row from tracker.gg for the selected playlist
/// (Ctrl+G switches it) and the stats picked in the settings next to it. Each row is the player's real tracker.gg page,
/// loaded in an embedded browser when they join and cropped; the app never reads the numbers. The favourites screen
/// (Ctrl+F) is a second one of these, showing a page of saved players instead (see <see cref="PanelViewModel.IsFavourites"/>).
/// </summary>
public partial class TrackerPanelView : UserControl, IRevealable, IDisposable
{
    private static readonly TimeSpan RiseDuration = TimeSpan.FromMilliseconds(320);
    private static readonly TimeSpan Stagger = TimeSpan.FromMilliseconds(55);

    /// <summary>A kept player's page is loaded again at the next match if it is older than this.</summary>
    private static readonly TimeSpan ReloadForNewMatchAfter = TimeSpan.FromMinutes(1);

    private static Task<CoreWebView2Environment>? s_environment;

    private readonly Dictionary<string, TrackerRowView> _rows = new();
    private readonly Dictionary<string, bool> _sampleRows = new();
    private readonly TeamBlock _blue;
    private readonly TeamBlock _orange;
    private readonly TeamBlock _other;
    private readonly TeamBlock _favourites;
    private readonly Action<string> _openProfile;
    private readonly ILogger _logger;
    private readonly IFavouriteMarks? _marks;
    private PanelViewModel? _vm;
    private double _zoom = 1;
    private int _matchNumber = -1;
    private bool _disposed;

    internal TrackerPanelView(Action<string> openProfile, ILogger logger, IFavouriteMarks? marks = null)
    {
        InitializeComponent();
        _openProfile = openProfile;
        _logger = logger;
        _marks = marks;
        _blue = new TeamBlock(this, "BLUE", "AccentBlue");
        _orange = new TeamBlock(this, "ORANGE", "AccentOrange");
        _other = new TeamBlock(this, "JOINING", "TextMuted");
        _favourites = new TeamBlock(this, "FAVOURITES", "FavouriteGold");
        if (marks is not null)
        {
            marks.Changed += OnFavouritesChanged;
        }

        DataContextChanged += OnDataContextChanged;
    }

    /// <summary>A shortcut hint in the header was pressed: do what that shortcut does.</summary>
    public event Action<OverlayHotkey>? HintClicked;

    /// <summary>The browser profile all rows share (cookies, cache), created on first use.</summary>
    private static Task<CoreWebView2Environment> Environment()
    {
        if (s_environment is null || s_environment.IsFaulted || s_environment.IsCanceled)
        {
            // Pages load out of sight (see TrackerRowView), so Chromium mustn't slow hidden pages down; their priority is
            // set by BrowserTuning instead.
            var options = new CoreWebView2EnvironmentOptions
            {
                AdditionalBrowserArguments = "--disable-background-timer-throttling --disable-renderer-backgrounding --disable-backgrounding-occluded-windows",
            };
            s_environment = CoreWebView2Environment.CreateAsync(null, AppPaths.WebViewDataDir, options);
        }

        return s_environment;
    }

    /// <summary>The shared browser profile, with its processes put below normal priority (see <see cref="BrowserTuning"/>).</summary>
    private async Task<CoreWebView2Environment> TunedEnvironment()
    {
        var environment = await Environment();
        BrowserTuning.LowerPriority(environment, _logger);
        return environment;
    }

    /// <summary>Rows for tests and diagnostics.</summary>
    internal IReadOnlyCollection<TrackerRowView> Rows => _rows.Values;

    /// <summary>The overlay's size setting: pages are drawn at that zoom so they stay sharp.</summary>
    public void SetZoom(double zoom)
    {
        _zoom = zoom;
        foreach (var row in _rows.Values)
        {
            row.SetZoom(zoom);
        }
    }

    /// <summary>Header, teams and footer rise into place one after another.</summary>
    public void PlayReveal()
    {
        var delay = TimeSpan.FromMilliseconds(40);
        var footer = _vm?.IsFavourites == true ? FavouritesFooter : FooterRow;
        foreach (var section in new FrameworkElement[] { HeaderRow, RowsHost, footer })
        {
            Rise(section, delay, fromY: 8, fromOpacity: 0);
            delay += Stagger;
        }
    }

    private void PlayPlaylistSwitch()
    {
        if (IsVisible && SystemParameters.ClientAreaAnimation)
        {
            Rise(RowsHost, TimeSpan.Zero, fromY: 4, fromOpacity: 0.25);
        }
    }

    private static void Rise(FrameworkElement element, TimeSpan delay, double fromY, double fromOpacity)
    {
        if (element.RenderTransform is not TranslateTransform shift)
        {
            shift = new TranslateTransform();
            element.RenderTransform = shift;
        }

        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        element.BeginAnimation(OpacityProperty, Keyframes(fromOpacity, 1, delay, ease));
        shift.BeginAnimation(TranslateTransform.YProperty, Keyframes(fromY, 0, delay, ease));
    }

    private static DoubleAnimationUsingKeyFrames Keyframes(double from, double to, TimeSpan delay, IEasingFunction ease)
    {
        var animation = new DoubleAnimationUsingKeyFrames();
        animation.KeyFrames.Add(new DiscreteDoubleKeyFrame(from, KeyTime.FromTimeSpan(TimeSpan.Zero)));
        animation.KeyFrames.Add(new DiscreteDoubleKeyFrame(from, KeyTime.FromTimeSpan(delay)));
        animation.KeyFrames.Add(new EasingDoubleKeyFrame(to, KeyTime.FromTimeSpan(delay + RiseDuration), ease));
        return animation;
    }

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (_vm is not null)
        {
            _vm.PropertyChanged -= OnViewModelPropertyChanged;
            _vm.RosterRefreshed -= Rebuild;
        }

        _vm = DataContext as PanelViewModel;
        if (_vm is not null)
        {
            _vm.PropertyChanged += OnViewModelPropertyChanged;
            _vm.RosterRefreshed += Rebuild;
            FooterRow.Visibility = _vm.IsFavourites ? Visibility.Collapsed : Visibility.Visible;
            FavouritesFooter.Visibility = _vm.IsFavourites ? Visibility.Visible : Visibility.Collapsed;
            ShowPageSize();
            Rebuild();
        }
    }

    /// <summary>The chosen page size stands out (gold edge, bright number).</summary>
    private void ShowPageSize()
    {
        foreach (var button in new[] { PerPage5, PerPage10 })
        {
            var chosen = _vm is not null && button.Tag is string tag && tag == _vm.PerPage.ToString(System.Globalization.CultureInfo.InvariantCulture);
            button.BorderBrush = (Brush)FindResource(chosen ? "FavouriteGold" : "KeyCapBorder");
            button.Foreground = (Brush)FindResource(chosen ? "TextPrimary" : "TextSecondary");
        }
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (_vm is null)
        {
            return;
        }

        switch (e.PropertyName)
        {
            case nameof(PanelViewModel.Playlist):
                foreach (var row in _rows.Values)
                {
                    row.SetLabel(_vm.RowLabel);
                }

                PlayPlaylistSwitch();
                break;

            case nameof(PanelViewModel.Columns):
                foreach (var row in _rows.Values)
                {
                    row.SetOptions(_vm.CropOptions);
                }

                break;

            case nameof(PanelViewModel.PerPage):
                ShowPageSize();
                break;
        }
    }

    private void OnFavouritesChanged()
    {
        foreach (var row in _rows.Values)
        {
            row.RefreshStar();
        }
    }

    private void OnCycleHintClick(object sender, RoutedEventArgs e) => HintClicked?.Invoke(OverlayHotkey.CyclePlaylist);

    private void OnFavouritesHintClick(object sender, RoutedEventArgs e) => HintClicked?.Invoke(OverlayHotkey.Favourites);

    private void OnToggleHintClick(object sender, RoutedEventArgs e) => HintClicked?.Invoke(OverlayHotkey.TogglePanel);

    private void OnPreviousPageClick(object sender, RoutedEventArgs e) => _vm?.PreviousPage();

    private void OnNextPageClick(object sender, RoutedEventArgs e) => _vm?.NextPage();

    private void OnPerPageClick(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string tag } && int.TryParse(tag, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var perPage))
        {
            _vm?.ChoosePerPage(perPage);
        }
    }

    /// <summary>
    /// The roster changed. Players no longer in it (a new match) are removed and their page closed; players who left the
    /// match stay, marked as left; new players get a row whose page starts loading right away, so it's ready by the time
    /// anyone looks.
    /// </summary>
    private void Rebuild()
    {
        if (_vm is null || _disposed)
        {
            return;
        }

        var players = _vm.ShownPlayers.DistinctBy(p => p.Key).ToList();
        var keep = new HashSet<string>(players.Where(p => _sampleRows.TryGetValue(p.Key, out var sample) && sample == _vm.IsSample).Select(p => p.Key));
        foreach (var gone in _rows.Keys.Where(k => !keep.Contains(k)).ToList())
        {
            var old = _rows[gone];
            _rows.Remove(gone);
            _sampleRows.Remove(gone);
            RowsHost.Children.Remove(old);
            old.Dispose();
        }

        var newMatch = _vm.MatchNumber != _matchNumber;
        _matchNumber = _vm.MatchNumber;
        var started = new List<TrackerRowView>();
        foreach (var player in players)
        {
            if (_rows.TryGetValue(player.Key, out var row))
            {
                row.Update(player);
                if (newMatch && row.LoadedAt != DateTime.MinValue && DateTime.UtcNow - row.LoadedAt > ReloadForNewMatchAfter)
                {
                    row.Reload(); // their rank may have moved since the last match
                }
            }
            else
            {
                row = new TrackerRowView(player, _vm.RowLabel, _vm.CropOptions, _zoom, _vm.IsSample, TunedEnvironment, _openProfile, _logger, _marks);
                row.Changed += OnRowChanged;
                _rows[player.Key] = row;
                _sampleRows[player.Key] = _vm.IsSample;
                RowsHost.Children.Add(row);
                started.Add(row);
            }
        }

        Layout(players);
        OnRowChanged();

        if (started.Count > 0)
        {
            // Start the browsers once the screen has drawn, so it never stalls on them.
            Dispatcher.BeginInvoke(DispatcherPriority.ContextIdle, () =>
            {
                foreach (var row in started)
                {
                    _ = row.StartAsync();
                }
            });
        }
    }

    /// <summary>
    /// Blue, then Orange, then players whose team isn't known yet; a block only for teams with players. The favourites
    /// screen has one block, the page of saved players.
    /// </summary>
    private void Layout(IReadOnlyList<LobbyPlayer> players)
    {
        var heights = new List<GridLength>();
        var favourites = _vm!.IsFavourites;
        var saved = favourites ? players.ToList() : [];
        var blue = favourites ? [] : players.Where(p => p.TeamNum == 0).ToList();
        var orange = favourites ? [] : players.Where(p => p.TeamNum == 1).ToList();
        var other = favourites ? [] : players.Where(p => p.TeamNum is not (0 or 1)).ToList();
        var blueIn = blue.Count(p => !p.HasLeft);
        var orangeIn = orange.Count(p => !p.HasLeft);
        var teamSize = _vm!.TeamSize(blueIn, orangeIn);
        var phaseKnown = _vm.Phase != MatchPhase.Idle;

        void Group(TeamBlock block, List<LobbyPlayer> members, string count)
        {
            block.SetVisible(members.Count > 0);
            if (members.Count == 0)
            {
                return;
            }

            if (heights.Count > 0)
            {
                heights.Add(new GridLength(8)); // gap between teams
            }

            var top = heights.Count;
            heights.Add(GridLength.Auto);
            block.Place(top, members.Count + 1, count);
            for (var i = 0; i < members.Count; i++)
            {
                var row = _rows[members[i].Key];
                Grid.SetRow(row, heights.Count);
                row.Margin = new Thickness(12, 0, 14, i == members.Count - 1 ? 9 : 6);
                heights.Add(GridLength.Auto);
            }
        }

        Group(_blue, blue, phaseKnown ? TeamCount(blueIn, blue.Count - blueIn, teamSize) : "");
        Group(_orange, orange, phaseKnown ? TeamCount(orangeIn, orange.Count - orangeIn, teamSize) : "");
        Group(_other, other, "");
        Group(_favourites, saved, _vm.CountText);

        while (RowsHost.RowDefinitions.Count < heights.Count)
        {
            RowsHost.RowDefinitions.Add(new RowDefinition());
        }

        while (RowsHost.RowDefinitions.Count > heights.Count)
        {
            RowsHost.RowDefinitions.RemoveAt(RowsHost.RowDefinitions.Count - 1);
        }

        for (var i = 0; i < heights.Count; i++)
        {
            RowsHost.RowDefinitions[i].Height = heights[i];
        }

        RowsHost.Margin = heights.Count > 0 ? new Thickness(0) : new Thickness(0, 0, 0, -8);
    }

    /// <summary>"2 / 2", or "2 / 2 · 1 left" when someone quit.</summary>
    private static string TeamCount(int playing, int left, int teamSize) =>
        $"{playing} / {Math.Max(teamSize, playing)}" + (left > 0 ? $" · {left} left" : "");

    private void OnRowChanged()
    {
        AlignColumns();
        AlignTextBands();
        UpdateRefreshButton();
    }

    private void OnRefreshClick(object sender, RoutedEventArgs e) => RefreshAll();

    /// <summary>
    /// The header's Refresh: loads every player's page again, so new ranks show without waiting for the next match.
    /// Rows already loading, bots, split-screen guests and Preview samples are left alone.
    /// </summary>
    /// <returns>How many pages were reloaded.</returns>
    internal int RefreshAll()
    {
        var due = _rows.Values.Where(Refreshable).ToList();
        foreach (var row in due)
        {
            row.Reload();
        }

        _logger.LogInformation("Overlay: refreshing {Count} player page(s)", due.Count);
        UpdateRefreshButton();
        return due.Count;
    }

    private static bool Refreshable(TrackerRowView row) => row.HasProfile && row.State != TrackerRowState.Loading;

    /// <summary>Greyed out while there is nothing to reload (empty lobby, everything still loading, Preview).</summary>
    private void UpdateRefreshButton() => RefreshButton.IsEnabled = _rows.Values.Any(Refreshable);

    /// <summary>
    /// Every player's page lays its table out on its own, so the columns land in different places. Each row reports its
    /// cells and tiles with their widths; all rows then get every column at its widest (a gap where a page lacks one),
    /// so ranks, peaks, streaks and tiles line up.
    /// </summary>
    private void AlignColumns()
    {
        var widths = TrackerPage.AlignWidths(_rows.Values.Select(r => r.NaturalCells));
        foreach (var row in _rows.Values)
        {
            row.SetAlignment(widths);
        }
    }

    /// <summary>Rows that show text (bot, not found, no rating) are as wide as the widest tracker.gg row.</summary>
    private void AlignTextBands()
    {
        var widest = _rows.Values.Select(r => r.CropWidth ?? 0).DefaultIfEmpty(0).Max();
        var width = widest > 0 ? widest : 640;
        foreach (var row in _rows.Values)
        {
            row.SetTextBandWidth(width);
        }
    }

    /// <summary>Closes every page (app exit).</summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        if (_marks is not null)
        {
            _marks.Changed -= OnFavouritesChanged;
        }

        foreach (var row in _rows.Values)
        {
            row.Dispose();
        }

        _rows.Clear();
        _sampleRows.Clear();
    }

    /// <summary>A team's background block (accent edge on the left) and its header with the player count.</summary>
    private sealed class TeamBlock
    {
        private readonly Border _background;
        private readonly Grid _header;
        private readonly TextBlock _count;

        public TeamBlock(TrackerPanelView owner, string label, string accent)
        {
            _background = new Border
            {
                CornerRadius = new CornerRadius(8),
                Background = (Brush)owner.FindResource("TeamBackground"),
                BorderBrush = (Brush)owner.FindResource(accent),
                BorderThickness = new Thickness(3, 0, 0, 0),
                Visibility = Visibility.Collapsed,
            };
            Panel.SetZIndex(_background, -1);
            var name = new TextBlock
            {
                Text = string.Join(" ", label.ToCharArray()),
                FontSize = 11,
                FontWeight = FontWeights.Bold,
                Foreground = (Brush)owner.FindResource(accent),
            };
            _count = new TextBlock
            {
                FontSize = 10.5,
                Foreground = (Brush)owner.FindResource("TextSecondary"),
                HorizontalAlignment = HorizontalAlignment.Right,
            };
            System.Windows.Documents.Typography.SetNumeralAlignment(_count, FontNumeralAlignment.Tabular);
            _header = new Grid { Margin = new Thickness(15, 7, 22, 6), Visibility = Visibility.Collapsed, Children = { name, _count } };
            owner.RowsHost.Children.Add(_background);
            owner.RowsHost.Children.Add(_header);
        }

        public void SetVisible(bool visible)
        {
            _background.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
            _header.Visibility = _background.Visibility;
        }

        public void Place(int row, int span, string count)
        {
            Grid.SetRow(_background, row);
            Grid.SetRowSpan(_background, span);
            Grid.SetRow(_header, row);
            _count.Text = count;
        }
    }
}

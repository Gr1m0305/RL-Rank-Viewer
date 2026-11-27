using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Extensions.Logging;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;
using RLOverlay.App.Overlay;
using RLOverlay.App.Services;
using RLOverlay.Core;
using RLOverlay.Core.Lobby;
using RLOverlay.Core.Players;

namespace RLOverlay.App.Views;

/// <summary>What a player's row on the overlay screen is showing.</summary>
internal enum TrackerRowState
{
    /// <summary>The page is loading (or the row is being found).</summary>
    Loading,

    /// <summary>The playlist row (and the picked stat tiles) are cropped out of the page and shown.</summary>
    Cropped,

    /// <summary>The player has no row for the selected playlist.</summary>
    NoRow,

    /// <summary>tracker.gg says the player doesn't exist.</summary>
    NotFound,

    /// <summary>The row didn't show up in time, so the whole page is shown (for example a browser check).</summary>
    ShowingPage,

    /// <summary>The page could not be loaded.</summary>
    Failed,

    /// <summary>Bots and split-screen guests have no tracker.gg profile.</summary>
    NoProfile,

    /// <summary>A made-up player (settings Preview, demo): a placeholder where the row would be.</summary>
    Sample,
}

/// <summary>
/// One player on the overlay screen: a star (click to save them as a favourite), the name (click for the full profile
/// in the browser) and a band with the player's tracker.gg page, cropped to the selected playlist's row plus the stat
/// tiles picked in the settings. The page is loaded once, like a normal browser tab, and stays loaded; switching
/// playlist or stats only moves the crop.
/// </summary>
/// <remarks>
/// Sizes here are in the panel's own units; the overlay window scales the whole panel by the user's size setting.
/// The browser control is drawn at that scale (zoomed by it and shrunk back by the inverse transform), so the page
/// stays sharp instead of being stretched as a picture.
/// To keep loading cheap for the game (see <see cref="BrowserTuning"/>), a page waits for a loading turn, loads with
/// the browser control hidden (nothing is drawn or copied onto the overlay until the row is there) and fetches only
/// tracker.gg itself. Once the row is cropped it is shown as a still picture taken from the page and the browser
/// control is hidden again: a visible browser control copies frames onto the overlay all the time, even when nothing
/// changes, which cost almost half a CPU core for a lobby. The page stays loaded, so switching playlist or stats crops
/// it again at once and takes a new picture.
/// </remarks>
internal sealed class TrackerRowView : Grid, IDisposable
{
    /// <summary>The page is laid out as on a normal desktop browser this wide (CSS pixels).</summary>
    public const double ViewportWidth = 1024;

    private const double LoadingHeight = 720;

    /// <summary>Width of the star, platform mark, name and status line in front of the band.</summary>
    private const double InfoWidth = 236;
    private const double IconSize = 15;
    private const double StarSize = 15;
    private const double PlaceholderWidth = 640;
    private const double RowHeight = 51;
    private const double TileRowHeight = 69;
    private const double PageZoom = 0.6;
    private const double PageHeight = 430;
    private static readonly TimeSpan ShowPageAfter = TimeSpan.FromSeconds(12);
    private static readonly TimeSpan TilesWait = TimeSpan.FromSeconds(8);
    private static readonly TimeSpan FastPoll = TimeSpan.FromMilliseconds(400);
    private static readonly TimeSpan SlowPoll = TimeSpan.FromSeconds(1.5);

    /// <summary>A page still loading after this long lets the next one start too (it keeps loading).</summary>
    private static readonly TimeSpan TurnTimeout = TimeSpan.FromSeconds(8);
    private static readonly Color DefaultBand = Color.FromRgb(0, 21, 56);
    private static readonly Brush StarHoverFill = Frozen(Color.FromArgb(0x40, 0xFF, 0xC9, 0x3C));

    private readonly Func<Task<CoreWebView2Environment>> _environment;
    private readonly Action<string> _openProfile;
    private readonly IFavouriteMarks? _marks;
    private readonly Button _star;
    private readonly System.Windows.Shapes.Path _starShape;
    private readonly bool _canMark;
    private bool _isFavourite;
    private readonly ILogger _logger;
    private readonly string? _url;
    private readonly bool _sample;
    private readonly Border _nameHit;
    private readonly TextBlock _name;
    private readonly System.Windows.Shapes.Path _platform;
    private readonly TextBlock _arrow;
    private readonly TextBlock _badge;
    private readonly Border _badgeBox;
    private readonly TextBlock _note;
    private readonly Border _band;
    private readonly Canvas _clip;
    private readonly Border _scaler;
    private readonly Grid _pageHost = new();
    private readonly Image _snapshot = new() { Stretch = Stretch.Fill, IsHitTestVisible = false, Visibility = Visibility.Collapsed };
    private readonly ScaleTransform _unscale = new(1, 1);
    private readonly StackPanel _bandMessage;
    private readonly SkeletonBar _shimmer;
    private readonly TextBlock _bandText;
    private readonly Border _cover;
    private readonly DispatcherTimer _poll;
    private WebView2CompositionControl? _web;
    private string _label;
    private TrackerCropOptions _options;
    private double _zoom;
    private double _viewport = ViewportWidth;
    private double _textWidth = PlaceholderWidth;
    private bool _polling;

    private bool _recropPending;
    private bool _disposed;
    private bool _parked;
    private bool _starting;
    private string _stateNote = "";
    private IDisposable? _turn;
    private readonly CancellationTokenSource _life = new();
    private int _snapshotVersion;
    private bool _snapshotStale;
    private string? _pictureKey;
    private DateTime _loadStarted;
    private TrackerCropResult? _lastCrop;

    private IReadOnlyList<TrackerCell> _align = [];

    public TrackerRowView(
        LobbyPlayer player,
        string label,
        TrackerCropOptions options,
        double zoom,
        bool sample,
        Func<Task<CoreWebView2Environment>> environment,
        Action<string> openProfile,
        ILogger logger,
        IFavouriteMarks? marks = null)
    {
        Player = player;
        _marks = marks;
        _label = label;
        _options = options;
        _zoom = zoom;
        _sample = sample;
        _environment = environment;
        _openProfile = openProfile;
        _logger = logger;
        _url = sample ? null : TrackerUrls.Profile(player.Id);

        Margin = new Thickness(0, 0, 0, 6);
        ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(InfoWidth) });
        ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        // Star, platform mark, name (+ BOT for bots), then a status line. Pages are loaded again with the header's Refresh.
        var info = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 12, 0) };
        Children.Add(info);

        var nameLine = new StackPanel { Orientation = Orientation.Horizontal };
        info.Children.Add(nameLine);

        // The star saves the player as a favourite (or removes them). Bots and split-screen guests have none, but every
        // row keeps its space so the names start in one line. The Preview's made-up players show it without a click.
        _starShape = new System.Windows.Shapes.Path
        {
            Data = (Geometry)Application.Current.FindResource("StarGeometry"),
            Width = StarSize,
            Height = StarSize,
            Stretch = Stretch.Uniform,
            StrokeThickness = 1.4,
            StrokeLineJoin = PenLineJoin.Round,
            VerticalAlignment = VerticalAlignment.Center,
        };
        _star = new Button
        {
            Template = (ControlTemplate)Application.Current.FindResource("BareButton"),
            Content = _starShape,
            Padding = new Thickness(3),
            Margin = new Thickness(-3, 0, 3, 0),
            Focusable = false,
            VerticalAlignment = VerticalAlignment.Center,
            Visibility = FavouriteList.CanSave(player.Id) ? Visibility.Visible : Visibility.Hidden,
        };
        _canMark = marks?.CanMark(player, sample) == true;
        if (_canMark)
        {
            OverlayHit.SetIsInteractive(_star, true);
            _star.Cursor = System.Windows.Input.Cursors.Hand;
            _star.Click += OnStarClicked;
            DependencyPropertyDescriptor.FromProperty(OverlayHit.IsHoveredProperty, typeof(Button)).AddValueChanged(_star, OnStarHover);
        }
        else
        {
            _star.IsHitTestVisible = false;
            _star.IsEnabled = false;
        }

        nameLine.Children.Add(_star);

        // Every row keeps the mark's space (empty for bots), so the names start in one line.
        _platform = new System.Windows.Shapes.Path
        {
            Width = IconSize,
            Height = IconSize,
            Stretch = Stretch.Uniform,
            Fill = Brush("TextSecondary"),
            Margin = new Thickness(0, 1, 8, 0),
            VerticalAlignment = VerticalAlignment.Center,
            Visibility = Visibility.Hidden,
        };
        nameLine.Children.Add(_platform);
        _name = new TextBlock
        {
            FontSize = 17,
            FontWeight = FontWeights.SemiBold,
            MaxWidth = 158,
            TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center,
        };
        _nameHit = new Border { Background = Brushes.Transparent, Child = _name };
        nameLine.Children.Add(_nameHit);
        _badge = new TextBlock { FontSize = 8.5, FontWeight = FontWeights.SemiBold, Foreground = Brush("TextSecondary") };
        _badgeBox = new Border
        {
            BorderBrush = Brush("BadgeBorder"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(3),
            Padding = new Thickness(4, 0, 4, 1),
            Margin = new Thickness(7, 1, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
            Child = _badge,
        };
        nameLine.Children.Add(_badgeBox);
        _arrow = new TextBlock
        {
            Text = "↗",
            FontSize = 13,
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(6, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = Brush("TextSecondary"),
            Visibility = Visibility.Collapsed,
        };
        nameLine.Children.Add(_arrow);

        var noteLine = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 2, 0, 0) };
        info.Children.Add(noteLine);
        _note = new TextBlock
        {
            FontSize = 11.5,
            FontStyle = FontStyles.Italic,
            Foreground = Brush("TextSecondary"),
            TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center,
            MaxWidth = InfoWidth - 16,
        };
        noteLine.Children.Add(_note);

        if (_url is not null)
        {
            // The overlay is click-through except over elements marked interactive (see ClickThroughController).
            OverlayHit.SetIsInteractive(_nameHit, true);
            _nameHit.Cursor = System.Windows.Input.Cursors.Hand;
            _nameHit.MouseLeftButtonUp += OnNameClicked;
            DependencyPropertyDescriptor.FromProperty(OverlayHit.IsHoveredProperty, typeof(Border)).AddValueChanged(_nameHit, OnNameHover);
        }

        // The band: tracker.gg's own row, shown at its real size (no extra space around it).
        _web = null;
        _pageHost.Children.Add(_snapshot);
        _scaler = new Border { LayoutTransform = _unscale, Child = _pageHost };
        _clip = new Canvas { ClipToBounds = true };
        _clip.Children.Add(_scaler);
        _shimmer = new SkeletonBar { Width = 46, Height = 8, Margin = new Thickness(0, 0, 10, 0), VerticalAlignment = VerticalAlignment.Center };
        _bandText = new TextBlock { FontSize = 12.5, Foreground = Brush("TextSecondary"), VerticalAlignment = VerticalAlignment.Center };
        _bandMessage = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(14, 0, 14, 0),
            Children = { _shimmer, _bandText },
        };
        var bandGrid = new Grid();
        // Covers the page while it loads (the browser control can't be made transparent).
        _cover = new Border { Background = new SolidColorBrush(DefaultBand) };
        bandGrid.Children.Add(_clip);
        bandGrid.Children.Add(_cover);
        bandGrid.Children.Add(_bandMessage);
        _band = new Border
        {
            Background = new SolidColorBrush(DefaultBand),
            CornerRadius = new CornerRadius(4),
            ClipToBounds = true,
            Child = bandGrid,
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Center,
        };
        SetColumn(_band, 1);
        Children.Add(_band);

        _poll = new DispatcherTimer { Interval = FastPoll };
        _poll.Tick += async (_, _) => await PollAsync();

        // A picture can only be taken while the row is on screen; one that was due while hidden is taken on showing.
        IsVisibleChanged += (_, _) =>
        {
            if (IsVisible && _snapshotStale && State == TrackerRowState.Cropped)
            {
                _ = RefreshSnapshotAsync();
            }
        };

        Update(player);
        if (sample)
        {
            SetState(TrackerRowState.Sample, SampleText());
        }
        else if (_url is null)
        {
            SetState(TrackerRowState.NoProfile, NoProfileText(player.Id));
        }
        else
        {
            SetState(TrackerRowState.Loading, "Loading tracker.gg…");
        }
    }

    public LobbyPlayer Player { get; private set; }

    public TrackerRowState State { get; private set; }

    /// <summary>False for bots, split-screen guests and Preview samples: no tracker.gg page to load.</summary>
    public bool HasProfile => _url is not null;

    /// <summary>Width of the shown row (panel units), or null while no row is shown.</summary>
    public double? CropWidth => State switch
    {
        TrackerRowState.Cropped => _lastCrop?.Width,
        TrackerRowState.Sample => SampleWidth(),
        _ => null,
    };

    /// <summary>
    /// The shown row's cell and tile widths before lining up with the other rows (CSS pixels), or null while no row is
    /// shown.
    /// </summary>
    public IReadOnlyList<TrackerCell>? NaturalCells => State == TrackerRowState.Cropped ? _lastCrop?.Cells : null;

    /// <summary>When the page was last (re)loaded, so old pages can be refreshed for a new match.</summary>
    public DateTime LoadedAt { get; private set; } = DateTime.MinValue;

    /// <summary>
    /// A match the player was in has finished since the page loaded, so their rating may have moved: the page is loaded
    /// again before it is shown for another match.
    /// </summary>
    public bool Outdated { get; private set; }

    /// <summary>
    /// Can the page be kept for a while after the player is gone from the screen (see <see cref="Park"/>)? Only a
    /// loaded one (a row, or tracker.gg's answer that there is none) that no finished match has made outdated.
    /// </summary>
    public bool CanPark => HasProfile && !_disposed && !Outdated && _web?.CoreWebView2 is not null
        && State is TrackerRowState.Cropped or TrackerRowState.NoRow or TrackerRowState.NotFound;

    /// <summary>Raised when the row's state or size changed (the screen lines the text bands up with it).</summary>
    public event Action? Changed;

    /// <summary>Width for bands that show text instead of a row: the widest row on the screen.</summary>
    public void SetTextBandWidth(double width)
    {
        if (Math.Abs(width - _textWidth) < 0.5)
        {
            return;
        }

        _textWidth = width;
        ApplySize();
    }

    /// <summary>Name, platform, team (the team can change after joining) and whether the player left the match.</summary>
    public void Update(LobbyPlayer player)
    {
        Player = player;
        _name.Text = player.Name;
        var icon = PlatformIcons.For(player.Id.Platform);
        _platform.Data = icon;
        _platform.Visibility = icon is not null ? Visibility.Visible : Visibility.Hidden;
        AutomationProperties.SetName(_platform, PlatformIcons.Name(player.Id.Platform));

        // A platform with a mark shows the mark; the text stays only for bots.
        var text = icon is null ? player.Id.BadgeText : "";
        _badge.Text = string.Join(" ", text.ToCharArray());
        _badgeBox.Visibility = text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;

        // Someone who quit stays on the board until the next game, faded and marked.
        Opacity = player.HasLeft ? 0.55 : 1;
        ShowNote();
        RefreshStar();
    }

    /// <summary>Whether the star shows the player as a favourite (call after the favourites changed).</summary>
    public bool IsFavourite => _isFavourite;

    /// <summary>The favourites changed: fill or empty the star.</summary>
    public void RefreshStar()
    {
        _isFavourite = _marks?.IsFavourite(Player.Id) == true;
        var hovered = _canMark && OverlayHit.GetIsHovered(_star);
        var gold = Brush("FavouriteGold");
        _starShape.Fill = _isFavourite ? gold : hovered ? StarHoverFill : Brushes.Transparent;
        _starShape.Stroke = _isFavourite || hovered ? gold : Brush("TextMuted");
        AutomationProperties.SetName(_star, _isFavourite ? $"Remove {Player.Name} from favourites" : $"Add {Player.Name} to favourites");
    }

    private void OnStarClicked(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        _marks?.Toggle(Player);
        RefreshStar();
    }

    private void OnStarHover(object? sender, EventArgs e) => RefreshStar();

    /// <summary>Why a player has no tracker.gg page; an unknown platform is named, so its spelling can be added.</summary>
    private static string NoProfileText(PlatformId id) =>
        id.IsBot ? "Bot: no tracker.gg profile"
        : id.IsSplitscreenGuest ? "Split-screen guest: no tracker.gg profile"
        : $"Unknown platform “{id.Prefix}”: no tracker.gg profile";

    private static Brush Frozen(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }

    /// <summary>The line under the name: "left the match" for a quitter, else what the band is doing.</summary>
    private void ShowNote()
    {
        _note.Text = Player.HasLeft ? "left the match" : _stateNote;
        _note.Visibility = _note.Text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>Starts loading the page (once; later calls do nothing).</summary>
    public async Task StartAsync()
    {
        if (_url is null || _web is not null || _disposed || _starting)
        {
            return;
        }

        _starting = true;
        try
        {
            await StartCoreAsync();
        }
        finally
        {
            _starting = false;
        }
    }

    private async Task StartCoreAsync()
    {
        CoreWebView2Environment environment;
        try
        {
            environment = await _environment();
        }
        catch (WebView2RuntimeNotFoundException)
        {
            SetState(TrackerRowState.Failed, "Needs the Microsoft Edge WebView2 Runtime");
            return;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not start the embedded browser");
            SetState(TrackerRowState.Failed, "The embedded browser could not start (see the log)");
            return;
        }

        if (_disposed || _web is not null || !await TakeTurnAsync())
        {
            return;
        }

        var web = new WebView2CompositionControl
        {
            IsHitTestVisible = false,
            Focusable = false,
            DefaultBackgroundColor = System.Drawing.Color.FromArgb(DefaultBand.R, DefaultBand.G, DefaultBand.B),
            Visibility = Visibility.Hidden, // drawn only once there is a row to show
        };
        BrowserTuning.FollowVisibility(web, _logger);
        _web = web;
        _pageHost.Children.Insert(0, web);
        ApplySize();

        try
        {
            await web.EnsureCoreWebView2Async(environment);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not create the embedded browser for {Player}", Player.Name);
            SetState(TrackerRowState.Failed, "The embedded browser could not start (see the log)");
            return;
        }

        if (_disposed)
        {
            return;
        }

        var core = web.CoreWebView2;
        var settings = core.Settings;
        settings.AreDevToolsEnabled = false;
        settings.AreDefaultContextMenusEnabled = false;
        settings.IsZoomControlEnabled = false;
        settings.IsStatusBarEnabled = false;
        settings.AreBrowserAcceleratorKeysEnabled = false;
        settings.IsGeneralAutofillEnabled = false;
        settings.IsPasswordAutosaveEnabled = false;
        settings.AreDefaultScriptDialogsEnabled = false;
        core.IsMuted = true;
        core.NewWindowRequested += (_, e) => e.Handled = true; // no pop-ups or new windows
        core.NavigationStarting += OnNavigationStarting;
        core.NavigationCompleted += OnNavigationCompleted;
        BrowserTuning.BlockExtras(core, _logger);
        Navigate();
    }

    /// <summary>Waits for a loading turn (see <see cref="BrowserTuning.MaxLoadingAtOnce"/>); false if the row closed meanwhile.</summary>
    private async Task<bool> TakeTurnAsync()
    {
        if (_turn is not null)
        {
            return true;
        }

        try
        {
            var turn = await BrowserTuning.TakeTurnAsync(_life.Token);
            if (_disposed)
            {
                turn.Dispose();
                return false;
            }

            _turn = turn;
            return true;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }

    private void ReleaseTurn()
    {
        _turn?.Dispose();
        _turn = null;
    }

    /// <summary>The selected playlist changed: crop the already loaded page to that row (no reload).</summary>
    public void SetLabel(string label)
    {
        if (label == _label)
        {
            return;
        }

        _label = label;
        _align = []; // another row, other widths
        Recrop();
    }

    /// <summary>The picked stats changed: crop again with the new cells and tiles (no reload).</summary>
    public void SetOptions(TrackerCropOptions options)
    {
        if (options.Key == _options.Key)
        {
            return;
        }

        _options = options;
        _align = [];
        if (State == TrackerRowState.Sample)
        {
            SetState(TrackerRowState.Sample, SampleText());
            return;
        }

        Recrop();
    }

    /// <summary>
    /// Minimum widths for the cells and tiles (see <see cref="TrackerPage.AlignWidths"/>) so this row lines up with the
    /// others on the screen.
    /// </summary>
    public void SetAlignment(IReadOnlyList<TrackerCell> widths)
    {
        if (widths.SequenceEqual(_align))
        {
            return;
        }

        _align = widths;
        Recrop();
    }

    /// <summary>The overlay's size setting: the page is drawn at that zoom so it stays sharp.</summary>
    public void SetZoom(double zoom)
    {
        if (Math.Abs(zoom - _zoom) < 0.001)
        {
            return;
        }

        _zoom = zoom;
        ApplySize();
        if (State == TrackerRowState.Cropped)
        {
            _ = RefreshSnapshotAsync(); // the picture was taken at the old size
        }
    }

    /// <summary>The match on the screen has finished (see <see cref="Outdated"/>).</summary>
    public void MarkOutdated() => Outdated = HasProfile && LoadedAt != DateTime.MinValue;

    /// <summary>
    /// The player is gone from the screen, but may well be back soon (the next match, a replay, the favourites screen):
    /// the page stays loaded, out of sight, with its scripts paused and its memory trimmed, so it can be shown again at
    /// once (<see cref="Unpark"/>).
    /// </summary>
    public void Park()
    {
        _parked = true;
        _poll.Stop();
        if (_web?.CoreWebView2 is not { } core)
        {
            return;
        }

        try
        {
            core.MemoryUsageTargetLevel = CoreWebView2MemoryUsageTargetLevel.Low;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Overlay: could not trim the memory of {Player}'s kept page", Player.Name);
        }

        // Pausing needs the browser control out of sight, which it is once the row's collapse has been laid out.
        _ = Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(async () =>
        {
            if (!_parked || _disposed || _web?.CoreWebView2 is not { } page || page.IsSuspended)
            {
                return;
            }

            string result;
            try
            {
                result = await page.TrySuspendAsync() ? "paused" : "running";
            }
            catch (Exception ex)
            {
                result = $"running: {ex.GetType().Name}";
                _logger.LogDebug(ex, "Overlay: could not pause {Player}'s kept page", Player.Name);
            }

            _logger.LogInformation("Overlay: {Player}'s page is kept for a while ({Result})", Player.Name, result);
        }));
    }

    /// <summary>The player is back on the screen: the kept page wakes up and is cropped again (no reload).</summary>
    public void Unpark()
    {
        _parked = false;
        if (_web?.CoreWebView2 is { } core)
        {
            try
            {
                core.MemoryUsageTargetLevel = CoreWebView2MemoryUsageTargetLevel.Normal;
                if (core.IsSuspended)
                {
                    core.Resume();
                }
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Overlay: could not wake {Player}'s kept page", Player.Name);
            }
        }

        Recrop();
    }

    public void Reload()
    {
        if (_web?.CoreWebView2 is null)
        {
            _ = StartAsync();
            return;
        }

        _ = ReloadInTurnAsync();
    }

    private async Task ReloadInTurnAsync()
    {
        SetState(TrackerRowState.Loading, "Loading tracker.gg…");
        if (await TakeTurnAsync() && !_disposed)
        {
            Navigate();
        }
    }

    private void Recrop()
    {
        if (State is TrackerRowState.Cropped or TrackerRowState.NoRow)
        {
            if (_polling)
            {
                // A crop is running (this may be its own result coming in): crop again right after it.
                _recropPending = true;
            }
            else
            {
                _ = PollAsync();
            }
        }
    }

    private void Navigate()
    {
        if (_web?.CoreWebView2 is not { } core || _url is null)
        {
            return;
        }

        _lastCrop = null;
        _viewport = ViewportWidth; // tracker.gg's columns are measured at the usual desktop width
        _loadStarted = DateTime.UtcNow;
        LoadedAt = DateTime.UtcNow;
        Outdated = false;
        SetState(TrackerRowState.Loading, "Loading tracker.gg…");
        _logger.LogInformation("Overlay: loading {Url}", _url);
        core.Navigate(_url);
        _poll.Interval = FastPoll;
        _poll.Start();
    }

    /// <summary>Only tracker.gg's Rocket League site may load in the main frame.</summary>
    private void OnNavigationStarting(object? sender, CoreWebView2NavigationStartingEventArgs e)
    {
        if (!Uri.TryCreate(e.Uri, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps
            || !uri.Host.Equals("rocketleague.tracker.network", StringComparison.OrdinalIgnoreCase))
        {
            _logger.LogInformation("Overlay: blocked navigation to {Url}", e.Uri);
            e.Cancel = true;
        }
    }

    private void OnNavigationCompleted(object? sender, CoreWebView2NavigationCompletedEventArgs e)
    {
        if (!e.IsSuccess && e.WebErrorStatus != CoreWebView2WebErrorStatus.OperationCanceled && State != TrackerRowState.Cropped)
        {
            _poll.Stop();
            _logger.LogInformation("Overlay: {Player}'s page failed to load ({Status})", Player.Name, e.WebErrorStatus);
            SetState(TrackerRowState.Failed, $"Couldn't load tracker.gg ({e.WebErrorStatus})");
        }
    }

    private async Task PollAsync()
    {
        if (_polling || _disposed || _web?.CoreWebView2 is not { } core)
        {
            return;
        }

        _polling = true;
        var label = _label;
        var options = _options;
        var align = _align;
        try
        {
            var json = await core.ExecuteScriptAsync(TrackerPage.CropScript(label, options, align));
            if (_disposed)
            {
                return;
            }

            var result = TrackerPage.ParseResult(json);
            switch (result.Status)
            {
                case TrackerCropStatus.Ok:
                    if (State != TrackerRowState.Cropped)
                    {
                        _logger.LogInformation("Overlay: {Player}'s row is in ({Seconds:0.0} s)", Player.Name, (DateTime.UtcNow - _loadStarted).TotalSeconds);
                    }

                    // The same crop of the same row again (tiles checked once more, widths re-aligned to the same values):
                    // the picture already shows it.
                    var pictureKey = $"{label}|{options.Key}|{string.Join(',', align)}";
                    var unchanged = State == TrackerRowState.Cropped && pictureKey == _pictureKey && SameCrop(_lastCrop, result);
                    _lastCrop = result;
                    SetBandColour(result.Background);
                    _poll.Stop();
                    if (result.Width > _viewport)
                    {
                        // Wider than the page itself (several tiles): widen the page and measure once more.
                        _viewport = result.Width + 16;
                        _poll.Interval = FastPoll;
                        _poll.Start();
                    }
                    else if (result.Missing > 0 && DateTime.UtcNow - _loadStarted < TilesWait)
                    {
                        _poll.Interval = SlowPoll; // the tiles may still be on their way
                        _poll.Start();
                    }

                    SetState(TrackerRowState.Cropped, "");
                    if (!unchanged)
                    {
                        _pictureKey = pictureKey;
                        _ = RefreshSnapshotAsync();
                    }

                    break;

                case TrackerCropStatus.NoRow:
                    _poll.Stop();
                    SetState(TrackerRowState.NoRow, $"No {label} rating on tracker.gg");
                    break;

                case TrackerCropStatus.NotFound:
                    _poll.Stop();
                    SetState(TrackerRowState.NotFound, "Not found on tracker.gg");
                    break;

                default:
                    if (_turn is not null && DateTime.UtcNow - _loadStarted > TurnTimeout)
                    {
                        ReleaseTurn(); // slow page: let the next one start as well
                    }

                    if (State == TrackerRowState.Loading && DateTime.UtcNow - _loadStarted > ShowPageAfter)
                    {
                        // Something else is on the page (a browser check, a notice): show it so the user can see it.
                        _logger.LogInformation("Overlay: no playlist table for {Player} after {Seconds} s; showing the page",
                            Player.Name, ShowPageAfter.TotalSeconds);
                        _poll.Interval = SlowPoll;
                        SetState(TrackerRowState.ShowingPage, "tracker.gg's page (the row appears once it loads)");
                    }

                    break;
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.Runtime.InteropServices.COMException or ObjectDisposedException)
        {
            // The page navigated or the control closed mid-script; the next tick tries again.
        }
        finally
        {
            _polling = false;

            // The playlist, stats or widths changed while this crop ran: crop again with the new ones.
            var stale = label != _label || options.Key != _options.Key || !align.SequenceEqual(_align);
            if ((_recropPending || stale) && !_disposed && State is TrackerRowState.Cropped or TrackerRowState.NoRow)
            {
                _ = Dispatcher.BeginInvoke(DispatcherPriority.Background, () => _ = PollAsync());
            }

            _recropPending = false;
        }
    }

    private static bool SameCrop(TrackerCropResult? a, TrackerCropResult b) =>
        a is not null && a.Width == b.Width && a.Height == b.Height && a.Background == b.Background && a.Missing == b.Missing
        && (a.Cells ?? []).SequenceEqual(b.Cells ?? []);

    private void SetBandColour(string? css)
    {
        var colour = TrackerPage.TryParseCssColor(css, out var r, out var g, out var b) ? Color.FromRgb(r, g, b) : DefaultBand;
        _band.Background = new SolidColorBrush(colour);
        _cover.Background = _band.Background;
        if (_web is not null)
        {
            _web.DefaultBackgroundColor = System.Drawing.Color.FromArgb(colour.R, colour.G, colour.B);
        }
    }

    private void SetState(TrackerRowState state, string text)
    {
        State = state;
        if (state != TrackerRowState.Loading)
        {
            ReleaseTurn(); // done loading (a row, a message or the whole page): the next page may start
        }

        _bandText.Text = text;
        var showsPage = state is TrackerRowState.Cropped or TrackerRowState.ShowingPage;
        _bandMessage.Visibility = showsPage ? Visibility.Collapsed : Visibility.Visible;
        _shimmer.Visibility = state == TrackerRowState.Loading ? Visibility.Visible : Visibility.Collapsed;
        _bandText.FontStyle = state == TrackerRowState.Sample ? FontStyles.Italic : FontStyles.Normal;
        _stateNote = state switch
        {
            TrackerRowState.Loading => "loading…",
            TrackerRowState.ShowingPage => "waiting for tracker.gg",
            TrackerRowState.NoRow => "no rating here",
            TrackerRowState.NotFound => "not on tracker.gg",
            TrackerRowState.Failed => "didn't load",
            TrackerRowState.Sample => "preview",
            _ => "",
        };
        ShowNote();
        _cover.Visibility = showsPage ? Visibility.Collapsed : Visibility.Visible;
        if (_web is not null)
        {
            // Hidden while there is nothing to show: a hidden page isn't drawn or copied onto the overlay. A cropped row
            // is shown as a picture (RefreshSnapshotAsync); a whole page (a browser check) stays live so it can be used.
            if (state != TrackerRowState.Cropped)
            {
                _snapshotVersion++;
                _snapshotStale = false;
                _pictureKey = null;
                _snapshot.Visibility = Visibility.Collapsed;
                _web.Visibility = state == TrackerRowState.ShowingPage ? Visibility.Visible : Visibility.Hidden;
            }

            // The cropped row is a picture; a whole page (e.g. a browser check) can be clicked.
            _web.IsHitTestVisible = state == TrackerRowState.ShowingPage;
            OverlayHit.SetIsInteractive(_web, state == TrackerRowState.ShowingPage);
        }

        ApplySize();
        Changed?.Invoke();
    }

    /// <summary>
    /// Shows the freshly cropped row live for a moment, takes a picture of it, then shows the picture and hides the
    /// browser control again (see the remarks above). A newer crop or state change cancels an older one.
    /// </summary>
    private async Task RefreshSnapshotAsync()
    {
        var version = ++_snapshotVersion;
        if (_web is not { } web || web.CoreWebView2 is not { } core || State != TrackerRowState.Cropped)
        {
            return;
        }

        bool Stale() => version != _snapshotVersion || _disposed || State != TrackerRowState.Cropped;

        web.Visibility = Visibility.Visible;
        if (!IsVisible)
        {
            // Off screen (the panel is hidden): nothing would be drawn to take a picture of. Done when it shows.
            _snapshotStale = true;
            _snapshot.Visibility = Visibility.Collapsed;
            return;
        }

        _snapshotStale = false;
        try
        {
            await Task.Delay(150);
            if (Stale())
            {
                return;
            }

            _snapshot.Visibility = Visibility.Collapsed; // the new crop, live
            await Task.Delay(350);
            if (Stale())
            {
                return;
            }

            using var png = new MemoryStream();
            await core.CapturePreviewAsync(CoreWebView2CapturePreviewImageFormat.Png, png);
            if (Stale() || png.Length == 0)
            {
                return;
            }

            png.Position = 0;
            var picture = new BitmapImage();
            picture.BeginInit();
            picture.CacheOption = BitmapCacheOption.OnLoad;
            picture.StreamSource = png;
            picture.EndInit();
            picture.Freeze();
            _snapshot.Source = picture;
            _snapshot.Visibility = Visibility.Visible;
            await Task.Delay(50); // let the picture reach the screen before the live view goes
            if (!Stale())
            {
                web.Visibility = Visibility.Hidden;
                _logger.LogDebug("Overlay: picture of {Player}'s row ({Width}x{Height})", Player.Name, picture.PixelWidth, picture.PixelHeight);
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.Runtime.InteropServices.COMException or ObjectDisposedException
            or NotSupportedException or FileFormatException)
        {
            // No picture: the live view simply stays.
            _logger.LogDebug(ex, "Overlay: no picture of {Player}'s row; showing it live", Player.Name);
        }
    }

    private void ApplySize()
    {
        double width, height, pageZoom, webWidth, webHeight;
        switch (State)
        {
            case TrackerRowState.Cropped when _lastCrop is { } crop:
                width = crop.Width;
                height = crop.Height;
                pageZoom = _zoom;
                webWidth = _viewport;
                webHeight = crop.Height;
                break;

            case TrackerRowState.ShowingPage:
                width = Math.Ceiling(ViewportWidth * PageZoom);
                height = PageHeight;
                pageZoom = _zoom * PageZoom;
                webWidth = width;
                webHeight = height;
                break;

            case TrackerRowState.Sample:
                width = SampleWidth();
                height = _options.Tiles.Count > 0 || (_options.Best && HasAllTimePeak) ? TileRowHeight : RowHeight;
                pageZoom = _zoom;
                webWidth = _viewport;
                webHeight = LoadingHeight;
                break;

            default:
                width = Math.Ceiling(_textWidth);
                height = _options.Tiles.Count > 0 || (_options.Best && HasAllTimePeak) ? TileRowHeight : RowHeight;
                pageZoom = _zoom;
                webWidth = _viewport;
                webHeight = LoadingHeight;
                break;
        }

        _clip.Width = width;
        _clip.Height = height;
        _band.Width = width;
        _band.Height = height;

        // Drawn at the overlay's scale, then shrunk back so it lands on whole device pixels (sharp text).
        _unscale.ScaleX = _unscale.ScaleY = 1 / _zoom;
        if (_web is not null)
        {
            _web.Width = Math.Ceiling(webWidth * _zoom);
            _web.Height = Math.Ceiling(webHeight * _zoom);
            if (Math.Abs(_web.ZoomFactor - pageZoom) > 0.0001)
            {
                _web.ZoomFactor = pageZoom;
            }

            _snapshot.Width = _web.Width;
            _snapshot.Height = _web.Height;
        }
    }

    /// <summary>
    /// Roughly how wide tracker.gg's row is with the picked stats (CSS pixels measured at 1024 wide, November 2026), for
    /// the Preview's placeholders.
    /// </summary>
    private double SampleWidth() =>
        412 + (_options.Peak ? 111 : 0) + (_options.Streak ? 106 : 0) + (_options.Best && HasAllTimePeak ? 157 : 0) + (_options.Tiles.Count * 120);

    /// <summary>tracker.gg's Peak Rating card only covers Ranked Duel, Doubles and Standard.</summary>
    private bool HasAllTimePeak => _label is "Ranked Duel 1v1" or "Ranked Doubles 2v2" or "Ranked Standard 3v3";

    private string SampleText()
    {
        var parts = new List<string> { "Rank, rating, division ±" };
        if (_options.Peak)
        {
            parts.Add("Peak");
        }

        if (_options.Streak)
        {
            parts.Add("Streak");
        }

        if (_options.Best && HasAllTimePeak)
        {
            parts.Add("All-time peak");
        }

        parts.AddRange(_options.Tiles);
        return $"{_label} row from tracker.gg: {string.Join(" · ", parts)}";
    }

    private void OnNameClicked(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (_url is not null)
        {
            e.Handled = true;
            _openProfile(_url);
        }
    }

    private void OnNameHover(object? sender, EventArgs e)
    {
        var hovered = OverlayHit.GetIsHovered(_nameHit);
        _name.TextDecorations = hovered ? TextDecorations.Underline : null;
        _arrow.Visibility = hovered ? Visibility.Visible : Visibility.Collapsed;
    }

    private static Brush Brush(string key) => (Brush)Application.Current.FindResource(key);

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _poll.Stop();
        _life.Cancel();
        ReleaseTurn();
        if (_url is not null)
        {
            DependencyPropertyDescriptor.FromProperty(OverlayHit.IsHoveredProperty, typeof(Border)).RemoveValueChanged(_nameHit, OnNameHover);
        }

        if (_canMark)
        {
            _star.Click -= OnStarClicked;
            DependencyPropertyDescriptor.FromProperty(OverlayHit.IsHoveredProperty, typeof(Button)).RemoveValueChanged(_star, OnStarHover);
        }

        if (_web is { } web)
        {
            if (web.CoreWebView2 is { } core)
            {
                core.NavigationStarting -= OnNavigationStarting;
                core.NavigationCompleted -= OnNavigationCompleted;
            }

            _pageHost.Children.Remove(web);
            web.Dispose();
            _web = null;
        }
    }
}

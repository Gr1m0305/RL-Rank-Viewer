using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Extensions.Logging;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;
using RLOverlay.App.Overlay;
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
/// One player on the overlay screen: the name (click for the full profile in the browser) and a band with the
/// player's tracker.gg page, cropped to the selected playlist's row plus the stat tiles picked in the settings. The
/// page is loaded once, like a normal browser tab, and stays loaded; switching playlist or stats only moves the crop.
/// </summary>
/// <remarks>
/// Sizes here are in the panel's own units; the overlay window scales the whole panel by the user's size setting.
/// The browser control is drawn at that scale (zoomed by it and shrunk back by the inverse transform), so the page
/// stays sharp instead of being stretched as a picture.
/// </remarks>
internal sealed class TrackerRowView : Grid, IDisposable
{
    /// <summary>The page is laid out as on a normal desktop browser this wide (CSS pixels).</summary>
    public const double ViewportWidth = 1024;

    private const double LoadingHeight = 720;
    private const double PlaceholderWidth = 640;
    private const double RowHeight = 51;
    private const double TileRowHeight = 69;
    private const double PageZoom = 0.6;
    private const double PageHeight = 430;
    private static readonly TimeSpan ShowPageAfter = TimeSpan.FromSeconds(12);
    private static readonly TimeSpan TilesWait = TimeSpan.FromSeconds(8);
    private static readonly TimeSpan FastPoll = TimeSpan.FromMilliseconds(400);
    private static readonly TimeSpan SlowPoll = TimeSpan.FromSeconds(1.5);
    private static readonly Color DefaultBand = Color.FromRgb(0, 21, 56);

    private readonly Func<Task<CoreWebView2Environment>> _environment;
    private readonly Action<string> _openProfile;
    private readonly ILogger _logger;
    private readonly string? _url;
    private readonly bool _sample;
    private readonly Border _nameHit;
    private readonly TextBlock _name;
    private readonly TextBlock _arrow;
    private readonly TextBlock _badge;
    private readonly Border _badgeBox;
    private readonly TextBlock _note;
    private readonly Button _reload;
    private readonly Border _band;
    private readonly Canvas _clip;
    private readonly Border _scaler;
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
        ILogger logger)
    {
        Player = player;
        _label = label;
        _options = options;
        _zoom = zoom;
        _sample = sample;
        _environment = environment;
        _openProfile = openProfile;
        _logger = logger;
        _url = sample ? null : TrackerUrls.Profile(player.Id);

        Margin = new Thickness(0, 0, 0, 6);
        ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(176) });
        ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        // Name (+ platform badge), then a status line with a reload button.
        var info = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 12, 0) };
        Children.Add(info);

        var nameLine = new StackPanel { Orientation = Orientation.Horizontal };
        info.Children.Add(nameLine);
        _name = new TextBlock
        {
            FontSize = 14.5,
            FontWeight = FontWeights.SemiBold,
            MaxWidth = 112,
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
            FontSize = 12,
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
            MaxWidth = 128,
        };
        noteLine.Children.Add(_note);
        _reload = new Button
        {
            Content = "↻",
            FontSize = 12,
            Padding = new Thickness(5, 0, 5, 1),
            Margin = new Thickness(4, 0, 0, 0),
            ToolTip = "Load the page again",
            Visibility = Visibility.Collapsed,
            Focusable = false,
            Template = ReloadButtonTemplate(),
            VerticalAlignment = VerticalAlignment.Center,
        };
        OverlayHit.SetIsInteractive(_reload, true);
        _reload.Click += (_, _) => Reload();
        noteLine.Children.Add(_reload);

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
        _scaler = new Border { LayoutTransform = _unscale };
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

        Update(player);
        if (sample)
        {
            SetState(TrackerRowState.Sample, SampleText());
        }
        else if (_url is null)
        {
            SetState(TrackerRowState.NoProfile, player.Id.IsBot ? "Bot: no tracker.gg profile" : "Split-screen guest: no tracker.gg profile");
        }
        else
        {
            SetState(TrackerRowState.Loading, "Loading tracker.gg…");
        }
    }

    public LobbyPlayer Player { get; private set; }

    public TrackerRowState State { get; private set; }

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

    /// <summary>Name, badge and team (the team can change after joining).</summary>
    public void Update(LobbyPlayer player)
    {
        Player = player;
        _name.Text = player.Name;
        _badge.Text = string.Join(" ", player.Id.BadgeText.ToCharArray());
        _badgeBox.Visibility = player.Id.BadgeText.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>Starts loading the page (once; later calls do nothing).</summary>
    public async Task StartAsync()
    {
        if (_url is null || _web is not null || _disposed)
        {
            return;
        }

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

        if (_disposed || _web is not null)
        {
            return;
        }

        var web = new WebView2CompositionControl
        {
            IsHitTestVisible = false,
            Focusable = false,
            DefaultBackgroundColor = System.Drawing.Color.FromArgb(DefaultBand.R, DefaultBand.G, DefaultBand.B),
        };
        _web = web;
        _scaler.Child = web;
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
        Navigate();
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
    }

    public void Reload()
    {
        if (_web?.CoreWebView2 is null)
        {
            _ = StartAsync();
            return;
        }

        Navigate();
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
        _bandText.Text = text;
        var showsPage = state is TrackerRowState.Cropped or TrackerRowState.ShowingPage;
        _bandMessage.Visibility = showsPage ? Visibility.Collapsed : Visibility.Visible;
        _shimmer.Visibility = state == TrackerRowState.Loading ? Visibility.Visible : Visibility.Collapsed;
        _bandText.FontStyle = state == TrackerRowState.Sample ? FontStyles.Italic : FontStyles.Normal;
        _note.Text = state switch
        {
            TrackerRowState.Loading => "loading…",
            TrackerRowState.ShowingPage => "waiting for tracker.gg",
            TrackerRowState.NoRow => "no rating here",
            TrackerRowState.NotFound => "not on tracker.gg",
            TrackerRowState.Failed => "didn't load",
            TrackerRowState.Sample => "preview",
            _ => "",
        };
        _note.Visibility = _note.Text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        _reload.Visibility = _url is not null && state != TrackerRowState.Loading ? Visibility.Visible : Visibility.Collapsed;
        _cover.Visibility = showsPage ? Visibility.Collapsed : Visibility.Visible;
        if (_web is not null)
        {
            // The cropped row is a picture; a whole page (e.g. a browser check) can be clicked.
            _web.IsHitTestVisible = state == TrackerRowState.ShowingPage;
            OverlayHit.SetIsInteractive(_web, state == TrackerRowState.ShowingPage);
        }

        ApplySize();
        Changed?.Invoke();
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
                height = _options.Tiles.Count > 0 ? TileRowHeight : RowHeight;
                pageZoom = _zoom;
                webWidth = _viewport;
                webHeight = LoadingHeight;
                break;

            default:
                width = Math.Ceiling(_textWidth);
                height = _options.Tiles.Count > 0 ? TileRowHeight : RowHeight;
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
        }
    }

    /// <summary>
    /// Roughly how wide tracker.gg's row is with the picked stats (CSS pixels measured at 1024 wide, November 2026), for
    /// the Preview's placeholders.
    /// </summary>
    private double SampleWidth() => 412 + (_options.Peak ? 111 : 0) + (_options.Streak ? 106 : 0) + (_options.Tiles.Count * 120);

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

    private static ControlTemplate ReloadButtonTemplate()
    {
        var border = new FrameworkElementFactory(typeof(Border));
        border.Name = "Chrome";
        border.SetValue(Border.BackgroundProperty, Brushes.Transparent);
        border.SetValue(Border.CornerRadiusProperty, new CornerRadius(4));
        border.SetValue(Border.PaddingProperty, new TemplateBindingExtension(Control.PaddingProperty));
        var text = new FrameworkElementFactory(typeof(ContentPresenter));
        text.SetValue(TextElement.ForegroundProperty, Application.Current.FindResource("TextSecondary"));
        border.AppendChild(text);
        var template = new ControlTemplate(typeof(Button)) { VisualTree = border };
        var hover = new Trigger { Property = OverlayHit.IsHoveredProperty, Value = true };
        hover.Setters.Add(new Setter(Border.BackgroundProperty, Application.Current.FindResource("KeyCapBackground"), "Chrome"));
        template.Triggers.Add(hover);
        return template;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _poll.Stop();
        if (_url is not null)
        {
            DependencyPropertyDescriptor.FromProperty(OverlayHit.IsHoveredProperty, typeof(Border)).RemoveValueChanged(_nameHit, OnNameHover);
        }

        if (_web is { } web)
        {
            if (web.CoreWebView2 is { } core)
            {
                core.NavigationStarting -= OnNavigationStarting;
                core.NavigationCompleted -= OnNavigationCompleted;
            }

            _scaler.Child = null;
            web.Dispose();
            _web = null;
        }
    }
}

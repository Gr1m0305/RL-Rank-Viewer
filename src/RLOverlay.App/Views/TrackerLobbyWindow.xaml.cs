using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Threading;
using Microsoft.Extensions.Logging;
using Microsoft.Web.WebView2.Core;
using RLOverlay.App.Native;
using RLOverlay.App.Services;
using RLOverlay.Core;
using RLOverlay.Core.Lobby;
using RLOverlay.Core.Playlists;

namespace RLOverlay.App.Views;

/// <summary>
/// Ctrl+T: every player in the lobby with their row from tracker.gg for the playlist selected in the overlay
/// (Ctrl+G switches it here too). Each row is the player's real tracker.gg page, loaded in an embedded browser when
/// this window opens and cropped to that one row; the app never reads the numbers. The window stays open across
/// matches and keeps loaded pages while hidden, so reopening it doesn't load them again.
/// </summary>
public partial class TrackerLobbyWindow : Window
{
    /// <summary>Pages older than this are loaded again when the window is reopened.</summary>
    private static readonly TimeSpan RefreshAfter = TimeSpan.FromMinutes(10);

    private static Task<CoreWebView2Environment>? s_environment;

    private readonly Dictionary<string, TrackerRowView> _rows = new();
    private readonly TextBlock _blueHeader;
    private readonly TextBlock _orangeHeader;
    private readonly TextBlock _otherHeader;
    private readonly Action<string> _openProfile;
    private readonly ILogger _logger;
    private IReadOnlyList<LobbyPlayer> _players = [];
    private bool _previewing;
    private Playlist _playlist = Playlist.Doubles;
    private double _zoom = 1;
    private double? _rightEdge;
    private bool _placing;
    private bool _closing;

    internal TrackerLobbyWindow(Action<string> openProfile, ILogger logger)
    {
        InitializeComponent();
        _openProfile = openProfile;
        _logger = logger;
        _blueHeader = TeamHeader("BLUE", "AccentBlue");
        _orangeHeader = TeamHeader("ORANGE", "AccentOrange");
        _otherHeader = TeamHeader("JOINING", "TextMuted");
        SourceInitialized += (_, _) => UseDarkTitleBar();
        SizeChanged += (_, _) => KeepRightEdge();
        LocationChanged += (_, _) =>
        {
            if (!_placing && IsLoaded)
            {
                _rightEdge = Left + ActualWidth; // the user moved it: stay where they put it
            }
        };
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape)
            {
                Hide();
                e.Handled = true;
            }
        };
        SetPlaylist(_playlist);
        Rebuild();
    }

    /// <summary>The browser profile all rows share (cookies, cache), created on first use.</summary>
    private static Task<CoreWebView2Environment> Environment()
    {
        if (s_environment is null || s_environment.IsFaulted || s_environment.IsCanceled)
        {
            s_environment = CoreWebView2Environment.CreateAsync(null, AppPaths.WebViewDataDir);
        }

        return s_environment;
    }

    public void SetHints(string cycleHotkey, string trackerHotkey) =>
        HintText.Text = $"{cycleHotkey} playlist  ·  {trackerHotkey} or Esc closes  ·  click a name for the full profile";

    /// <summary>The overlay's selected playlist: every row moves its crop to that playlist (no reload).</summary>
    public void SetPlaylist(Playlist playlist)
    {
        _playlist = playlist;
        var label = TrackerPage.RowLabel(playlist);
        PlaylistText.Text = label;
        foreach (var row in _rows.Values)
        {
            row.SetLabel(label);
        }

        UpdateEmptyText();
    }

    /// <summary>The overlay's size setting; tracker.gg's rows are zoomed to match.</summary>
    public void SetZoom(double zoom)
    {
        _zoom = Math.Clamp(zoom, 0.75, 1.5);
        foreach (var row in _rows.Values)
        {
            row.SetZoom(_zoom);
        }
    }

    /// <summary>
    /// The lobby changed. Players who left are removed (their page is closed). New players get a row; their page only
    /// loads while the window is open.
    /// </summary>
    public void SetPlayers(IReadOnlyList<LobbyPlayer> players, bool previewing)
    {
        _previewing = previewing;
        _players = previewing ? [] : players.DistinctBy(p => p.Key).ToList();
        Rebuild();
        if (IsVisible)
        {
            StartRows();
        }
    }

    /// <summary>Shows the window without taking focus from the game (on the right of the screen the first time).</summary>
    public void ShowWithoutFocus()
    {
        var area = SystemParameters.WorkArea;
        MaxHeight = Math.Max(300, area.Height - 120);
        if (_rightEdge is null)
        {
            _rightEdge = area.Right - 24;
            _placing = true;
            Top = area.Top + 96;
            Left = Math.Max(area.Left, _rightEdge.Value - Math.Max(ActualWidth, 640));
            _placing = false;
        }

        Show();
        KeepRightEdge();

        // Load pages once the window has drawn, so opening it never stalls on starting the browser.
        Dispatcher.BeginInvoke(DispatcherPriority.ContextIdle, () =>
        {
            foreach (var row in _rows.Values)
            {
                if (row.LoadedAt != DateTime.MinValue && DateTime.UtcNow - row.LoadedAt > RefreshAfter)
                {
                    row.Reload();
                }
            }

            StartRows();
        });
    }

    /// <summary>Closes the window and its pages for good (the close button only hides it).</summary>
    public void CloseForGood()
    {
        _closing = true;
        foreach (var row in _rows.Values)
        {
            row.Dispose();
        }

        _rows.Clear();
        Close();
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        if (!_closing)
        {
            e.Cancel = true;
            Hide();
        }

        base.OnClosing(e);
    }

    private void StartRows()
    {
        foreach (var row in _rows.Values)
        {
            _ = row.StartAsync();
        }
    }

    private void Rebuild()
    {
        var label = TrackerPage.RowLabel(_playlist);
        var keep = new HashSet<string>(_players.Select(p => p.Key));
        foreach (var gone in _rows.Keys.Where(k => !keep.Contains(k)).ToList())
        {
            var row = _rows[gone];
            _rows.Remove(gone);
            RowsHost.Children.Remove(row);
            row.Dispose();
        }

        foreach (var player in _players)
        {
            if (_rows.TryGetValue(player.Key, out var row))
            {
                row.Update(player);
            }
            else
            {
                row = new TrackerRowView(player, label, _zoom, Environment, _openProfile, _logger);
                row.Changed += AlignTextBands;
                _rows[player.Key] = row;
                RowsHost.Children.Add(row);
            }
        }

        // Blue, then Orange, then players whose team isn't known yet; headers only for teams with players.
        var order = new List<UIElement>();
        void Group(TextBlock header, IEnumerable<LobbyPlayer> members)
        {
            var list = members.ToList();
            header.Visibility = list.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
            if (list.Count == 0)
            {
                return;
            }

            order.Add(header);
            order.AddRange(list.Select(p => _rows[p.Key]));
        }

        Group(_blueHeader, _players.Where(p => p.TeamNum == 0));
        Group(_orangeHeader, _players.Where(p => p.TeamNum == 1));
        Group(_otherHeader, _players.Where(p => p.TeamNum is not (0 or 1)));

        foreach (var header in new[] { _blueHeader, _orangeHeader, _otherHeader })
        {
            if (!RowsHost.Children.Contains(header))
            {
                RowsHost.Children.Add(header);
            }

            Grid.SetRow(header, 0);
        }

        while (RowsHost.RowDefinitions.Count < order.Count)
        {
            RowsHost.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        }

        while (RowsHost.RowDefinitions.Count > Math.Max(1, order.Count))
        {
            RowsHost.RowDefinitions.RemoveAt(RowsHost.RowDefinitions.Count - 1);
        }

        for (var i = 0; i < order.Count; i++)
        {
            Grid.SetRow(order[i], i);
        }

        UpdateEmptyText();
        AlignTextBands();
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

    private void UpdateEmptyText()
    {
        EmptyText.Text = _previewing
            ? "The settings Preview is running. The real lobby comes back when it ends."
            : $"No match yet. When you load into a match, every player appears here with their {TrackerPage.RowLabel(_playlist)} row from tracker.gg.";
        EmptyText.Visibility = _players.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private TextBlock TeamHeader(string text, string brush)
    {
        var header = new TextBlock { Text = string.Join(" ", text.ToCharArray()), Style = (Style)FindResource("TeamHeader") };
        header.SetResourceReference(TextBlock.ForegroundProperty, brush);
        return header;
    }

    /// <summary>The window grows to the left as rows get wider, so it stays on screen at the right edge.</summary>
    private void KeepRightEdge()
    {
        if (_rightEdge is not { } right || ActualWidth <= 0)
        {
            return;
        }

        var area = SystemParameters.WorkArea;
        _placing = true;
        Left = Math.Max(area.Left, right - ActualWidth);
        _placing = false;
    }

    private void UseDarkTitleBar()
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        var on = 1;
        _ = NativeMethods.DwmSetWindowAttribute(hwnd, NativeMethods.DWMWA_USE_IMMERSIVE_DARK_MODE, ref on, sizeof(int));
    }
}

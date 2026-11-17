using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Threading;
using Microsoft.Extensions.Logging;
using RLOverlay.App.Overlay;
using RLOverlay.App.ViewModels;
using RLOverlay.App.Views;
using RLOverlay.Core.Config;
using RLOverlay.Core.Lobby;
using RLOverlay.Core.Players;
using RLOverlay.Core.Playlists;
using RLOverlay.Core.Providers;
using Path = System.IO.Path;

namespace RLOverlay.App.Services;

/// <summary>
/// <c>--screenshots &lt;dir&gt;</c>: renders the real panel views off-screen (no window, no desktop) with mock data on a
/// neutral backdrop, so documentation images never contain anyone's screen or game artwork. Only built-in rank
/// placeholders are used, even if the machine has user rank icons installed.
/// </summary>
internal static class ScreenshotRenderer
{
    private const double RenderScale = 1.5;

    private static readonly (string Name, string PrimaryId, int Team)[] Lobby2v2 =
    [
        ("Zephyr", "Epic|7f3a9c1e2b7d4e8f9a0b1c2d3e4f5a6b|0", 0),
        ("kickoffKing", "Steam|76561198012345678|0", 0),
        ("Nova_RL", "PS4|4829173650192837465|0", 1),
        ("flipreset.exe", "XboxOne|2535412345678901|0", 1),
    ];

    private static readonly (string Name, string PrimaryId, int Team)[] Lobby3v3 =
    [
        ("Zephyr", "Epic|7f3a9c1e2b7d4e8f9a0b1c2d3e4f5a6b|0", 0),
        ("kickoffKing", "Steam|76561198012345678|0", 0),
        ("AerialAce", "Steam|76561198087654321|0", 0),
        ("SonicBoom", "Steam|76561198055555555|0", 1),
        ("flipreset.exe", "XboxOne|2535412345678901|0", 1),
        ("turtleMode", "Epic|0b1c2d3e4f5a6b7c8d9e0f1a2b3c4d5e|0", 1),
    ];

    /// <summary>
    /// Renders every scene. <paramref name="icons"/> decides which rank images appear: pass null for the built-in
    /// placeholders (what the committed docs screenshots use, so they contain no rank artwork).
    /// </summary>
    public static IReadOnlyList<string> RenderAll(string directory, ILoggerFactory loggerFactory, RankIconService? icons = null)
    {
        Directory.CreateDirectory(directory);
        var official = icons is not null;
        icons ??= new RankIconService([], Path.Combine(Path.GetTempPath(), "rloverlay-screenshots-no-cache"), null, loggerFactory.CreateLogger<RankIconService>());
        var written = new List<string>();

        void Save(string file, FrameworkElement scene) => written.Add(RenderToPng(scene, Path.Combine(directory, file)));

        // 1. Main view: Ranked Doubles, everything loaded, cursor on a name.
        {
            var vm = Scene(icons, new OverlaySettings(), Lobby2v2, Playlist.Doubles, teamSize: 2);
            Save("01_main_2v2.png", Compose(vm, strip: false, hoverFirstName: true));
        }

        // 2. Loading into the match: one lookup still running, one seat still empty.
        {
            var vm = Scene(icons, new OverlaySettings(), Lobby2v2[..3], Playlist.Doubles, teamSize: 2, loading: ["kickoffKing"]);
            Save("02_loading.png", Compose(vm, strip: false));
        }

        // 3. Ctrl+G to 3v3 in a 3v3 lobby, with Tournaments turned off in settings (struck through on the strip).
        {
            var settings = new OverlaySettings();
            settings.EnabledPlaylists.Remove(Playlist.Tournament);
            var vm = Scene(icons, settings, Lobby3v3, Playlist.Standard, teamSize: 3);
            vm.ShowSwitchedNote = true;
            Save("03_ctrl_g_3v3.png", Compose(vm, strip: true));
        }

        // 4. Casual: casual MMR with the unranked icon.
        {
            var vm = Scene(icons, new OverlaySettings(), Lobby2v2, Playlist.Casual, teamSize: 2);
            Save("04_casual.png", Compose(vm, strip: false));
        }

        // 5. Stat columns turned off in settings: the panel gets narrower.
        {
            var settings = new OverlaySettings();
            settings.Columns.Peak = settings.Columns.Shots = settings.Columns.Assists = settings.Columns.Saves = false;
            var vm = Scene(icons, settings, Lobby2v2, Playlist.Doubles, teamSize: 2);
            Save("05_columns_off.png", Compose(vm, strip: false));
        }

        // 6. Opened with Ctrl+R after kickoff (footer changes), at 125 % scale.
        {
            var vm = Scene(icons, new OverlaySettings(), Lobby2v2, Playlist.Doubles, teamSize: 2, phase: MatchPhase.Live);
            Save("06_manual_open_125.png", Compose(vm, strip: false, scale: 1.25));
        }

        // 7. The built-in rank placeholders (used when no rank icon files are installed).
        Save("07_rank_placeholders.png", IconSheet(RankIconService.Placeholder,
            "Built-in rank placeholders (shown until the official icons have downloaded, or when downloads are off)"));

        // 8. The official icons as the app resolves them (only with --official-icons; never committed).
        if (official)
        {
            Save("08_rank_icons_official.png", IconSheet(icons.Get, "Official rank icons, downloaded at runtime to the local cache"));
        }

        return written;
    }

    private static PanelViewModel Scene(
        RankIconService icons,
        OverlaySettings settings,
        (string Name, string PrimaryId, int Team)[] players,
        Playlist playlist,
        int teamSize,
        string[]? loading = null,
        MatchPhase phase = MatchPhase.PreKickoff)
    {
        var vm = new PanelViewModel(icons);
        vm.ApplySettings(settings.Normalize());
        vm.Phase = phase;
        vm.MatchTeamSize = teamSize;
        vm.Playlist = playlist;
        var now = DateTimeOffset.Now;
        foreach (var (name, primaryId, team) in players)
        {
            var id = PlatformId.Parse(primaryId, name);
            var player = new LobbyPlayer(LobbyPlayer.KeyFor(primaryId, name), name, id, team);
            vm.AddOrUpdate(player);
            if (loading?.Contains(name) != true)
            {
                vm.SetProfile(player.Key, MockStatsProvider.Build(id, now));
            }
        }

        vm.Refresh();
        return vm;
    }

    private static FrameworkElement Compose(PanelViewModel vm, bool strip, bool hoverFirstName = false, double scale = 1.0)
    {
        var content = new StackPanel { Margin = new Thickness(44, 40, 44, 48), HorizontalAlignment = HorizontalAlignment.Center };
        var panel = new PanelView { DataContext = vm };
        content.Children.Add(panel);
        if (strip)
        {
            vm.Strip.Update(vm.Playlist, vm.EnabledPlaylists);
            content.Children.Add(new PlaylistStripView
            {
                DataContext = vm.Strip,
                Margin = new Thickness(0, 12, 0, 0),
                HorizontalAlignment = HorizontalAlignment.Center,
            });
        }

        if (scale != 1.0)
        {
            content.LayoutTransform = new ScaleTransform(scale, scale);
        }

        var cursorLayer = new Canvas { IsHitTestVisible = false };
        var root = new Grid { Background = Backdrop() };
        root.Children.Add(content);
        root.Children.Add(cursorLayer);
        TextOptions.SetTextFormattingMode(root, TextFormattingMode.Ideal);
        Layout(root);

        if (hoverFirstName && FindInteractive(panel) is FrameworkElement name)
        {
            OverlayHit.SetIsHovered(name, true);
            Layout(root);
            var at = name.TransformToAncestor(root).Transform(new Point(name.ActualWidth * 0.55, name.ActualHeight * 0.7));
            var cursor = Cursor();
            Canvas.SetLeft(cursor, at.X);
            Canvas.SetTop(cursor, at.Y);
            cursorLayer.Children.Add(cursor);
            Layout(root);
        }

        return root;
    }

    private static FrameworkElement IconSheet(Func<int, ImageSource> source, string caption)
    {
        var grid = new UniformGrid { Columns = 7, Margin = new Thickness(28, 16, 28, 8) };
        foreach (var tier in Enumerable.Range(1, 21))
        {
            grid.Children.Add(IconCell(source, tier));
        }

        var bottom = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 0, 0, 20) };
        bottom.Children.Add(IconCell(source, RankTier.SupersonicLegend));
        bottom.Children.Add(IconCell(source, RankTier.Unranked));

        var title = new TextBlock
        {
            Text = caption,
            FontSize = 14,
            FontWeight = FontWeights.SemiBold,
            Foreground = (Brush)Application.Current.Resources["TextPrimary"],
            Margin = new Thickness(32, 22, 32, 0),
        };
        var stack = new StackPanel();
        stack.Children.Add(title);
        stack.Children.Add(grid);
        stack.Children.Add(bottom);

        var card = new Border
        {
            CornerRadius = new CornerRadius(12),
            Background = (Brush)Application.Current.Resources["PanelBackground"],
            BorderBrush = (Brush)Application.Current.Resources["PanelBorder"],
            BorderThickness = new Thickness(1),
            Margin = new Thickness(40),
            Child = stack,
        };
        var root = new Grid { Background = Backdrop() };
        TextElement.SetFontFamily(root, (FontFamily)Application.Current.Resources["UiFont"]);
        TextOptions.SetTextFormattingMode(root, TextFormattingMode.Ideal);
        root.Children.Add(card);
        Layout(root);
        return root;
    }

    private static FrameworkElement IconCell(Func<int, ImageSource> source, int tier)
    {
        var cell = new StackPanel { Width = 120, Margin = new Thickness(0, 10, 0, 6) };
        cell.Children.Add(new Image { Source = source(tier), Width = 48, Height = 48 });
        cell.Children.Add(new TextBlock
        {
            Text = RankTier.Name(tier),
            FontSize = 11.5,
            Foreground = (Brush)Application.Current.Resources["TextSecondary"],
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, 6, 0, 0),
        });
        return cell;
    }

    /// <summary>A plain stand-in for a game scene: dark sky over a green pitch, so the panel's translucency shows.</summary>
    private static Brush Backdrop()
    {
        var brush = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(0, 1) };
        brush.GradientStops.Add(new GradientStop(Color.FromRgb(0x10, 0x16, 0x2C), 0));
        brush.GradientStops.Add(new GradientStop(Color.FromRgb(0x1A, 0x25, 0x48), 0.55));
        brush.GradientStops.Add(new GradientStop(Color.FromRgb(0x9A, 0xA4, 0xB4), 0.552));
        brush.GradientStops.Add(new GradientStop(Color.FromRgb(0x17, 0x5C, 0x34), 0.556));
        brush.GradientStops.Add(new GradientStop(Color.FromRgb(0x0E, 0x3F, 0x24), 1));
        return brush;
    }

    private static System.Windows.Shapes.Path Cursor() => new()
    {
        Data = Geometry.Parse("M0,0 L0,18 L4.6,13.8 L7.8,21 L10.6,19.8 L7.6,12.8 L13.4,12.8 Z"),
        Fill = Brushes.White,
        Stroke = Brushes.Black,
        StrokeThickness = 1.1,
        StrokeLineJoin = PenLineJoin.Round,
    };

    private static DependencyObject? FindInteractive(DependencyObject parent)
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (OverlayHit.GetIsInteractive(child))
            {
                return child;
            }

            if (FindInteractive(child) is { } found)
            {
                return found;
            }
        }

        return null;
    }

    private static void Layout(FrameworkElement root)
    {
        for (var pass = 0; pass < 2; pass++)
        {
            root.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            root.Arrange(new Rect(root.DesiredSize));
            root.UpdateLayout();
            // Let deferred bindings (RelativeSource, ItemsControl generation) settle.
            Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
        }
    }

    private static string RenderToPng(FrameworkElement root, string path)
    {
        Layout(root);
        var width = (int)Math.Ceiling(root.ActualWidth * RenderScale);
        var height = (int)Math.Ceiling(root.ActualHeight * RenderScale);
        var bitmap = new RenderTargetBitmap(width, height, 96 * RenderScale, 96 * RenderScale, PixelFormats.Pbgra32);
        bitmap.Render(root);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(path);
        encoder.Save(stream);
        return path;
    }
}

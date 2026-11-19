using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Extensions.Logging.Abstractions;
using RLOverlay.App.ViewModels;
using RLOverlay.App.Views;
using RLOverlay.Core.Config;
using RLOverlay.Core.Lobby;
using RLOverlay.Core.Players;
using RLOverlay.Core.Playlists;
using Path = System.IO.Path;

namespace RLOverlay.App.Services;

/// <summary>
/// <c>--screenshots &lt;dir&gt;</c>: renders documentation images off-screen (no window on the desktop): the overlay
/// screen with made-up players (placeholders where tracker.gg's rows go, so no tracker.gg page or rank artwork ends
/// up in the repository), the settings window and setup.
/// </summary>
internal static class ScreenshotRenderer
{
    private const double RenderScale = 1.5;

    private static readonly (string Name, string PrimaryId, int Team)[] Lobby2v2 =
    [
        ("Gr1m", "Epic|7f3a9c1e2b7d4e8f9a0b1c2d3e4f5a6b|0", 0),
        ("Khimaira", "Steam|76561198012345678|0", 0),
        ("Rizza", "PS4|4829173650192837465|0", 1),
        ("Homebody John", "XboxOne|2535412345678901|0", 1),
    ];

    public static IReadOnlyList<string> RenderAll(string directory)
    {
        Directory.CreateDirectory(directory);
        var written = new List<string>();

        // 1. The overlay screen's layout (Preview look: made-up players, so placeholders instead of tracker.gg rows).
        written.Add(RenderToPng(Preview(), Path.Combine(directory, "01_screen_layout.png")));

        // 8 and 9. The settings window (first screen, and the whole scrolling page), with a stand-in host.
        RenderSettings(directory, written);

        // 11. The installer's first page (nothing is installed: the window is only drawn).
        RenderSetup(directory, written);
        return written;
    }

    private static FrameworkElement Preview()
    {
        var vm = new PanelViewModel { IsSample = true };
        vm.ApplySettings(new OverlaySettings().Normalize());
        vm.Phase = MatchPhase.PreKickoff;
        vm.MatchTeamSize = 2;
        vm.Playlist = Playlist.Doubles;
        foreach (var (name, primaryId, team) in Lobby2v2)
        {
            vm.AddOrUpdate(new LobbyPlayer(LobbyPlayer.KeyFor(primaryId, name), name, PlatformId.Parse(primaryId, name), team));
        }

        var panel = new TrackerPanelView(_ => { }, NullLogger.Instance) { DataContext = vm };
        var root = new Grid { Background = Backdrop() };
        root.Children.Add(new Border { Margin = new Thickness(44, 40, 44, 48), HorizontalAlignment = HorizontalAlignment.Center, Child = panel });
        TextOptions.SetTextFormattingMode(root, TextFormattingMode.Ideal);
        return root;
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

    private static void RenderSettings(string directory, List<string> written)
    {
        var window = new SettingsWindow(new SettingsViewModel(new SampleSettingsHost()))
        {
            WindowStartupLocation = WindowStartupLocation.Manual,
            Left = -20000,
            Top = -20000,
            ShowActivated = false,
            ShowInTaskbar = false,
        };
        window.Show();
        try
        {
            window.UpdateLayout();
            Settle();
            var background = (Brush)window.FindResource("WindowBackground");
            var client = (FrameworkElement)window.Content;
            written.Add(RenderVisual(client, client.ActualWidth, client.ActualHeight, background, Path.Combine(directory, "08_settings.png")));

            var page = (FrameworkElement)((ScrollViewer)((Grid)client).Children[0]).Content;
            written.Add(RenderVisual(page, page.ActualWidth, page.ActualHeight, background, Path.Combine(directory, "09_settings_full.png"), page.Margin));
        }
        finally
        {
            window.Close();
        }
    }

    private static void RenderSetup(string directory, List<string> written)
    {
        var scratch = Path.Combine(Path.GetTempPath(), "rloverlay-setup-shot-" + Guid.NewGuid().ToString("N"));
        var vm = new Setup.SetupViewModel(new Setup.Installer(Setup.InstallLocations.Sandbox(scratch)), uninstall: false,
            sourceExe: "", closeRunningApp: () => true, launch: _ => { })
        {
            InstallDir = Path.Combine(@"C:\Users\you\AppData\Local\Programs", AppInfo.Name),
        };
        var window = new Setup.SetupWindow(vm)
        {
            WindowStartupLocation = WindowStartupLocation.Manual,
            Left = -20000,
            Top = -20000,
            ShowActivated = false,
            ShowInTaskbar = false,
        };
        window.Show();
        try
        {
            window.UpdateLayout();
            Settle();
            var client = (FrameworkElement)window.Content;
            written.Add(RenderVisual(client, client.ActualWidth, client.ActualHeight, (Brush)window.FindResource("WindowBackground"),
                Path.Combine(directory, "11_setup.png")));
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>
    /// Lets a freshly shown window finish its entrance animations (switches slide to their state when they load) before
    /// it is captured; without this a screenshot could catch a switch halfway.
    /// </summary>
    private static void Settle()
    {
        var frame = new DispatcherFrame();
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(600) };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            frame.Continue = false;
        };
        timer.Start();
        Dispatcher.PushFrame(frame);
        Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
    }

    /// <summary>
    /// Renders an element in place (no re-layout) onto a solid background. The brush's viewbox is in the parent's
    /// coordinates (it includes the element's layout offset), so <paramref name="margin"/> is the element's margin.
    /// </summary>
    private static string RenderVisual(Visual visual, double width, double height, Brush background, string path, Thickness margin = default)
    {
        var totalWidth = width + margin.Left + margin.Right;
        var totalHeight = height + margin.Top + margin.Bottom;
        var area = new Rect(0, 0, totalWidth, totalHeight);
        var drawing = new DrawingVisual();
        using (var dc = drawing.RenderOpen())
        {
            dc.DrawRectangle(background, null, area);
            var brush = new VisualBrush(visual)
            {
                Stretch = Stretch.None,
                AlignmentX = AlignmentX.Left,
                AlignmentY = AlignmentY.Top,
                ViewboxUnits = BrushMappingMode.Absolute,
                Viewbox = area,
            };
            dc.DrawRectangle(brush, null, area);
        }

        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(totalWidth * RenderScale), (int)Math.Ceiling(totalHeight * RenderScale),
            96 * RenderScale, 96 * RenderScale, PixelFormats.Pbgra32);
        bitmap.Render(drawing);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(path);
        encoder.Save(stream);
        return path;
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

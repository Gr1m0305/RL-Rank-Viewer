using System.Diagnostics;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using Microsoft.Extensions.Logging;
using RLOverlay.App.Native;
using RLOverlay.Core.Config;

namespace RLOverlay.App.Overlay;

/// <summary>
/// The transparent, always-on-top, never-activating overlay window. Click-through except over player names, the
/// header and the corner grips (see <see cref="ClickThroughController"/>). Fades/slides in and out, and positions
/// itself in the top-right corner of the game's monitor; dragging the header moves it and dragging a corner resizes it.
/// </summary>
public partial class OverlayWindow : Window
{
    public const double ShadowMargin = 24;
    public static readonly Thickness ShadowMarginThickness = new(ShadowMargin);

    private static readonly TimeSpan FadeIn = TimeSpan.FromMilliseconds(240);
    private static readonly TimeSpan PopIn = TimeSpan.FromMilliseconds(420);
    private static readonly TimeSpan FadeOut = TimeSpan.FromMilliseconds(250);

    private readonly ClickThroughController _clickThrough;
    private readonly Action _dragFrame;
    private readonly DispatcherTimer _dragBackstop;
    private long _lastDragTick;
    private DragStart? _drag;
    private FrameClock? _frameClock;
    private int _framePending;
    private DispatcherPriority _framePriority = DispatcherPriority.Send;
    private (int Dx, int Dy)? _lastDelta;
    private ProcessPriorityClass? _priorityBeforeDrag;
    private Func<(int X, int Y, bool Down)>? _simulatedCursor;
    private readonly List<long> _moves = [];
    private IntPtr _hwnd;
    private Func<IntPtr>? _gameWindow;
    private double _offsetX;
    private double _offsetY;
    private bool _wantVisible;

    public OverlayWindow()
    {
        InitializeComponent();
        _clickThrough = new ClickThroughController(this, Root);
        SourceInitialized += OnSourceInitialized;
        SizeChanged += (_, _) =>
        {
            if (_drag is null) // a drag places the window itself, right after the resize it caused
            {
                Reposition();
            }
        };
        DpiChanged += (_, _) => Reposition();

        // The drag follows the cursor by polling it once per frame the screen shows (FrameClock), so it keeps going even
        // if the cursor outruns the window (a window that never activates can't capture the mouse).
        _dragFrame = () =>
        {
            Volatile.Write(ref _framePending, 0);
            OnDragTick();
        };

        // In case the frame clock ever stalls (a display that stops answering), the drag still follows the cursor and
        // still notices the button being let go.
        _dragBackstop = new DispatcherTimer(DispatcherPriority.Input, Dispatcher) { Interval = TimeSpan.FromMilliseconds(50) };
        _dragBackstop.Tick += (_, _) =>
        {
            if (Stopwatch.GetElapsedTime(_lastDragTick) > TimeSpan.FromMilliseconds(40))
            {
                OnDragTick();
            }
        };
        Root.PreviewMouseLeftButtonDown += OnMouseDown;
    }

    /// <summary>
    /// The user moved or resized the screen (drag ended): the new offset from the top-right corner in DIPs and the scale.
    /// </summary>
    public event Action<double, double, double>? PlacementChanged;

    public ILogger? Logger { get; init; }

    /// <summary>True while the user is dragging the header or a corner.</summary>
    public bool IsDragging => _drag is not null;

    /// <summary>True while shown or fading in (false while fading out or hidden).</summary>
    public bool IsShowing => _wantVisible;

    public IntPtr Handle => _hwnd;

    public HwndSource? Source => _hwnd == IntPtr.Zero ? null : HwndSource.FromHwnd(_hwnd);

    public object? Panel
    {
        get => PanelHost.Content;
        set => PanelHost.Content = value;
    }

    public object? Strip
    {
        get => StripHost.Content;
        set => StripHost.Content = value;
    }

    /// <summary>Creates the HWND without showing the window (needed for hotkeys before the first show).</summary>
    public IntPtr EnsureHandle()
    {
        if (_hwnd == IntPtr.Zero)
        {
            _hwnd = new WindowInteropHelper(this).EnsureHandle();
        }

        return _hwnd;
    }

    public void Configure(Func<IntPtr> gameWindow, double offsetX, double offsetY, double scale)
    {
        _gameWindow = gameWindow;
        _offsetX = offsetX;
        _offsetY = offsetY;
        Scale.ScaleX = Scale.ScaleY = Math.Clamp(scale, OverlaySettings.MinScale, OverlaySettings.MaxScale);
        Reposition();
    }

    /// <summary>Panel wanted on screen (lobby state / Ctrl+R).</summary>
    public bool PanelOn { get; private set; }

    /// <summary>Ctrl+G strip wanted on screen.</summary>
    public bool StripOn { get; private set; }

    /// <summary>Temporarily hidden because the game is running but another window is in front.</summary>
    public bool Suppressed { get; private set; }

    public void SetPanelVisible(bool on)
    {
        PanelOn = on;
        UpdateVisibility();
    }

    public void SetStripVisible(bool on)
    {
        var wasOn = StripOn;
        StripOn = on;
        if (on)
        {
            StripHost.Visibility = Visibility.Visible;
            if (!wasOn && RichMotion)
            {
                (StripHost.Content as IRevealable)?.PlayReveal();
            }
        }

        var fade = new DoubleAnimation(on ? 1 : 0, new Duration(on ? TimeSpan.FromMilliseconds(160) : FadeOut))
        {
            EasingFunction = new CubicEase { EasingMode = on ? EasingMode.EaseOut : EasingMode.EaseIn },
        };
        if (!on)
        {
            fade.Completed += (_, _) =>
            {
                if (!StripOn)
                {
                    StripHost.Visibility = Visibility.Hidden; // keeps its space so the window does not resize
                }
            };
        }

        StripHost.BeginAnimation(OpacityProperty, fade, HandoffBehavior.SnapshotAndReplace);
        UpdateVisibility();
    }

    /// <summary>False when the user turned off Windows animations: then the overlay only fades.</summary>
    private static bool RichMotion => SystemParameters.ClientAreaAnimation;

    public void SetSuppressed(bool suppressed)
    {
        Suppressed = suppressed;
        UpdateVisibility();
    }

    private void UpdateVisibility()
    {
        var show = (PanelOn || StripOn) && !Suppressed;
        if (show)
        {
            // Strip-only (Ctrl+G while the panel is hidden): collapse the panel. While the window fades out the panel
            // keeps its layout, so the fade is not cut short.
            var panelAppears = PanelOn && PanelArea.Visibility != Visibility.Visible;
            PanelArea.Visibility = PanelOn ? Visibility.Visible : Visibility.Collapsed;
            if (panelAppears && _wantVisible && RichMotion)
            {
                // Strip was showing alone and now the panel joins it: stagger the panel in.
                (PanelHost.Content as IRevealable)?.PlayReveal();
            }

            ShowAnimated();
        }
        else
        {
            HideAnimated();
        }
    }

    public void ShowAnimated()
    {
        if (_wantVisible)
        {
            return;
        }

        // A fresh appearance (not a re-show that interrupts a fade-out) gets the full entrance.
        var fresh = !IsVisible || Root.Opacity < 0.05;
        _wantVisible = true;
        if (!IsVisible)
        {
            Show();
        }

        Reposition();
        EnsureTopmost();
        AnimateIn(fresh);
        _clickThrough.Start();
    }

    public void HideAnimated()
    {
        if (!_wantVisible)
        {
            return;
        }

        _wantVisible = false;
        EndDrag(commit: true);
        _clickThrough.Stop();
        AnimateOut(() =>
        {
            if (!_wantVisible)
            {
                Hide();
            }
        });
    }

    /// <summary>Re-asserts HWND_TOPMOST (borderless games can end up above us after focus changes).</summary>
    public void EnsureTopmost()
    {
        if (_hwnd != IntPtr.Zero)
        {
            NativeMethods.SetWindowPos(_hwnd, NativeMethods.HWND_TOPMOST, 0, 0, 0, 0,
                NativeMethods.SWP_NOMOVE | NativeMethods.SWP_NOSIZE | NativeMethods.SWP_NOACTIVATE | NativeMethods.SWP_NOOWNERZORDER);
        }
    }

    /// <summary>Top-right corner of the game's monitor (primary monitor if the game is not found), plus the user offset.</summary>
    public void Reposition()
    {
        if (_hwnd == IntPtr.Zero || ActualWidth <= 0)
        {
            return;
        }

        Place(Monitor(), VisualTreeHelper.GetDpi(this).DpiScaleX);
    }

    /// <summary>
    /// Puts the window at the current offset on <paramref name="monitor"/>. A drag passes the monitor and DPI it read
    /// when it started, so a move costs one <c>SetWindowPos</c> (looking for the game window can mean listing every
    /// process when the game isn't running).
    /// </summary>
    private void Place(PixelRect monitor, double dpi)
    {
        var width = (int)Math.Ceiling(ActualWidth * dpi);
        var height = (int)Math.Ceiling(ActualHeight * dpi);
        var (x, y) = PanelPlacement.TopRight(monitor, width, height, _offsetX, _offsetY, dpi, (int)Math.Round(ShadowMargin * dpi));
        NativeMethods.SetWindowPos(_hwnd, NativeMethods.HWND_TOPMOST, x, y, 0, 0,
            NativeMethods.SWP_NOSIZE | NativeMethods.SWP_NOACTIVATE | NativeMethods.SWP_NOOWNERZORDER);
        if (_drag is not null)
        {
            _moves.Add(Stopwatch.GetTimestamp());
        }
    }

    private PixelRect Monitor()
    {
        var game = _gameWindow?.Invoke() ?? IntPtr.Zero;
        return game != IntPtr.Zero ? MonitorInfo.BoundsFor(game) : MonitorInfo.BoundsFor(IntPtr.Zero);
    }

    /// <summary>Where a drag started: cursor, window rectangle (physical pixels), scale and what is being dragged.</summary>
    private sealed record DragStart(
        OverlayDrag Kind, int CursorX, int CursorY, NativeMethods.RECT Window, PixelRect Monitor, double Dpi, int Shadow, double Scale);

    private void OnMouseDown(object sender, MouseButtonEventArgs e)
    {
        var kind = OverlayDrag.None;
        for (var d = e.OriginalSource as DependencyObject; d is not null && kind == OverlayDrag.None; d = Parent(d))
        {
            kind = OverlayHit.GetDrag(d);
        }

        if (kind == OverlayDrag.None || _hwnd == IntPtr.Zero
            || !NativeMethods.GetCursorPos(out var cursor) || !NativeMethods.GetWindowRect(_hwnd, out var rect))
        {
            return;
        }

        e.Handled = true;
        BeginDrag(kind, cursor.X, cursor.Y, rect);

        static DependencyObject? Parent(DependencyObject d) =>
            d is Visual or System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(d) : LogicalTreeHelper.GetParent(d);
    }

    private void BeginDrag(OverlayDrag kind, int cursorX, int cursorY, NativeMethods.RECT rect)
    {
        var dpi = VisualTreeHelper.GetDpi(this).DpiScaleX;
        _drag = new DragStart(kind, cursorX, cursorY, rect, Monitor(), dpi, (int)Math.Round(ShadowMargin * dpi), Scale.ScaleX);
        _lastDelta = null;
        _moves.Clear();

        // A move is one SetWindowPos, so it goes ahead of everything. A resize lays the whole panel out again; at the
        // render priority it takes turns with WPF's own repaint, so the panel keeps redrawing while it grows.
        _framePriority = kind == OverlayDrag.Move ? DispatcherPriority.Send : DispatcherPriority.Render;
        _priorityBeforeDrag = RaisePriority();
        _lastDragTick = Stopwatch.GetTimestamp();
        _frameClock = FrameClock.Start(_hwnd, OnFrame, Logger);
        _dragBackstop.Start();
    }

    /// <summary>
    /// On the frame clock's thread, once per refresh: asks the UI thread to follow the cursor, ahead of any other queued
    /// work. At most one request waits at a time, so a busy UI thread skips frames instead of falling behind.
    /// </summary>
    private void OnFrame()
    {
        if (Interlocked.Exchange(ref _framePending, 1) == 0)
        {
            Dispatcher.BeginInvoke(_framePriority, _dragFrame);
        }
    }

    /// <summary>
    /// The app runs below normal priority so the game gets the CPU first (see App.OnStartup). While the user drags the
    /// panel it is what they are looking at, so it runs above normal until they let go.
    /// </summary>
    private ProcessPriorityClass? RaisePriority()
    {
        try
        {
            using var self = Process.GetCurrentProcess();
            var before = self.PriorityClass;
            if (before is ProcessPriorityClass.BelowNormal or ProcessPriorityClass.Idle or ProcessPriorityClass.Normal)
            {
                self.PriorityClass = ProcessPriorityClass.AboveNormal;
                return before;
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            Logger?.LogDebug(ex, "Could not raise the priority for a drag");
        }

        return null;
    }

    private void RestorePriority()
    {
        if (_priorityBeforeDrag is not { } before)
        {
            return;
        }

        _priorityBeforeDrag = null;
        try
        {
            using var self = Process.GetCurrentProcess();
            self.PriorityClass = before;
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            Logger?.LogDebug(ex, "Could not restore the priority after a drag");
        }
    }

    /// <summary>
    /// Testing (<c>--simulate-drag</c>): drags the panel by its header for <paramref name="duration"/> with a made-up
    /// cursor (the real mouse is never moved): side to side at a steady 1200 px/s (600 px each way, starting to the
    /// left) while going down and up at 400 px/s, then lets go and logs how often the panel moved. With
    /// <paramref name="resize"/> the bottom-left corner is dragged the same way instead, so the panel grows and shrinks.
    /// </summary>
    internal void SimulateDrag(TimeSpan duration, bool resize = false)
    {
        if (_hwnd == IntPtr.Zero || _drag is not null || !IsVisible || !NativeMethods.GetWindowRect(_hwnd, out var rect))
        {
            Logger?.LogWarning("Simulated drag: the panel isn't on screen");
            return;
        }

        var inset = (int)Math.Round((ShadowMargin + 20) * VisualTreeHelper.GetDpi(this).DpiScaleX);
        var x0 = resize ? rect.Left + inset : rect.Left + (rect.Width / 2);
        var y0 = resize ? rect.Bottom - inset : rect.Top + inset;
        var clock = Stopwatch.StartNew();
        _simulatedCursor = () =>
        {
            var t = clock.Elapsed.TotalSeconds;
            var (dx, dy) = SimulatedPath(t);
            return (x0 + dx, y0 + dy, t < duration.TotalSeconds);
        };
        Logger?.LogInformation("Simulated {Kind} for {Seconds} s from {X}, {Y}", resize ? "resize" : "drag", duration.TotalSeconds, x0, y0);
        BeginDrag(resize ? OverlayDrag.BottomLeft : OverlayDrag.Move, x0, y0, rect);
    }

    /// <summary>The simulated cursor's offset after <paramref name="t"/> seconds: a zig-zag at constant speed.</summary>
    internal static (int Dx, int Dy) SimulatedPath(double t)
    {
        static double Triangle(double t, double speed, double reach)
        {
            var travel = (t * speed) % (2 * reach);
            return travel <= reach ? travel : (2 * reach) - travel;
        }

        return (-(int)Math.Round(Triangle(t, 1200, 600)), (int)Math.Round(Triangle(t, 400, 200)));
    }

    private bool ReadCursor(out int x, out int y, out bool down)
    {
        if (_simulatedCursor is { } simulated)
        {
            (x, y, down) = simulated();
            return true;
        }

        down = NativeMethods.PrimaryButtonDown();
        var ok = NativeMethods.GetCursorPos(out var p);
        (x, y) = (p.X, p.Y);
        return ok;
    }

    private void OnDragTick()
    {
        if (_drag is not { } start)
        {
            return; // a frame that was already queued when the drag ended
        }

        _lastDragTick = Stopwatch.GetTimestamp();

        if (ReadCursor(out var x, out var y, out var down))
        {
            var delta = (x - start.CursorX, y - start.CursorY);
            if (delta != _lastDelta)
            {
                _lastDelta = delta;
                DragTo(start, delta.Item1, delta.Item2);
            }
        }

        if (!down)
        {
            EndDrag(commit: true);
        }
    }

    private void EndDrag(bool commit)
    {
        if (_drag is null)
        {
            return;
        }

        var simulated = _simulatedCursor is not null;
        var clock = _frameClock?.Source;
        _drag = null;
        _simulatedCursor = null;
        _frameClock?.Dispose();
        _frameClock = null;
        _dragBackstop.Stop();
        RestorePriority();
        ReportDrag(simulated, clock);
        if (!commit)
        {
            return;
        }

        if (NativeMethods.GetWindowRect(_hwnd, out var rect))
        {
            // Save where it actually is: a drag past the monitor's edge stops at the edge.
            var dpi = VisualTreeHelper.GetDpi(this).DpiScaleX;
            var (x, y) = PanelPlacement.OffsetFor(Monitor(), rect.Width, rect.Left, rect.Top, dpi, (int)Math.Round(ShadowMargin * dpi));
            (_offsetX, _offsetY) = (Math.Round(x), Math.Round(y));
        }

        PlacementChanged?.Invoke(_offsetX, _offsetY, Scale.ScaleX);
    }

    /// <summary>
    /// Moves the window by (<paramref name="dx"/>, <paramref name="dy"/>) physical pixels from where the drag started,
    /// or resizes it from the dragged corner: the scale follows the cursor along the diagonal, the opposite corner stays
    /// where it was, and the size stays within <see cref="OverlaySettings.MinScale"/> to <see cref="OverlaySettings.MaxScale"/>.
    /// </summary>
    private void DragTo(DragStart start, int dx, int dy)
    {
        var w0 = start.Window.Width;
        var h0 = start.Window.Height;
        if (start.Kind == OverlayDrag.Move)
        {
            (_offsetX, _offsetY) = PanelPlacement.OffsetFor(start.Monitor, w0, start.Window.Left + dx, start.Window.Top + dy, start.Dpi, start.Shadow);
            Place(start.Monitor, start.Dpi);
            return;
        }

        var (scale, to) = PanelPlacement.ResizeFromCorner(
            new PixelRect(start.Window.Left, start.Window.Top, w0, h0),
            start.Shadow,
            start.Scale,
            left: start.Kind is OverlayDrag.TopLeft or OverlayDrag.BottomLeft,
            top: start.Kind is OverlayDrag.TopLeft or OverlayDrag.TopRight,
            dx,
            dy,
            OverlaySettings.MinScale,
            OverlaySettings.MaxScale);
        (_offsetX, _offsetY) = PanelPlacement.OffsetFor(start.Monitor, to.Width, to.Left, to.Top, start.Dpi, start.Shadow);
        if (Math.Abs(Scale.ScaleX - scale) > 0.0005)
        {
            Scale.ScaleX = Scale.ScaleY = scale;
            UpdateLayout(); // resize now, so the move below uses the new size
        }

        Place(start.Monitor, start.Dpi);
    }

    /// <summary>How often the window moved during the drag (Information for a simulated drag, else Debug).</summary>
    private void ReportDrag(bool simulated, string? clock)
    {
        if (_moves.Count < 2 || Logger is null)
        {
            return;
        }

        var gaps = new double[_moves.Count - 1];
        for (var i = 1; i < _moves.Count; i++)
        {
            gaps[i - 1] = (_moves[i] - _moves[i - 1]) * 1000.0 / Stopwatch.Frequency;
        }

        Array.Sort(gaps);
        var seconds = (_moves[^1] - _moves[0]) / (double)Stopwatch.Frequency;
        Logger.Log(
            simulated ? LogLevel.Information : LogLevel.Debug,
            "Drag: {Moves} moves in {Seconds:0.00} s ({Rate:0} per second, clock: {Clock}), gap median {Median:0.0} ms, 95% {P95:0.0} ms, worst {Worst:0.0} ms",
            _moves.Count,
            seconds,
            (_moves.Count - 1) / Math.Max(seconds, 0.001),
            clock,
            gaps[gaps.Length / 2],
            gaps[(int)(gaps.Length * 0.95)],
            gaps[^1]);
        _moves.Clear();
    }

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        _hwnd = new WindowInteropHelper(this).Handle;
        var style = NativeMethods.GetExtendedStyle(_hwnd);
        style |= NativeMethods.WS_EX_LAYERED | NativeMethods.WS_EX_TOOLWINDOW | NativeMethods.WS_EX_NOACTIVATE
                 | NativeMethods.WS_EX_TRANSPARENT | NativeMethods.WS_EX_TOPMOST;
        NativeMethods.SetExtendedStyle(_hwnd, style);
        HwndSource.FromHwnd(_hwnd)?.AddHook(WndProc);
        _clickThrough.Attach(_hwnd);
    }

    private static IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == NativeMethods.WM_MOUSEACTIVATE)
        {
            // Clicking a name must never take focus away from the game.
            handled = true;
            return new IntPtr(NativeMethods.MA_NOACTIVATE);
        }

        return IntPtr.Zero;
    }

    /// <summary>
    /// Entrance: fade in while the panel drops 16 px into place and springs from 96 % to full size (slight overshoot),
    /// then the panel's sections cascade in (<see cref="IRevealable"/>).
    /// </summary>
    private void AnimateIn(bool fresh)
    {
        var easeOut = new CubicEase { EasingMode = EasingMode.EaseOut };
        Root.BeginAnimation(OpacityProperty, new DoubleAnimation(1, new Duration(FadeIn)) { EasingFunction = easeOut }, HandoffBehavior.SnapshotAndReplace);

        if (!RichMotion)
        {
            Pop.BeginAnimation(ScaleTransform.ScaleXProperty, null);
            Pop.BeginAnimation(ScaleTransform.ScaleYProperty, null);
            Slide.BeginAnimation(TranslateTransform.YProperty, null);
            return;
        }

        var duration = new Duration(PopIn);
        var spring = new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = 0.35 };
        var slide = fresh ? new DoubleAnimation(-16, 0, duration) : new DoubleAnimation(0, duration);
        slide.EasingFunction = easeOut;
        var pop = fresh ? new DoubleAnimation(0.96, 1, duration) : new DoubleAnimation(1, duration);
        pop.EasingFunction = spring;
        Slide.BeginAnimation(TranslateTransform.YProperty, slide, HandoffBehavior.SnapshotAndReplace);
        Pop.BeginAnimation(ScaleTransform.ScaleXProperty, pop, HandoffBehavior.SnapshotAndReplace);
        Pop.BeginAnimation(ScaleTransform.ScaleYProperty, pop, HandoffBehavior.SnapshotAndReplace);

        if (fresh && PanelArea.Visibility == Visibility.Visible)
        {
            (PanelHost.Content as IRevealable)?.PlayReveal();
        }
    }

    /// <summary>Exit (kickoff, Ctrl+R): fade out over 250 ms while lifting 10 px and shrinking to 97 %.</summary>
    private void AnimateOut(Action completed)
    {
        var easeIn = new CubicEase { EasingMode = EasingMode.EaseIn };
        var duration = new Duration(FadeOut);
        var fade = new DoubleAnimation(0, duration) { EasingFunction = easeIn };
        fade.Completed += (_, _) => completed();
        Root.BeginAnimation(OpacityProperty, fade, HandoffBehavior.SnapshotAndReplace);

        if (RichMotion)
        {
            var lift = new DoubleAnimation(-10, duration) { EasingFunction = easeIn };
            var shrink = new DoubleAnimation(0.97, duration) { EasingFunction = easeIn };
            Slide.BeginAnimation(TranslateTransform.YProperty, lift, HandoffBehavior.SnapshotAndReplace);
            Pop.BeginAnimation(ScaleTransform.ScaleXProperty, shrink, HandoffBehavior.SnapshotAndReplace);
            Pop.BeginAnimation(ScaleTransform.ScaleYProperty, shrink, HandoffBehavior.SnapshotAndReplace);
        }
    }
}

/// <summary>Overlay content that can play its own entrance (staggered sections, pop-in).</summary>
public interface IRevealable
{
    void PlayReveal();
}

using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using RLOverlay.App.Native;
using RLOverlay.Core.Config;

namespace RLOverlay.App.Overlay;

/// <summary>
/// The transparent, always-on-top, never-activating overlay window. Click-through except over player names
/// (see <see cref="ClickThroughController"/>). Fades/slides in and out, and positions itself top-centre on the
/// game's monitor.
/// </summary>
public partial class OverlayWindow : Window
{
    public const double ShadowMargin = 24;
    public static readonly Thickness ShadowMarginThickness = new(ShadowMargin);

    private static readonly TimeSpan FadeIn = TimeSpan.FromMilliseconds(240);
    private static readonly TimeSpan PopIn = TimeSpan.FromMilliseconds(420);
    private static readonly TimeSpan FadeOut = TimeSpan.FromMilliseconds(250);

    private readonly ClickThroughController _clickThrough;
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
        SizeChanged += (_, _) => Reposition();
        DpiChanged += (_, _) => Reposition();
    }

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
            var panelAppears = PanelOn && PanelHost.Visibility != Visibility.Visible;
            PanelHost.Visibility = PanelOn ? Visibility.Visible : Visibility.Collapsed;
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

    /// <summary>Top-centre of the game's monitor (primary monitor if the game is not found), plus the user offset.</summary>
    public void Reposition()
    {
        if (_hwnd == IntPtr.Zero || ActualWidth <= 0)
        {
            return;
        }

        var game = _gameWindow?.Invoke() ?? IntPtr.Zero;
        var monitor = game != IntPtr.Zero ? MonitorInfo.BoundsFor(game) : MonitorInfo.BoundsFor(IntPtr.Zero);
        var dpi = VisualTreeHelper.GetDpi(this).DpiScaleX;
        var width = (int)Math.Ceiling(ActualWidth * dpi);
        var height = (int)Math.Ceiling(ActualHeight * dpi);
        var (x, y) = PanelPlacement.TopCenter(monitor, width, height, _offsetX, _offsetY, dpi, (int)Math.Round(ShadowMargin * dpi));
        NativeMethods.SetWindowPos(_hwnd, NativeMethods.HWND_TOPMOST, x, y, 0, 0,
            NativeMethods.SWP_NOSIZE | NativeMethods.SWP_NOACTIVATE | NativeMethods.SWP_NOOWNERZORDER);
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

        if (fresh && PanelHost.Visibility == Visibility.Visible)
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

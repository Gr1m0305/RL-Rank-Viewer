using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using RLOverlay.App.Native;

namespace RLOverlay.App.Overlay;

/// <summary>
/// Keeps the overlay click-through everywhere except over player names.
/// <c>WS_EX_TRANSPARENT</c> is on by default; while the panel is visible a ~30 Hz timer reads the cursor position,
/// hit-tests the WPF tree, and removes <c>WS_EX_TRANSPARENT</c> only while the cursor is over an element marked
/// <see cref="OverlayHit.IsInteractiveProperty"/>. The window stays non-activating throughout.
/// </summary>
internal sealed class ClickThroughController
{
    private readonly Window _window;
    private readonly FrameworkElement _root;
    private readonly DispatcherTimer _timer;
    private IntPtr _hwnd;
    private bool _clickable;
    private DependencyObject? _hovered;

    public ClickThroughController(Window window, FrameworkElement root)
    {
        _window = window;
        _root = root;
        _timer = new DispatcherTimer(DispatcherPriority.Input, window.Dispatcher)
        {
            Interval = TimeSpan.FromMilliseconds(33),
        };
        _timer.Tick += (_, _) => Poll();
    }

    /// <summary>The interactive element under the cursor, if any (for tests/diagnostics).</summary>
    public DependencyObject? Hovered => _hovered;

    public void Attach(IntPtr hwnd) => _hwnd = hwnd;

    public void Start()
    {
        if (!_timer.IsEnabled)
        {
            _timer.Start();
        }
    }

    public void Stop()
    {
        _timer.Stop();
        SetHovered(null);
        SetClickable(false);
    }

    private void Poll()
    {
        if (_hwnd == IntPtr.Zero || !_window.IsVisible || PresentationSource.FromVisual(_root) is null)
        {
            SetHovered(null);
            SetClickable(false);
            return;
        }

        DependencyObject? target = null;
        if (NativeMethods.GetCursorPos(out var p))
        {
            var local = _root.PointFromScreen(new Point(p.X, p.Y));
            if (local.X >= 0 && local.Y >= 0 && local.X <= _root.ActualWidth && local.Y <= _root.ActualHeight)
            {
                target = FindInteractive(local);
            }
        }

        SetHovered(target);
        SetClickable(target is not null);
    }

    private DependencyObject? FindInteractive(Point local)
    {
        DependencyObject? found = null;
        VisualTreeHelper.HitTest(
            _root,
            null,
            result =>
            {
                for (var d = result.VisualHit; d is not null && !ReferenceEquals(d, _root); d = VisualTreeHelper.GetParent(d))
                {
                    if (OverlayHit.GetIsInteractive(d) && d is UIElement { IsVisible: true, IsEnabled: true })
                    {
                        found = d;
                        return HitTestResultBehavior.Stop;
                    }
                }

                return HitTestResultBehavior.Continue;
            },
            new PointHitTestParameters(local));
        return found;
    }

    private void SetHovered(DependencyObject? target)
    {
        if (ReferenceEquals(target, _hovered))
        {
            return;
        }

        if (_hovered is not null)
        {
            OverlayHit.SetIsHovered(_hovered, false);
        }

        _hovered = target;
        if (target is not null)
        {
            OverlayHit.SetIsHovered(target, true);
        }
    }

    private void SetClickable(bool clickable)
    {
        if (clickable == _clickable || _hwnd == IntPtr.Zero)
        {
            return;
        }

        _clickable = clickable;
        var style = NativeMethods.GetExtendedStyle(_hwnd);
        style = clickable ? style & ~NativeMethods.WS_EX_TRANSPARENT : style | NativeMethods.WS_EX_TRANSPARENT;
        NativeMethods.SetExtendedStyle(_hwnd, style);
    }
}

using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace RLOverlay.App.Views;

/// <summary>
/// A rounded placeholder bar with a light band sweeping across it. The animation runs only while the bar is visible,
/// so a hidden overlay costs nothing, and at a low frame rate: each frame redraws the whole layered overlay window.
/// </summary>
public sealed class SkeletonBar : Border
{
    private static readonly Color Base = Color.FromArgb(0x2E, 0xFF, 0xFF, 0xFF);
    private static readonly Color Highlight = Color.FromArgb(0x55, 0xFF, 0xFF, 0xFF);
    private static readonly TimeSpan Period = TimeSpan.FromMilliseconds(1600);
    private const int FramesPerSecond = 15;

    private readonly TranslateTransform _shift = new(-1, 0);
    private bool _running;

    public SkeletonBar()
    {
        CornerRadius = new CornerRadius(4);
        Background = new LinearGradientBrush
        {
            StartPoint = new Point(0, 0.5),
            EndPoint = new Point(1, 0.5),
            MappingMode = BrushMappingMode.RelativeToBoundingBox,
            SpreadMethod = GradientSpreadMethod.Pad,
            RelativeTransform = _shift,
            GradientStops =
            [
                new GradientStop(Base, 0),
                new GradientStop(Base, 0.3),
                new GradientStop(Highlight, 0.5),
                new GradientStop(Base, 0.7),
                new GradientStop(Base, 1),
            ],
        };
        IsVisibleChanged += (_, _) => UpdateAnimation();
        Loaded += (_, _) => UpdateAnimation();
        Unloaded += (_, _) => UpdateAnimation();
    }

    private void UpdateAnimation()
    {
        var run = IsLoaded && IsVisible;
        if (run == _running)
        {
            return;
        }

        _running = run;
        if (run)
        {
            // Start every bar at the same phase of a shared clock so the sweep lines up across the panel.
            var phase = TimeSpan.FromTicks(Environment.TickCount64 * TimeSpan.TicksPerMillisecond % Period.Ticks);
            var sweep = new DoubleAnimation(-1, 1, new Duration(Period))
            {
                RepeatBehavior = RepeatBehavior.Forever,
                BeginTime = -phase,
            };
            Timeline.SetDesiredFrameRate(sweep, FramesPerSecond);
            _shift.BeginAnimation(TranslateTransform.XProperty, sweep);
        }
        else
        {
            _shift.BeginAnimation(TranslateTransform.XProperty, null);
        }
    }
}

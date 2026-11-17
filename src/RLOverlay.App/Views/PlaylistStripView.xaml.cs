using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using RLOverlay.App.Overlay;

namespace RLOverlay.App.Views;

public partial class PlaylistStripView : UserControl, IRevealable
{
    private readonly ScaleTransform _pop = new(1, 1);
    private readonly TranslateTransform _rise = new();

    public PlaylistStripView()
    {
        InitializeComponent();
        RenderTransformOrigin = new Point(0.5, 0.5);
        RenderTransform = new TransformGroup { Children = { _pop, _rise } };
    }

    /// <summary>The strip pops up from slightly below with a small spring.</summary>
    public void PlayReveal()
    {
        var duration = new Duration(TimeSpan.FromMilliseconds(360));
        var spring = new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = 0.45 };
        var pop = new DoubleAnimation(0.9, 1, duration) { EasingFunction = spring };
        _pop.BeginAnimation(ScaleTransform.ScaleXProperty, pop);
        _pop.BeginAnimation(ScaleTransform.ScaleYProperty, pop);
        _rise.BeginAnimation(TranslateTransform.YProperty,
            new DoubleAnimation(10, 0, duration) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });
    }
}

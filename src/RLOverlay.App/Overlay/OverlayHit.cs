using System.Windows;

namespace RLOverlay.App.Overlay;

/// <summary>
/// Attached properties for the click-through overlay. Elements marked <c>IsInteractive</c> (player names) are the only
/// places where the overlay accepts the mouse; <c>IsHovered</c> is set by <see cref="ClickThroughController"/> while the
/// cursor is over one, so styles can underline the name and show the link arrow.
/// </summary>
public static class OverlayHit
{
    public static readonly DependencyProperty IsInteractiveProperty = DependencyProperty.RegisterAttached(
        "IsInteractive", typeof(bool), typeof(OverlayHit), new PropertyMetadata(false));

    public static readonly DependencyProperty IsHoveredProperty = DependencyProperty.RegisterAttached(
        "IsHovered", typeof(bool), typeof(OverlayHit), new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.Inherits));

    public static bool GetIsInteractive(DependencyObject d) => (bool)d.GetValue(IsInteractiveProperty);

    public static void SetIsInteractive(DependencyObject d, bool value) => d.SetValue(IsInteractiveProperty, value);

    public static bool GetIsHovered(DependencyObject d) => (bool)d.GetValue(IsHoveredProperty);

    public static void SetIsHovered(DependencyObject d, bool value) => d.SetValue(IsHoveredProperty, value);
}

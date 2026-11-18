using System.Windows;

namespace RLOverlay.App.Overlay;

/// <summary>What dragging an element of the overlay does: move the screen, or resize it from that corner.</summary>
public enum OverlayDrag
{
    None,
    Move,
    TopLeft,
    TopRight,
    BottomLeft,
    BottomRight,
}

/// <summary>
/// Attached properties for the click-through overlay. Elements marked <c>IsInteractive</c> (player names, the header,
/// the corner grips) are the only places where the overlay accepts the mouse; <c>IsHovered</c> is set by
/// <see cref="ClickThroughController"/> while the cursor is over one, so styles can underline the name or light up the
/// grip. <c>Drag</c> makes an element a handle for moving or resizing the screen.
/// </summary>
public static class OverlayHit
{
    public static readonly DependencyProperty DragProperty = DependencyProperty.RegisterAttached(
        "Drag", typeof(OverlayDrag), typeof(OverlayHit), new PropertyMetadata(OverlayDrag.None));

    public static OverlayDrag GetDrag(DependencyObject d) => (OverlayDrag)d.GetValue(DragProperty);

    public static void SetDrag(DependencyObject d, OverlayDrag value) => d.SetValue(DragProperty, value);

    public static readonly DependencyProperty IsInteractiveProperty = DependencyProperty.RegisterAttached(
        "IsInteractive", typeof(bool), typeof(OverlayHit), new PropertyMetadata(false));

    public static readonly DependencyProperty IsHoveredProperty = DependencyProperty.RegisterAttached(
        "IsHovered", typeof(bool), typeof(OverlayHit), new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.Inherits));

    public static bool GetIsInteractive(DependencyObject d) => (bool)d.GetValue(IsInteractiveProperty);

    public static void SetIsInteractive(DependencyObject d, bool value) => d.SetValue(IsInteractiveProperty, value);

    public static bool GetIsHovered(DependencyObject d) => (bool)d.GetValue(IsHoveredProperty);

    public static void SetIsHovered(DependencyObject d, bool value) => d.SetValue(IsHoveredProperty, value);
}

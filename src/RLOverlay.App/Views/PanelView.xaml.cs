using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using RLOverlay.App.Overlay;
using RLOverlay.App.ViewModels;

namespace RLOverlay.App.Views;

public partial class PanelView : UserControl, IRevealable
{
    private static readonly TimeSpan RiseDuration = TimeSpan.FromMilliseconds(320);
    private static readonly TimeSpan Stagger = TimeSpan.FromMilliseconds(55);

    private PanelViewModel? _vm;

    public PanelView()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
    }

    /// <summary>A player name was clicked.</summary>
    public event Action<PlayerRowViewModel>? ProfileRequested;

    /// <summary>Header, blue team, orange team and footer rise into place one after another.</summary>
    public void PlayReveal()
    {
        var delay = TimeSpan.FromMilliseconds(40);
        foreach (var section in new FrameworkElement[] { HeaderRow, BlueHost, OrangeHost, FooterRow })
        {
            Rise(section, delay, fromY: 8, fromOpacity: 0);
            delay += Stagger;
        }
    }

    /// <summary>Ctrl+G: the team blocks dip and come back so the change of numbers reads as a switch.</summary>
    private void PlayPlaylistSwitch()
    {
        if (!IsVisible || !SystemParameters.ClientAreaAnimation)
        {
            return;
        }

        Rise(BlueHost, TimeSpan.Zero, fromY: 4, fromOpacity: 0.25);
        Rise(OrangeHost, TimeSpan.FromMilliseconds(45), fromY: 4, fromOpacity: 0.25);
    }

    /// <summary>
    /// Fades/slides an element in after <paramref name="delay"/>. Key frames hold the start values during the delay,
    /// so nothing flashes before its turn, and the element's own property values are never changed.
    /// </summary>
    private static void Rise(FrameworkElement element, TimeSpan delay, double fromY, double fromOpacity)
    {
        if (element.RenderTransform is not TranslateTransform shift)
        {
            shift = new TranslateTransform();
            element.RenderTransform = shift;
        }

        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        element.BeginAnimation(OpacityProperty, Keyframes(fromOpacity, 1, delay, ease));
        shift.BeginAnimation(TranslateTransform.YProperty, Keyframes(fromY, 0, delay, ease));
    }

    private static DoubleAnimationUsingKeyFrames Keyframes(double from, double to, TimeSpan delay, IEasingFunction ease)
    {
        var animation = new DoubleAnimationUsingKeyFrames();
        animation.KeyFrames.Add(new DiscreteDoubleKeyFrame(from, KeyTime.FromTimeSpan(TimeSpan.Zero)));
        animation.KeyFrames.Add(new DiscreteDoubleKeyFrame(from, KeyTime.FromTimeSpan(delay)));
        animation.KeyFrames.Add(new EasingDoubleKeyFrame(to, KeyTime.FromTimeSpan(delay + RiseDuration), ease));
        return animation;
    }

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (_vm is not null)
        {
            _vm.PropertyChanged -= OnViewModelPropertyChanged;
        }

        _vm = DataContext as PanelViewModel;
        if (_vm is not null)
        {
            _vm.PropertyChanged += OnViewModelPropertyChanged;
        }
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(PanelViewModel.Playlist))
        {
            PlayPlaylistSwitch();
        }
    }

    private void OnNameClicked(object sender, MouseButtonEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: PlayerRowViewModel { CanOpenProfile: true } row })
        {
            e.Handled = true;
            ProfileRequested?.Invoke(row);
        }
    }
}

using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using RLOverlay.App.ViewModels;

namespace RLOverlay.App.Views;

public partial class PanelView : UserControl
{
    public PanelView()
    {
        InitializeComponent();
    }

    /// <summary>A player name was clicked.</summary>
    public event Action<PlayerRowViewModel>? ProfileRequested;

    private void OnNameClicked(object sender, MouseButtonEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: PlayerRowViewModel { CanOpenProfile: true } row })
        {
            e.Handled = true;
            ProfileRequested?.Invoke(row);
        }
    }
}

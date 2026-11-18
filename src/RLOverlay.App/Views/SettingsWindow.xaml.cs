using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using RLOverlay.App.Native;
using RLOverlay.App.ViewModels;
using RLOverlay.Core.Config;

namespace RLOverlay.App.Views;

public partial class SettingsWindow : Window
{
    private const string DefaultShortcutHint =
        "Click a shortcut, then press the new keys (Ctrl, Alt, Shift or Win plus a key). They only work while Rocket League is in front.";

    private readonly SettingsViewModel _vm;
    private Button? _recordingButton;

    public SettingsWindow(SettingsViewModel vm)
    {
        InitializeComponent();
        DataContext = _vm = vm;
        vm.CloseRequested += _ => Close();
        Closed += (_, _) => vm.OnClosed();
        SourceInitialized += (_, _) => UseDarkTitleBar();
        PlaceAwayFromOverlay();
    }

    /// <summary>
    /// The overlay sits at the top centre of the screen, so open in the lower right where it covers the least of it.
    /// </summary>
    private void PlaceAwayFromOverlay()
    {
        var area = SystemParameters.WorkArea;
        Height = Math.Min(Height, area.Height - 32);
        WindowStartupLocation = WindowStartupLocation.Manual;
        Left = Math.Max(area.Left + 16, area.Right - Width - 24);
        Top = Math.Max(area.Top + 16, area.Bottom - Height - 24);
    }

    private void UseDarkTitleBar()
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        var on = 1;
        _ = NativeMethods.DwmSetWindowAttribute(hwnd, NativeMethods.DWMWA_USE_IMMERSIVE_DARK_MODE, ref on, sizeof(int));
    }

    private string Target(Button button) => button == ToggleKeyField ? "toggle" : "cycle";

    private void OnShortcutClicked(object sender, RoutedEventArgs e)
    {
        var button = (Button)sender;
        if (_recordingButton == button)
        {
            return;
        }

        StopRecording();
        _recordingButton = button;
        button.Tag = "recording";
        button.ContentTemplate = (DataTemplate)Resources["RecordingContent"];
        ShortcutHint.Text = "Press the new shortcut now (Esc cancels).";
        _vm.StartRecording(Target(button));
        button.Focus();
    }

    private void OnShortcutKeyDown(object sender, KeyEventArgs e)
    {
        if (sender != _recordingButton)
        {
            return; // Space/Enter on a focused field start recording via Click
        }

        e.Handled = true;
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (key == Key.Escape)
        {
            StopRecording();
            return;
        }

        if (key is Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt or Key.LeftShift or Key.RightShift or Key.LWin or Key.RWin)
        {
            return; // wait for the real key
        }

        var modifiers = HotkeyModifiers.None;
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
        {
            modifiers |= HotkeyModifiers.Control;
        }

        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Alt))
        {
            modifiers |= HotkeyModifiers.Alt;
        }

        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Shift))
        {
            modifiers |= HotkeyModifiers.Shift;
        }

        if (Keyboard.IsKeyDown(Key.LWin) || Keyboard.IsKeyDown(Key.RWin))
        {
            modifiers |= HotkeyModifiers.Windows;
        }

        if (HotkeyGesture.FromKeyPress(modifiers, key.ToString()) is { } gesture)
        {
            _vm.SetHotkey(Target(_recordingButton), gesture);
            StopRecording();
        }
        else
        {
            ShortcutHint.Text = "Hold Ctrl, Alt, Shift or Win and press a letter, number or F-key (Esc cancels).";
        }
    }

    private void OnShortcutLostFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (sender == _recordingButton)
        {
            StopRecording();
        }
    }

    private void StopRecording()
    {
        if (_recordingButton is { } button)
        {
            _recordingButton = null;
            button.Tag = null;
            button.ContentTemplate = (DataTemplate)Resources["ShortcutContent"];
        }

        ShortcutHint.Text = DefaultShortcutHint;
        _vm.StopRecording();
    }
}

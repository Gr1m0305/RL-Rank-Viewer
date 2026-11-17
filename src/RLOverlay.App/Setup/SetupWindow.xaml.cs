using System.IO;
using System.Windows;
using System.Windows.Interop;
using Microsoft.Win32;
using RLOverlay.App.Native;

namespace RLOverlay.App.Setup;

public partial class SetupWindow : Window
{
    private readonly SetupViewModel _vm;

    public SetupWindow(SetupViewModel vm)
    {
        InitializeComponent();
        DataContext = _vm = vm;
        vm.CloseRequested += Close;
        vm.PickFolder = current =>
        {
            var dialog = new OpenFolderDialog
            {
                Title = "Choose where to install",
                InitialDirectory = Directory.Exists(current) ? current : Path.GetDirectoryName(current) ?? "",
            };
            return dialog.ShowDialog(this) == true ? dialog.FolderName : null;
        };
        SourceInitialized += (_, _) => UseDarkTitleBar();
        Closing += (_, e) => e.Cancel = vm.IsWorking; // let the copy finish
    }

    private void UseDarkTitleBar()
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        var on = 1;
        _ = NativeMethods.DwmSetWindowAttribute(hwnd, NativeMethods.DWMWA_USE_IMMERSIVE_DARK_MODE, ref on, sizeof(int));
    }
}

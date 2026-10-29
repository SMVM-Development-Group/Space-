using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using SpacePlusPlus.Infrastructure;
using SpacePlusPlus.ViewModels;

namespace SpacePlusPlus;

public partial class MainWindow : Window
{
    private readonly MainViewModel _viewModel;

    public MainWindow(MainViewModel viewModel)
    {
        InitializeComponent();
        _viewModel = viewModel;
        DataContext = viewModel;
        viewModel.Navigate("overview");
        SourceInitialized += (_, _) => EnableRoundedCorners();
        StateChanged += (_, _) => UpdateMaximizeIcon();
    }

    private void EnableRoundedCorners()
    {
        try
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            if (hwnd == IntPtr.Zero) return;
            int preference = NativeMethods.DWMWCP_ROUND;
            NativeMethods.DwmSetWindowAttribute(hwnd, NativeMethods.DWMWA_WINDOW_CORNER_PREFERENCE, ref preference, sizeof(int));
        }
        catch
        {
            // Rounded corners are a visual enhancement only (older systems).
        }
    }

    private void UpdateMaximizeIcon()
    {
        if (FindName("MaximizeIcon") is System.Windows.Shapes.Path icon)
        {
            icon.Data = (System.Windows.Media.Geometry)FindResource(
                WindowState == WindowState.Maximized ? "IconRestore" : "IconMaximize");
        }
        if (FindName("MaximizeButton") is System.Windows.Controls.Button btn)
        {
            btn.ToolTip = WindowState == WindowState.Maximized ? "Restore" : "Maximize";
        }
    }

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        base.OnPreviewKeyDown(e);
        if (e.Key == Key.F && Keyboard.Modifiers == ModifierKeys.Control)
        {
            if (FindName("SearchBox") is System.Windows.Controls.TextBox box)
            {
                box.Focus();
                box.SelectAll();
                e.Handled = true;
            }
        }
    }

    private void SearchBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            _viewModel.SearchText = string.Empty;
            _viewModel.SearchOpen = false;
            e.Handled = true;
        }
    }
}

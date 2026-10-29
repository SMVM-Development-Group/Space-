using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shell;
using SpacePlusPlus.Infrastructure;

namespace SpacePlusPlus.Views.Dialogs;

/// <summary>
/// Shared chrome for all modal dialogs: custom title bar, rounded corners, Escape-to-close.
/// Deliberately code-only (no XAML) so derived dialogs can use it as their XAML root element.
/// </summary>
public class SpaceDialogWindow : Window
{
    protected SpaceDialogWindow()
    {
        Width = 520;
        SizeToContent = SizeToContent.Height;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;
        ResizeMode = ResizeMode.NoResize;
        UseLayoutRounding = true;
        SnapsToDevicePixels = true;
        TextOptions.SetTextFormattingMode(this, TextFormattingMode.Display);
        if (Application.Current?.TryFindResource("AppFont") is FontFamily font)
            FontFamily = font;

        WindowChrome.SetWindowChrome(this, new WindowChrome
        {
            CaptionHeight = 40,
            ResizeBorderThickness = new Thickness(1),
            GlassFrameThickness = new Thickness(0),
            CornerRadius = new CornerRadius(0),
            UseAeroCaptionButtons = false
        });

        // Present the dialog content directly; dialogs draw their own title bar row.
        Template = new ControlTemplate(typeof(Window))
        {
            VisualTree = new FrameworkElementFactory(typeof(ContentPresenter))
        };

        Loaded += (_, _) =>
        {
            try
            {
                var hwnd = new WindowInteropHelper(this).Handle;
                int preference = NativeMethods.DWMWCP_ROUND;
                NativeMethods.DwmSetWindowAttribute(hwnd, NativeMethods.DWMWA_WINDOW_CORNER_PREFERENCE, ref preference, sizeof(int));
            }
            catch { }
        };
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape) Close();
        };
    }
}

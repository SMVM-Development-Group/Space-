using System.Windows;
using System.Windows.Media;

namespace SpacePlusPlus.Views.Dialogs;

public partial class MessageDialog : SpaceDialogWindow
{
    public string TitleText { get; }
    public string MessageText { get; }

    public MessageDialog(string title, string message, bool error)
    {
        TitleText = title;
        MessageText = message;
        Title = title;
        DataContext = this;
        InitializeComponent();

        Icon.Data = (Geometry)FindResource(error ? "IconWarning" : "IconInfo");
        Icon.Stroke = (Brush)FindResource(error ? "Error" : "TextSecondary");
        Icon.StrokeThickness = 1.6;
    }
}

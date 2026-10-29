using System.Windows;
using System.Windows.Media;

namespace SpacePlusPlus.Views.Dialogs;

public partial class ConfirmDialog : SpaceDialogWindow
{
    public string TitleText { get; }
    public string MessageText { get; }
    public string? DetailsText { get; }
    public string ConfirmText { get; }
    public string? CheckboxText { get; }
    public bool CheckboxResult { get; set; }

    public ConfirmDialog(string title, string message, string? details, string confirmText,
        bool danger, string? checkboxText, bool checkboxChecked)
    {
        TitleText = title;
        MessageText = message;
        DetailsText = string.IsNullOrWhiteSpace(details) ? null : details;
        ConfirmText = confirmText;
        CheckboxText = checkboxText;
        CheckboxResult = checkboxChecked;
        Title = title;
        DataContext = this;
        InitializeComponent();

        if (danger)
        {
            ConfirmButton.Style = (Style)FindResource("BtnDanger");
            Icon.Data = (Geometry)FindResource("IconWarning");
            Icon.Stroke = (Brush)FindResource("Warning");
            Icon.StrokeThickness = 1.6;
        }
        else
        {
            Icon.Data = (Geometry)FindResource("IconInfo");
            Icon.Stroke = (Brush)FindResource("TextSecondary");
            Icon.StrokeThickness = 1.6;
        }
    }

    private void Confirm_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = true;
    }
}

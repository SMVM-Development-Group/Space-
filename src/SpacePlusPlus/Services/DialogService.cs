using System.Windows;
using Microsoft.Extensions.Logging;
using SpacePlusPlus.Models;
using SpacePlusPlus.Views.Dialogs;

namespace SpacePlusPlus.Services;

public sealed class DialogService(ILogger<DialogService> logger)
{
    private static Window? OwnerWindow =>
        Application.Current?.Windows.OfType<Window>().FirstOrDefault(w => w.IsActive) ?? Application.Current?.MainWindow;

    public bool Confirm(string title, string message, string? details = null,
        string confirmText = "Confirm", bool danger = false, string? checkboxText = null, bool checkboxChecked = false)
    {
        try
        {
            var dialog = new ConfirmDialog(title, message, details, confirmText, danger, checkboxText, checkboxChecked)
            {
                Owner = OwnerWindow
            };
            return dialog.ShowDialog() == true;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Confirm dialog failed");
            return false;
        }
    }

    public (bool Confirmed, bool Checkbox) ConfirmWithCheckbox(string title, string message, string? details,
        string confirmText, bool danger, string checkboxText, bool checkboxChecked)
    {
        try
        {
            var dialog = new ConfirmDialog(title, message, details, confirmText, danger, checkboxText, checkboxChecked)
            {
                Owner = OwnerWindow
            };
            bool confirmed = dialog.ShowDialog() == true;
            return (confirmed, dialog.CheckboxResult);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Confirm dialog failed");
            return (false, false);
        }
    }

    public void Info(string title, string message, bool error = false)
    {
        try
        {
            new MessageDialog(title, message, error) { Owner = OwnerWindow }.ShowDialog();
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Message dialog failed");
        }
    }

    public void ShowCleanupResults(IReadOnlyList<CleanupResultItem> items, long totalFreed, bool cancelled)
    {
        try
        {
            new CleanupResultDialog(items, totalFreed, cancelled) { Owner = OwnerWindow }.ShowDialog();
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Result dialog failed");
        }
    }

    public void ShowFileProperties(FileNode file)
    {
        try
        {
            new FilePropertiesDialog(file) { Owner = OwnerWindow }.ShowDialog();
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Properties dialog failed");
        }
    }
}

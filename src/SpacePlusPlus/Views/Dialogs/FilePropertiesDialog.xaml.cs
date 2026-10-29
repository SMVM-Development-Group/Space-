using System.Diagnostics;
using System.IO;
using System.Windows;
using SpacePlusPlus.Models;

namespace SpacePlusPlus.Views.Dialogs;

public partial class FilePropertiesDialog : SpaceDialogWindow
{
    private readonly FileNode _file;

    public string FileName { get; }
    public string SizeLine { get; }
    public string Modified { get; }
    public string TypeName { get; }
    public string CategoryName { get; }
    public string Attributes { get; }
    public string FullPath { get; }

    public FilePropertiesDialog(FileNode file)
    {
        _file = file;
        FileName = file.Name;
        SizeLine = $"{Infrastructure.SizeFormat.Format(file.SizeBytes)} ({file.SizeBytes:N0} bytes) — approx. {Infrastructure.SizeFormat.Format(file.AllocatedBytes)} on disk";
        Modified = file.ModifiedUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss");
        TypeName = file.TypeText;
        CategoryName = file.CategoryText;
        Attributes = file.Attributes.ToString();
        FullPath = file.Path;
        DataContext = this;
        InitializeComponent();
    }

    private void OpenLocation_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{_file.Path}\"") { UseShellExecute = true });
        }
        catch (Exception)
        {
            // Explorer failed to open; nothing destructive occurred.
        }
    }
}

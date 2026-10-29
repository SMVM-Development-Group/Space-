using System.Windows;
using System.Windows.Controls;
using SpacePlusPlus.Models;
using SpacePlusPlus.ViewModels;

namespace SpacePlusPlus.Views;

public partial class LargeFilesPage : UserControl
{
    public LargeFilesPage()
    {
        InitializeComponent();
    }

    private void FilesGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (DataContext is LargeFilesViewModel vm && FilesGrid.SelectedItems is { } selected)
        {
            var list = new List<FileNode>(selected.Count);
            foreach (var item in selected)
                if (item is FileNode file)
                    list.Add(file);
            vm.SelectedFiles = list;
        }
    }
}

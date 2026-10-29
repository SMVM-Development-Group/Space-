using System.Windows;
using System.Windows.Controls;
using SpacePlusPlus.Controls;
using SpacePlusPlus.ViewModels;

namespace SpacePlusPlus.Views;

public partial class StorageMapPage : UserControl
{
    public StorageMapPage()
    {
        InitializeComponent();
        DataContextChanged += (_, _) => WireTreemap();
        Loaded += (_, _) => WireTreemap();
    }

    private void WireTreemap()
    {
        if (Treemap is null) return;
        Treemap.ItemActivated -= OnItemActivated;
        Treemap.ItemActivated += OnItemActivated;
    }

    private void OnItemActivated(object? sender, TreemapItem item)
    {
        if (DataContext is StorageMapViewModel vm)
            vm.NavigateToItemCommand.Execute(item);
    }
}

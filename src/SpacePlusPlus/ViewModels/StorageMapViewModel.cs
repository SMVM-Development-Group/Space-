using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Input;
using Microsoft.Extensions.Logging;
using SpacePlusPlus.Controls;
using SpacePlusPlus.Infrastructure;
using SpacePlusPlus.Models;
using SpacePlusPlus.Services;

namespace SpacePlusPlus.ViewModels;

public sealed class BreadcrumbViewModel
{
    public required string Name { get; init; }
    public required DirectoryNode Node { get; init; }
    public required StorageMapViewModel Owner { get; init; }

    public ICommand NavigateCommand => new RelayCommand(() => Owner.NavigateTo(Node));
}

public sealed class TopItemViewModel
{
    public required string Name { get; init; }
    public required string Path { get; init; }
    public required bool IsDirectory { get; init; }
    public required long SizeBytes { get; init; }
    public required long FileCount { get; init; }
    public string SizeText => SizeFormat.Format(SizeBytes);
}

public sealed class StorageMapViewModel : ObservableObject, IPageLifecycle, TreemapControl.ICommandProvider
{
    private readonly ScanManager _scanManager;
    private readonly SettingsService _settings;
    private readonly SafetyService _safety;
    private readonly DialogService _dialogs;
    private readonly ILogger<StorageMapViewModel> _logger;

    private DirectoryNode? _currentNode;
    private IReadOnlyList<TreemapItem> _items = [];
    private string _filterText = string.Empty;
    private double _minItemRatioPercent = 0.2;
    private string _sortMode = "Size";
    private string _viewModeNote = string.Empty;

    public StorageMapViewModel(
        ScanManager scanManager,
        SettingsService settings,
        SafetyService safety,
        DialogService dialogs,
        IDriveService driveService,
        ILogger<StorageMapViewModel> logger)
    {
        _scanManager = scanManager;
        _settings = settings;
        _safety = safety;
        _dialogs = dialogs;
        _logger = logger;

        ScanNowCommand = new AsyncRelayCommand(ScanNowAsync, () => !_scanManager.IsScanning);
        NavigateUpCommand = new RelayCommand(() =>
        {
            if (_currentNode?.Parent is { } parent) NavigateTo(parent);
        }, () => _currentNode?.Parent is not null);

        NavigateToItemCommand = new RelayCommand<object>(o =>
        {
            if (o is TreemapItem { IsDirectory: true } item)
            {
                var node = FindNode(item.Path);
                if (node is not null) NavigateTo(node);
                else _dialogs.Info("Storage Map", "That folder is not part of the current scan results.");
            }
        });

        OpenLocation = new RelayCommand<string>(OpenLocationCore);
        CopyPath = new RelayCommand<string>(CopyPathCore);
        DeleteItem = new RelayCommand<TreemapItem>(DeleteItemCore);

        _scanManager.ScanCompleted += result => Application.Current?.Dispatcher.BeginInvoke(() =>
        {
            if (result is not null) NavigateTo(result.Root);
            OnPropertyChanged(nameof(HasScan));
        });
    }

    public ObservableCollection<BreadcrumbViewModel> Breadcrumbs { get; } = [];
    public ObservableCollection<TopItemViewModel> TopItems { get; } = [];

    public bool HasScan => _scanManager.LastResult is not null;

    public DirectoryNode? CurrentNode
    {
        get => _currentNode;
        private set
        {
            if (Set(ref _currentNode, value))
            {
                OnPropertyChanged(nameof(CurrentPathText));
                OnPropertyChanged(nameof(CurrentSizeText));
                OnPropertyChanged(nameof(CurrentFilesText));
                OnPropertyChanged(nameof(CurrentDirsText));
                OnPropertyChanged(nameof(ErrorsText));
                OnPropertyChanged(nameof(CanNavigateUp));
                OnPropertyChanged(nameof(RootSize));
                RebuildBreadcrumbs();
            }
        }
    }

    public string CurrentPathText => _currentNode?.Path ?? string.Empty;
    public string CurrentSizeText => _currentNode is { } n ? SizeFormat.Format(n.TotalSize) : "—";
    public string CurrentFilesText => _currentNode is { } n ? SizeFormat.FormatCount(n.TotalFiles) : "—";
    public string CurrentDirsText => _currentNode is { } n ? SizeFormat.FormatCount(n.TotalDirectories) : "—";
    public string ErrorsText => _currentNode is { } n && n.TotalErrors + n.TotalJunctionsSkipped > 0
        ? $"{n.TotalErrors + n.TotalJunctionsSkipped} inaccessible/skipped locations"
        : "None";
    public bool CanNavigateUp => _currentNode?.Parent is not null;

    public long RootSize => _currentNode?.TotalSize ?? 0;

    public IReadOnlyList<TreemapItem> Items
    {
        get => _items;
        private set
        {
            if (Set(ref _items, value))
                OnPropertyChanged(nameof(HasItems));
        }
    }

    public bool HasItems => Items.Count > 0;

    public string ViewModeNote
    {
        get => _viewModeNote;
        private set => Set(ref _viewModeNote, value);
    }

    public string FilterText
    {
        get => _filterText;
        set
        {
            if (Set(ref _filterText, value))
                RebuildItems();
        }
    }

    public double MinItemRatioPercent
    {
        get => _minItemRatioPercent;
        set
        {
            if (Set(ref _minItemRatioPercent, value))
            {
                OnPropertyChanged(nameof(MinItemRatioText));
                RebuildItems();
            }
        }
    }

    public string MinItemRatioText => MinItemRatioPercent >= 1
        ? $"{MinItemRatioPercent:F1}% of view"
        : $"{MinItemRatioPercent:F2}% of view";

    public string SortMode
    {
        get => _sortMode;
        set
        {
            if (Set(ref _sortMode, value))
                RebuildTopItems();
        }
    }

    public IReadOnlyList<string> SortModes { get; } = ["Size", "Name", "Files"];

    public ICommand ScanNowCommand { get; }
    public ICommand NavigateUpCommand { get; }
    public ICommand NavigateToItemCommand { get; }
    public ICommand OpenLocation { get; }
    public ICommand CopyPath { get; }
    public ICommand DeleteItem { get; }

    public void OnActivated()
    {
        OnPropertyChanged(nameof(HasScan));
        if (_currentNode is null && _scanManager.LastResult is { } result)
            NavigateTo(result.Root);
    }

    private async Task ScanNowAsync()
    {
        var location = _settings.Current.DefaultScanLocation;
        string root = !string.IsNullOrWhiteSpace(location) && System.IO.Directory.Exists(location)
            ? location
            : (_scanManager.LastResult?.RootPath ?? "C:\\");
        await _scanManager.StartScanAsync(root);
    }

    public void NavigateTo(DirectoryNode node)
    {
        CurrentNode = node;
        RebuildItems();
        RebuildTopItems();
    }

    private DirectoryNode? FindNode(string path)
    {
        var root = _scanManager.LastResult?.Root;
        if (root is null) return null;
        var current = root;
        if (string.Equals(current.Path, path, StringComparison.OrdinalIgnoreCase)) return current;
        var relative = path;
        if (path.StartsWith(root.Path, StringComparison.OrdinalIgnoreCase))
            relative = path[root.Path.Length..].TrimStart('\\');
        if (relative.Length == 0) return root;
        var segments = relative.Split('\\', StringSplitOptions.RemoveEmptyEntries);
        foreach (var segment in segments)
        {
            var next = current.Children.FirstOrDefault(c =>
                string.Equals(c.Name, segment, StringComparison.OrdinalIgnoreCase));
            if (next is null) return null;
            current = next;
        }
        return current;
    }

    private void RebuildBreadcrumbs()
    {
        Breadcrumbs.Clear();
        var node = _currentNode;
        if (node is null) return;
        var chain = new List<DirectoryNode>();
        for (var n = node; n is not null; n = n.Parent) chain.Add(n);
        chain.Reverse();
        foreach (var n in chain)
            Breadcrumbs.Add(new BreadcrumbViewModel { Name = n.Name, Node = n, Owner = this });
    }

    private void RebuildItems()
    {
        var node = _currentNode;
        if (node is null) { Items = []; ViewModeNote = string.Empty; return; }

        if (!string.IsNullOrWhiteSpace(FilterText))
        {
            var matches = CollectMatches(node, FilterText.Trim());
            Items = matches;
            ViewModeNote = $"Filtered view — {matches.Count} matching items (folders and files ≥ " +
                           $"{SizeFormat.Format(_settings.Current.LargeFileThresholdBytes)})";
            return;
        }

        ViewModeNote = string.Empty;
        Items = [BuildItem(node)];
    }

    private static IReadOnlyList<TreemapItem> CollectMatches(DirectoryNode root, string query)
    {
        var dirs = new List<DirectoryNode>();
        var files = new List<FileNode>();
        var queue = new Queue<DirectoryNode>();
        queue.Enqueue(root);
        while (queue.Count > 0)
        {
            var node = queue.Dequeue();
            foreach (var child in node.Children) queue.Enqueue(child);
            if (node != root && node.Name.Contains(query, StringComparison.OrdinalIgnoreCase))
                dirs.Add(node);
            foreach (var f in node.LargeFiles)
                if (files.Count < 120 && f.Name.Contains(query, StringComparison.OrdinalIgnoreCase))
                    files.Add(f);
            if (dirs.Count >= 120) break;
        }
        var items = new List<TreemapItem>();
        foreach (var d in dirs.OrderByDescending(d => d.TotalSize).Take(60))
            items.Add(ToFlatItem(d));
        foreach (var f in files.OrderByDescending(f => f.SizeBytes).Take(60))
            items.Add(new TreemapItem
            {
                Name = f.Name, Path = f.Path, IsDirectory = false, SizeBytes = f.SizeBytes, FileCount = 1
            });
        return items;
    }

    private static TreemapItem ToFlatItem(DirectoryNode node) => new()
    {
        Name = node.Name,
        Path = node.Path,
        IsDirectory = true,
        SizeBytes = node.TotalSize,
        FileCount = node.TotalFiles
    };

    private TreemapItem BuildItem(DirectoryNode node)
    {
        var captured = node;
        return new TreemapItem
        {
            Name = captured.Name,
            Path = captured.Path,
            IsDirectory = true,
            SizeBytes = captured.TotalSize,
            FileCount = captured.TotalFiles,
            ChildrenProvider = () => BuildChildren(captured)
        };
    }

    private List<TreemapItem> BuildChildren(DirectoryNode node)
    {
        var list = new List<TreemapItem>();
        foreach (var child in node.Children)
            list.Add(BuildItem(child));
        foreach (var file in node.LargeFiles)
        {
            list.Add(new TreemapItem
            {
                Name = file.Name,
                Path = file.Path,
                IsDirectory = false,
                SizeBytes = file.SizeBytes,
                FileCount = 1
            });
        }
        if (node.SmallFilesCount > 0 && node.SmallFilesSize > 0)
        {
            list.Add(new TreemapItem
            {
                Name = $"{SizeFormat.FormatCount(node.SmallFilesCount)} smaller files",
                Path = string.Empty,
                IsDirectory = false,
                SizeBytes = node.SmallFilesSize,
                IsAggregate = true
            });
        }
        return list;
    }

    private void RebuildTopItems()
    {
        TopItems.Clear();
        var node = _currentNode;
        if (node is null) return;

        var entries = new List<TopItemViewModel>();
        foreach (var child in node.Children)
            entries.Add(new TopItemViewModel
            {
                Name = child.Name, Path = child.Path, IsDirectory = true,
                SizeBytes = child.TotalSize, FileCount = child.TotalFiles
            });
        foreach (var file in node.LargeFiles)
            entries.Add(new TopItemViewModel
            {
                Name = file.Name, Path = file.Path, IsDirectory = false,
                SizeBytes = file.SizeBytes, FileCount = 1
            });

        var sorted = SortMode switch
        {
            "Name" => entries.OrderBy(e => e.Name, StringComparer.OrdinalIgnoreCase),
            "Files" => entries.OrderByDescending(e => e.FileCount).ThenByDescending(e => e.SizeBytes),
            _ => entries.OrderByDescending(e => e.SizeBytes)
        };
        foreach (var e in sorted.Take(50))
            TopItems.Add(e);
    }

    private void OpenLocationCore(string? path)
    {
        if (string.IsNullOrEmpty(path)) return;
        try
        {
            if (System.IO.File.Exists(path))
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = true });
            else if (System.IO.Directory.Exists(path))
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("explorer.exe", path) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not open location for {Path}", path);
        }
    }

    private void CopyPathCore(string? path)
    {
        if (string.IsNullOrEmpty(path)) return;
        try { Clipboard.SetText(path); } catch { /* clipboard can be locked by other apps */ }
    }

    private void DeleteItemCore(TreemapItem? item)
    {
        if (item is null || item.IsDirectory || item.IsAggregate || string.IsNullOrEmpty(item.Path)) return;
        var assessment = _safety.Assess(item.Path);
        if (assessment.Level == DeletionSafety.Blocked)
        {
            _dialogs.Info("Deletion blocked", $"{item.Name} cannot be deleted:\n{assessment.Message}", error: true);
            return;
        }

        string warning = assessment.Level == DeletionSafety.Warning ? $"{assessment.Message}\n\n" : string.Empty;
        var (confirmed, permanent) = _dialogs.ConfirmWithCheckbox(
            "Delete file",
            $"{warning}Move this file to the Recycle Bin?\n\n{item.Path}",
            null,
            "Delete",
            danger: true,
            checkboxText: "Permanently delete (bypass Recycle Bin)",
            checkboxChecked: false);
        if (!confirmed) return;

        try
        {
            var (deleted, freed, failures) = _safety.Delete([(item.Path, item.SizeBytes)], permanent, CancellationToken.None);
            if (deleted > 0)
                _dialogs.Info("Deleted", $"Freed approximately {SizeFormat.Format(item.SizeBytes)}. The Storage Map sizes update after the next scan.");
            if (failures.Count > 0)
                _dialogs.Info("Not deleted", $"{failures[0].Path}\n{failures[0].Error}", error: true);
        }
        catch (Exception ex)
        {
            _dialogs.Info("Deletion failed", ex.Message, error: true);
        }
    }
}

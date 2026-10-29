using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Data;
using System.Windows.Input;
using Microsoft.Extensions.Logging;
using Microsoft.Win32;
using SpacePlusPlus.Infrastructure;
using SpacePlusPlus.Models;
using SpacePlusPlus.Services;

namespace SpacePlusPlus.ViewModels;

public sealed class LargeFilesViewModel : ObservableObject, IPageLifecycle
{
    private readonly ScanManager _scanManager;
    private readonly SafetyService _safety;
    private readonly DialogService _dialogs;
    private readonly ILogger<LargeFilesViewModel> _logger;

    private ObservableCollection<FileNode> _files = [];
    private ICollectionView? _filesView;
    private string _searchText = string.Empty;
    private string? _extensionFilter;
    private string? _locationFilter;
    private long _minSizeBytes;
    private IReadOnlyList<string> _extensions = ["(all)"];
    private IReadOnlyList<string> _locations = ["(all)"];
    private IReadOnlyList<FileNode> _selectedFiles = [];
    private System.Timers.Timer? _searchDebounce;

    public LargeFilesViewModel(
        ScanManager scanManager,
        SafetyService safety,
        DialogService dialogs,
        ILogger<LargeFilesViewModel> logger)
    {
        _scanManager = scanManager;
        _safety = safety;
        _dialogs = dialogs;
        _logger = logger;

        _minSizeBytes = scanManager.LastResult is null ? 100L << 20 : 0; // show everything from the current scan by default

        ScanNowCommand = new AsyncRelayCommand(ScanNowAsync, () => !_scanManager.IsScanning);
        OpenFileCommand = new AsyncRelayCommand<FileNode>(OpenFileAsync);
        RevealFileCommand = new RelayCommand<FileNode>(RevealFile);
        CopyPathCommand = new RelayCommand<FileNode>(CopyPath);
        PropertiesCommand = new RelayCommand<FileNode>(ShowProperties);
        DeleteSelectedCommand = new AsyncRelayCommand(DeleteSelectedAsync, () => SelectedFiles.Count > 0);
        MoveSelectedCommand = new AsyncRelayCommand(MoveSelectedAsync, () => SelectedFiles.Count > 0);
        ClearFiltersCommand = new RelayCommand(ClearFilters);

        _scanManager.ScanCompleted += result => Application.Current?.Dispatcher.BeginInvoke(() =>
        {
            LoadFiles(result);
            OnPropertyChanged(nameof(HasScan));
        });
    }

    public ObservableCollection<FileNode> Files
    {
        get => _files;
        private set
        {
            if (Set(ref _files, value))
            {
                _filesView = CollectionViewSource.GetDefaultView(value);
                if (_filesView is not null)
                {
                    _filesView.Filter = FilterFile;
                    using (_filesView.DeferRefresh())
                        _filesView.SortDescriptions.Add(new SortDescription(nameof(FileNode.SizeBytes), ListSortDirection.Descending));
                }
                OnPropertyChanged(nameof(FilesView));
                OnPropertyChanged(nameof(VisibleCountText));
            }
        }
    }

    public ICollectionView? FilesView => _filesView;

    public bool HasScan => _scanManager.LastResult is not null;

    public bool IsScanning => _scanManager.IsScanning;

    public string TotalText
    {
        get
        {
            var scan = _scanManager.LastResult;
            if (scan is null) return "—";
            string suffix = scan.LargeFileTrackingCapped ? " (list capped)" : "";
            return $"{SizeFormat.FormatCount(scan.LargeFiles.Count)} tracked files{suffix}";
        }
    }

    public string VisibleCountText
    {
        get
        {
            long shown = 0;
            long totalBytes = 0;
            if (_filesView is not null)
            {
                foreach (var item in _filesView)
                {
                    if (item is FileNode f) { shown++; totalBytes += f.SizeBytes; }
                }
            }
            return $"{SizeFormat.FormatCount(shown)} files — {SizeFormat.Format(totalBytes)}";
        }
    }

    public string SelectionText => SelectedFiles.Count == 0
        ? "No files selected"
        : $"{SelectedFiles.Count} selected — {SizeFormat.Format(SelectedFiles.Sum(f => f.SizeBytes))}";

    public IReadOnlyList<FileNode> SelectedFiles
    {
        get => _selectedFiles;
        set
        {
            if (Set(ref _selectedFiles, value ?? []))
            {
                OnPropertyChanged(nameof(SelectionText));
                CommandManager.InvalidateRequerySuggested();
            }
        }
    }

    public string SearchText
    {
        get => _searchText;
        set
        {
            if (Set(ref _searchText, value))
            {
                _searchDebounce?.Stop();
                _searchDebounce?.Dispose();
                _searchDebounce = new System.Timers.Timer(200) { AutoReset = false };
                _searchDebounce.Elapsed += (_, _) => Application.Current?.Dispatcher.BeginInvoke(() =>
                {
                    _filesView?.Refresh();
                    OnPropertyChanged(nameof(VisibleCountText));
                });
                _searchDebounce.Start();
            }
        }
    }

    public IReadOnlyList<string> Extensions
    {
        get => _extensions;
        private set => Set(ref _extensions, value);
    }

    public string? ExtensionFilter
    {
        get => _extensionFilter;
        set
        {
            if (Set(ref _extensionFilter, value))
            {
                _filesView?.Refresh();
                OnPropertyChanged(nameof(VisibleCountText));
            }
        }
    }

    public IReadOnlyList<string> Locations
    {
        get => _locations;
        private set => Set(ref _locations, value);
    }

    public string? LocationFilter
    {
        get => _locationFilter;
        set
        {
            if (Set(ref _locationFilter, value))
            {
                _filesView?.Refresh();
                OnPropertyChanged(nameof(VisibleCountText));
            }
        }
    }

    public IReadOnlyList<string> MinSizeOptions { get; } = ["Any size", "10 MB", "50 MB", "100 MB", "500 MB", "1 GB", "5 GB"];

    public string MinSizeOption
    {
        get
        {
            return _minSizeBytes switch
            {
                0 => "Any size",
                10L << 20 => "10 MB",
                50L << 20 => "50 MB",
                100L << 20 => "100 MB",
                500L << 20 => "500 MB",
                1L << 30 => "1 GB",
                5L << 30 => "5 GB",
                _ => "Any size"
            };
        }
        set
        {
            _minSizeBytes = value switch
            {
                "10 MB" => 10L << 20,
                "50 MB" => 50L << 20,
                "100 MB" => 100L << 20,
                "500 MB" => 500L << 20,
                "1 GB" => 1L << 30,
                "5 GB" => 5L << 30,
                _ => 0
            };
            OnPropertyChanged(nameof(MinSizeOption));
            _filesView?.Refresh();
            OnPropertyChanged(nameof(VisibleCountText));
        }
    }

    public ICommand ScanNowCommand { get; }
    public ICommand OpenFileCommand { get; }
    public ICommand RevealFileCommand { get; }
    public ICommand CopyPathCommand { get; }
    public ICommand PropertiesCommand { get; }
    public ICommand DeleteSelectedCommand { get; }
    public ICommand MoveSelectedCommand { get; }
    public ICommand ClearFiltersCommand { get; }

    public void OnActivated()
    {
        OnPropertyChanged(nameof(HasScan));
        if (_scanManager.LastResult is { } result && Files.Count == 0)
            LoadFiles(result);
    }

    private bool FilterFile(object item)
    {
        if (item is not FileNode file) return false;
        if (_minSizeBytes > 0 && file.SizeBytes < _minSizeBytes) return false;
        if (_extensionFilter is { } ext && ext != "(all)" &&
            !string.Equals(file.Extension, ext, StringComparison.OrdinalIgnoreCase)) return false;
        if (_locationFilter is { } loc && loc != "(all)" &&
            !file.Path.Contains("\\" + loc + "\\", StringComparison.OrdinalIgnoreCase)
            && !file.Path.StartsWith(loc + "\\", StringComparison.OrdinalIgnoreCase)) return false;
        if (!string.IsNullOrWhiteSpace(_searchText) &&
            !file.Name.Contains(_searchText.Trim(), StringComparison.OrdinalIgnoreCase)) return false;
        return true;
    }

    private void LoadFiles(ScanResult? result)
    {
        Files.Clear();
        if (result is null)
        {
            Extensions = ["(all)"];
            Locations = ["(all)"];
            OnPropertyChanged(nameof(TotalText));
            return;
        }

        foreach (var file in result.LargeFiles)
            Files.Add(file);

        Extensions = ["(all)", .. result.LargeFiles
            .Where(f => !string.IsNullOrEmpty(f.Extension))
            .GroupBy(f => f.Extension, StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(g => g.Sum(f => f.SizeBytes))
            .Select(g => g.Key)
            .Take(30)];

        var locations = result.LargeFiles
            .Select(f => ExtractTopDir(f.Path, result.RootPath))
            .Where(l => l is not null)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(l => l)
            .ToList();
        Locations = ["(all)", .. locations];

        OnPropertyChanged(nameof(TotalText));
        OnPropertyChanged(nameof(VisibleCountText));
    }

    private static string? ExtractTopDir(string path, string rootPath)
    {
        if (!path.StartsWith(rootPath, StringComparison.OrdinalIgnoreCase)) return null;
        var relative = path[rootPath.Length..].TrimStart('\\');
        var segments = relative.Split('\\');
        return segments.Length > 1 ? segments[0] : null;
    }

    private async Task ScanNowAsync()
    {
        var root = _scanManager.LastResult?.RootPath ?? "C:\\";
        await _scanManager.StartScanAsync(root);
    }

    private async Task OpenFileAsync(FileNode? file)
    {
        if (file is null || !System.IO.File.Exists(file.Path)) return;
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(file.Path) { UseShellExecute = true });
            await Task.CompletedTask;
        }
        catch (Exception ex)
        {
            _dialogs.Info("Cannot open file", ex.Message, error: true);
        }
    }

    private void RevealFile(FileNode? file)
    {
        if (file is null) return;
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("explorer.exe", $"/select,\"{file.Path}\"") { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not reveal {Path}", file.Path);
        }
    }

    private void CopyPath(FileNode? file)
    {
        if (file is null) return;
        try { Clipboard.SetText(file.Path); } catch { /* clipboard may be locked */ }
    }

    private void ShowProperties(FileNode? file)
    {
        if (file is null) return;
        _dialogs.ShowFileProperties(file);
    }

    private async Task DeleteSelectedAsync()
    {
        var files = SelectedFiles;
        if (files.Count == 0) return;

        // Safety gate for every selected file.
        var allowed = new List<(string Path, long Size)>();
        var blocked = new List<string>();
        var warnings = new List<string>();
        foreach (var file in files)
        {
            var assessment = _safety.Assess(file.Path);
            switch (assessment.Level)
            {
                case DeletionSafety.Blocked:
                    blocked.Add($"{file.Name}: {assessment.Message}");
                    break;
                case DeletionSafety.Warning:
                    warnings.Add($"{file.Name}: {assessment.Message}");
                    allowed.Add((file.Path, file.SizeBytes));
                    break;
                default:
                    allowed.Add((file.Path, file.SizeBytes));
                    break;
            }
        }

        if (blocked.Count > 0)
        {
            _dialogs.Info("Some files are protected",
                "These items cannot be deleted from Space++:\n\n" + string.Join("\n", blocked.Take(6)), error: true);
            if (allowed.Count == 0) return;
        }

        long totalBytes = allowed.Sum(a => a.Size);
        string details = warnings.Count > 0 ? string.Join("\n", warnings.Take(6)) : null;
        var (confirmed, permanent) = _dialogs.ConfirmWithCheckbox(
            "Delete files",
            $"Move {allowed.Count} file(s) — {SizeFormat.Format(totalBytes)} — to the Recycle Bin?",
            details,
            "Delete",
            danger: true,
            checkboxText: "Permanently delete (bypass Recycle Bin)",
            checkboxChecked: false);
        if (!confirmed) return;

        try
        {
            var (deleted, freed, failures) = await Task.Run(() =>
                _safety.Delete(allowed, permanent, CancellationToken.None));

            foreach (var deletedFile in files.Where(f => failures.All(fail => fail.Path != f.Path)).ToList())
                Files.Remove(deletedFile);

            string message = $"{deleted} of {allowed.Count} files deleted — approximately {SizeFormat.Format(freed)} freed.";
            if (failures.Count > 0)
                message += $"\n\n{failures.Count} file(s) could not be deleted, e.g.:\n{failures[0].Path}";
            _dialogs.Info("Deletion complete", message);
            OnPropertyChanged(nameof(VisibleCountText));
        }
        catch (Exception ex)
        {
            _dialogs.Info("Deletion failed", ex.Message, error: true);
        }
        await Task.CompletedTask;
    }

    private async Task MoveSelectedAsync()
    {
        var files = SelectedFiles;
        if (files.Count == 0) return;

        var dialog = new OpenFolderDialog
        {
            Title = "Choose destination folder",
            InitialDirectory = System.IO.Path.GetDirectoryName(files[0].Path) ?? string.Empty
        };
        if (dialog.ShowDialog(Application.Current.MainWindow) != true) return;
        string destination = dialog.FolderName;

        bool confirmed = _dialogs.Confirm(
            "Move files",
            $"Move {files.Count} file(s) — {SizeFormat.Format(files.Sum(f => f.SizeBytes))} — to:\n{destination}",
            null,
            "Move",
            danger: false);
        if (!confirmed) return;

        int moved = 0, skipped = 0;
        var errors = new List<string>();
        await Task.Run(() =>
        {
            foreach (var file in files)
            {
                try
                {
                    string target = Path.Combine(destination, file.Name);
                    string unique = target;
                    int counter = 1;
                    while (System.IO.File.Exists(unique))
                        unique = Path.Combine(destination,
                            $"{Path.GetFileNameWithoutExtension(file.Name)} ({counter++}){Path.GetExtension(file.Name)}");
                    System.IO.File.Move(file.Path, unique, false);
                    moved++;
                    Application.Current?.Dispatcher.BeginInvoke(() => Files.Remove(file));
                }
                catch (Exception ex)
                {
                    skipped++;
                    if (errors.Count < 5) errors.Add($"{file.Name}: {ex.Message}");
                }
            }
        });

        string message = $"{moved} file(s) moved to {destination}.";
        if (skipped > 0) message += $"\n{skipped} failed, e.g. {errors[0]}";
        _dialogs.Info("Move complete", message);
        OnPropertyChanged(nameof(VisibleCountText));
    }

    private void ClearFilters()
    {
        SearchText = string.Empty;
        ExtensionFilter = "(all)";
        LocationFilter = "(all)";
        MinSizeOption = "Any size";
    }

    public void FocusFile(FileNode? file)
    {
        if (file is null) return;
        SearchText = file.Name;
    }
}

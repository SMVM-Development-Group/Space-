using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Input;
using Microsoft.Extensions.Logging;
using SpacePlusPlus.Infrastructure;
using SpacePlusPlus.Services;

namespace SpacePlusPlus.ViewModels;

public sealed class NavItemViewModel : ObservableObject
{
    private bool _isSelected;

    public required string Key { get; init; }
    public required string Title { get; init; }
    public required string IconKey { get; init; }
    public required MainViewModel Owner { get; init; }

    public bool IsSelected
    {
        get => _isSelected;
        set { if (Set(ref _isSelected, value) && value) Owner.Navigate(Key); }
    }

    public ICommand SelectCommand => new RelayCommand(() => Owner.Navigate(Key));
}

public sealed class MainViewModel : ObservableObject
{
    private readonly ScanManager _scanManager;
    private readonly SearchService _search;
    private readonly ILogger<MainViewModel> _logger;
    private readonly Dictionary<string, object> _pages;
    private object? _currentPage;
    private string? _activeKey;
    private string _statusText = "Ready";
    private string _searchText = string.Empty;
    private IReadOnlyList<SearchResult> _searchResults = [];
    private bool _searchOpen;
    private System.Timers.Timer? _searchDebounce;

    public MainViewModel(
        OverviewViewModel overview, StorageMapViewModel storageMap, LargeFilesViewModel largeFiles,
        ApplicationsViewModel applications, SmartCleanupViewModel smartCleanup, OneDriveViewModel oneDrive,
        ScanHistoryViewModel scanHistory, SettingsViewModel settings, DebloatViewModel debloat,
        ScanManager scanManager, SearchService search, ILogger<MainViewModel> logger)
    {
        _scanManager = scanManager;
        _search = search;
        _logger = logger;

        _pages = new Dictionary<string, object>
        {
            ["overview"] = overview,
            ["storage-map"] = storageMap,
            ["large-files"] = largeFiles,
            ["applications"] = applications,
            ["smart-cleanup"] = smartCleanup,
            ["debloat"] = debloat,
            ["onedrive"] = oneDrive,
            ["scan-history"] = scanHistory,
            ["settings"] = settings
        };

        NavItems = new ObservableCollection<NavItemViewModel>
        {
            new() { Key = "overview", Title = "Overview", IconKey = "Overview", Owner = this },
            new() { Key = "storage-map", Title = "Storage Map", IconKey = "StorageMap", Owner = this },
            new() { Key = "large-files", Title = "Large Files", IconKey = "LargeFiles", Owner = this },
            new() { Key = "applications", Title = "Applications", IconKey = "Applications", Owner = this },
            new() { Key = "smart-cleanup", Title = "Smart Cleanup", IconKey = "Cleanup", Owner = this },
            new() { Key = "debloat", Title = "Debloat", IconKey = "Debloat", Owner = this },
            new() { Key = "onedrive", Title = "OneDrive", IconKey = "OneDrive", Owner = this },
            new() { Key = "scan-history", Title = "Scan History", IconKey = "History", Owner = this },
            new() { Key = "settings", Title = "Settings", IconKey = "Settings", Owner = this }
        };

        _scanManager.PropertyChanged += (_, _) => UpdateStatusText();
        _scanManager.ScanStarted += () => UpdateStatusText();
        _scanManager.ScanCompleted += _ => UpdateStatusText();

        ExecuteSearchResultCommand = new RelayCommand<SearchResult>(ExecuteSearchResult);
        ClearSearchCommand = new RelayCommand(() => SearchText = string.Empty);
        MinimizeCommand = new RelayCommand(() =>
        {
            var w = Application.Current?.MainWindow;
            if (w is not null) w.WindowState = WindowState.Minimized;
        });
        MaximizeRestoreCommand = new RelayCommand(() =>
        {
            var w = Application.Current?.MainWindow;
            if (w is null) return;
            w.WindowState = w.WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
        });
        CloseCommand = new RelayCommand(() => Application.Current?.MainWindow?.Close());
    }

    public ObservableCollection<NavItemViewModel> NavItems { get; }

    public object? CurrentPage
    {
        get => _currentPage;
        private set => Set(ref _currentPage, value);
    }

    public string? ActiveKey
    {
        get => _activeKey;
        private set => Set(ref _activeKey, value);
    }

    public string StatusText
    {
        get => _statusText;
        private set => Set(ref _statusText, value);
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
                if (string.IsNullOrWhiteSpace(value))
                {
                    SearchResults = [];
                    SearchOpen = false;
                }
                else
                {
                    _searchDebounce = new System.Timers.Timer(250) { AutoReset = false };
                    _searchDebounce.Elapsed += async (_, _) => await RunSearchAsync();
                    _searchDebounce.Start();
                }
            }
        }
    }

    public IReadOnlyList<SearchResult> SearchResults
    {
        get => _searchResults;
        private set => Set(ref _searchResults, value);
    }

    public bool SearchOpen
    {
        get => _searchOpen;
        set => Set(ref _searchOpen, value);
    }

    public ICommand ExecuteSearchResultCommand { get; }
    public ICommand ClearSearchCommand { get; }
    public ICommand MinimizeCommand { get; }
    public ICommand MaximizeRestoreCommand { get; }
    public ICommand CloseCommand { get; }

    public bool IsScanning => _scanManager.IsScanning;
    public string? ErrorNotice => _scanManager.LastError;

    public string StatusVersionText
    {
        get
        {
            var version = typeof(MainViewModel).Assembly.GetName().Version;
            return $"Space++ {version?.Major ?? 1}.{version?.Minor ?? 0} — .NET 10 / WPF";
        }
    }

    private async Task RunSearchAsync()
    {
        try
        {
            var query = SearchText;
            if (string.IsNullOrWhiteSpace(query)) { SearchOpen = false; return; }
            var results = await _search.SearchAsync(query);
            SearchResults = results;
            SearchOpen = results.Count > 0;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Global search failed");
        }
    }

    private void ExecuteSearchResult(SearchResult? result)
    {
        if (result is null) return;
        SearchOpen = false;
        switch (result.TargetKind)
        {
            case "file":
                Navigate("large-files");
                if (CurrentPage is LargeFilesViewModel lf) lf.FocusFile(result.Target as Models.FileNode);
                break;
            case "folder":
                Navigate("storage-map");
                if (CurrentPage is StorageMapViewModel sm && result.Target is Models.DirectoryNode node) sm.NavigateTo(node);
                break;
            case "app":
                Navigate("applications");
                if (CurrentPage is ApplicationsViewModel av) av.FilterText = result.Title;
                break;
            case "cleanup":
                Navigate("smart-cleanup");
                break;
            case "history":
                Navigate("scan-history");
                break;
        }
    }

    public void Navigate(string key)
    {
        if (!_pages.TryGetValue(key, out var page)) return;
        foreach (var item in NavItems)
            item.IsSelected = item.Key == key;
        if (ActiveKey == key && CurrentPage == page) return;
        ActiveKey = key;
        CurrentPage = page;
        (page as IPageLifecycle)?.OnActivated();
    }

    private void UpdateStatusText()
    {
        if (_scanManager.IsScanning)
        {
            var p = _scanManager.Progress;
            if (p is not null)
            {
                StatusText = $"{(_scanManager.IsPaused ? "Paused" : "Scanning")} — " +
                             $"{Infrastructure.SizeFormat.FormatCount(p.FilesScanned)} files, " +
                             $"{Infrastructure.SizeFormat.FormatCount(p.DirectoriesScanned)} folders — " +
                             $"elapsed {Infrastructure.SizeFormat.FormatDuration(p.Elapsed)}";
            }
            else
            {
                StatusText = "Scanning…";
            }
        }
        else if (_scanManager.LastResult is { } result)
        {
            StatusText = $"Last scan: {result.DisplayName} — {Infrastructure.SizeFormat.Format(result.TotalSizeBytes)} " +
                         $"in {Infrastructure.SizeFormat.FormatDuration(result.Duration)}" +
                         (result.ErrorCount > 0 ? $" — {result.ErrorCount} inaccessible locations" : "");
        }
        else
        {
            StatusText = "Ready — run a scan to analyze storage";
        }
    }
}

public interface IPageLifecycle
{
    void OnActivated();
}

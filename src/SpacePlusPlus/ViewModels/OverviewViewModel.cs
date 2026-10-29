using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Media;
using Microsoft.Extensions.Logging;
using SpacePlusPlus.Infrastructure;
using SpacePlusPlus.Models;
using SpacePlusPlus.Services;

namespace SpacePlusPlus.ViewModels;

public sealed class CategoryStatViewModel
{
    public required StorageCategory Category { get; init; }
    public required long Bytes { get; init; }
    public required long TotalBytes { get; init; }
    public string Title => Category.Title();
    public Brush ColorBrush => new SolidColorBrush((Color)ColorConverter.ConvertFromString(Category.ColorHex()));
    public string SizeText => SizeFormat.Format(Bytes);
    public string ShareText => TotalBytes > 0 ? $"{Bytes * 100.0 / TotalBytes:F1}%" : "—";
}

public sealed class DriveCardViewModel : ObservableObject
{
    private bool _isSelected;

    public required DriveModel Drive { get; init; }
    public required OverviewViewModel Owner { get; init; }

    public string Letter => Drive.Letter;
    public string Title => Drive.DisplayName;
    public string FileSystem => Drive.FileSystem;
    public string TotalText => SizeFormat.Format(Drive.TotalBytes);
    public string UsedText => SizeFormat.Format(Drive.UsedBytes);
    public string FreeText => SizeFormat.Format(Drive.FreeBytes);
    public double UsedFraction => Drive.UsedFraction;
    public double UsedPercentValue => Drive.UsedFraction * 100;
    public string UsedPercent => $"{Drive.UsedFraction * 100:F0}%";

    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            if (Set(ref _isSelected, value) && value)
                Owner.SelectedDriveCard = this;
        }
    }
}

public sealed class RecommendationViewModel
{
    public required string Title { get; init; }
    public required string SizeText { get; init; }
    public required string CategoryText { get; init; }
    public required string Confidence { get; init; }
    public required bool IsSafe { get; init; }
}

public sealed class ActivityViewModel
{
    public required ActivityRecord Record { get; init; }
    public string TimeText => Record.TimeText;
    public string KindText => Record.KindText;
    public string Title => Record.Title;
    public string Detail => Record.Detail;
}

public sealed class OverviewViewModel : ObservableObject, IPageLifecycle
{
    private readonly IDriveService _driveService;
    private readonly ScanManager _scanManager;
    private readonly CleanupService _cleanup;
    private readonly OneDriveService _oneDrive;
    private readonly ScanHistoryService _history;
    private readonly SettingsService _settings;
    private readonly ILogger<OverviewViewModel> _logger;

    private DriveCardViewModel? _selectedDriveCard;
    private ScanResult? _lastResult;
    private List<CategoryStatViewModel> _categoryStats = [];
    private long _categoryTotal;
    private long? _unclassifiedBytes;
    private List<RecommendationViewModel> _recommendations = [];
    private long _cleanupEstimateBytes;
    private List<ActivityViewModel> _recentActivity = [];
    private string _scanErrorText = string.Empty;

    public OverviewViewModel(
        IDriveService driveService,
        ScanManager scanManager,
        CleanupService cleanup,
        OneDriveService oneDrive,
        ScanHistoryService history,
        SettingsService settings,
        ILogger<OverviewViewModel> logger)
    {
        _driveService = driveService;
        _scanManager = scanManager;
        _cleanup = cleanup;
        _oneDrive = oneDrive;
        _history = history;
        _settings = settings;
        _logger = logger;

        RefreshDrivesCommand = new AsyncRelayCommand(() => LoadDrivesAsync());
        StartScanCommand = new AsyncRelayCommand(StartScanAsync, () => !_scanManager.IsScanning);
        CancelScanCommand = new AsyncRelayCommand(() => { _scanManager.Cancel(); return Task.CompletedTask; }, () => _scanManager.IsScanning);
        TogglePauseCommand = new RelayCommand(() => _scanManager.SetPaused(!_scanManager.IsPaused));
        ReviewCleanupCommand = new RelayCommand(() =>
        {
            if (Application.Current.MainWindow?.DataContext is MainViewModel main)
                main.Navigate("smart-cleanup");
        });
        RescanCommand = new AsyncRelayCommand(() => StartScanAsync(), () => !_scanManager.IsScanning);

        _scanManager.PropertyChanged += (_, e) =>
        {
            switch (e.PropertyName)
            {
                case nameof(ScanManager.Progress):
                case nameof(ScanManager.IsPaused):
                    OnPropertyChanged(nameof(Progress));
                    OnPropertyChanged(nameof(IsPaused));
                    OnPropertyChanged(nameof(ElapsedText));
                    OnPropertyChanged(nameof(CurrentDirectoryText));
                    OnPropertyChanged(nameof(FilesText));
                    OnPropertyChanged(nameof(DirsText));
                    OnPropertyChanged(nameof(BytesText));
                    OnPropertyChanged(nameof(ErrorsText));
                    break;
                case nameof(ScanManager.IsScanning):
                    OnPropertyChanged(nameof(IsScanning));
                    ScanErrorText = string.Empty;
                    break;
                case nameof(ScanManager.LastError):
                    ScanErrorText = _scanManager.LastError ?? string.Empty;
                    break;
            }
        };
        _scanManager.ScanCompleted += result => Application.Current?.Dispatcher.BeginInvoke(() =>
        {
            RefreshFromScan(result);
            RefreshActivity();
        });
        _cleanup.DetectionChanged += () => Application.Current?.Dispatcher.BeginInvoke(() =>
        {
            if (_cleanup.CurrentGroups is { } groups) ApplyGroups(groups);
        });
    }

    public ObservableCollection<DriveCardViewModel> DriveCards { get; } = [];

    public AsyncRelayCommand RefreshDrivesCommand { get; }
    public AsyncRelayCommand StartScanCommand { get; }
    public AsyncRelayCommand CancelScanCommand { get; }
    public RelayCommand TogglePauseCommand { get; }
    public RelayCommand ReviewCleanupCommand { get; }
    public AsyncRelayCommand RescanCommand { get; }

    public bool IsScanning => _scanManager.IsScanning;
    public bool IsPaused => _scanManager.IsPaused;
    public ScanProgress? Progress => _scanManager.Progress;
    public string ElapsedText => Progress?.Elapsed is { } e ? SizeFormat.FormatDuration(e) : "0s";
    public string CurrentDirectoryText => Progress?.CurrentDirectory ?? string.Empty;
    public string FilesText => Progress is { } p ? SizeFormat.FormatCount(p.FilesScanned) : "0";
    public string DirsText => Progress is { } p ? SizeFormat.FormatCount(p.DirectoriesScanned) : "0";
    public string BytesText => Progress is { } p ? SizeFormat.Format(p.BytesProcessed) : "0 B";
    public string ErrorsText => Progress is { } p && p.Errors + p.JunctionsSkipped > 0
        ? $"{p.Errors + p.JunctionsSkipped} inaccessible/skipped"
        : string.Empty;

    public string ScanErrorText
    {
        get => _scanErrorText;
        private set => Set(ref _scanErrorText, value);
    }

    public ScanResult? LastResult
    {
        get => _lastResult;
        private set
        {
            if (Set(ref _lastResult, value))
                OnPropertyChanged(nameof(HasScan));
        }
    }

    public bool HasScan => LastResult is not null;

    public List<CategoryStatViewModel> CategoryStats
    {
        get => _categoryStats;
        private set
        {
            if (Set(ref _categoryStats, value))
            {
                OnPropertyChanged(nameof(HasCategories));
                OnPropertyChanged(nameof(CategorySegments));
            }
        }
    }

    /// <summary>Proportional strip segments derived from the current category stats.</summary>
    public IReadOnlyList<SpacePlusPlus.Controls.CategorySegment> CategorySegments =>
        CategoryStats
            .Where(s => s.Bytes > 0)
            .Select(s => new SpacePlusPlus.Controls.CategorySegment
            {
                Label = s.Title,
                Bytes = s.Bytes,
                ColorHex = s.Category.ColorHex()
            })
            .ToList();

    public bool HasCategories => CategoryStats.Count > 0;

    public long CategoryTotal
    {
        get => _categoryTotal;
        private set => Set(ref _categoryTotal, value);
    }

    public long? UnclassifiedBytes
    {
        get => _unclassifiedBytes;
        private set
        {
            if (Set(ref _unclassifiedBytes, value))
                OnPropertyChanged(nameof(UnclassifiedText));
        }
    }

    public string? UnclassifiedText => UnclassifiedBytes is { } b && b > 0 ? SizeFormat.Format(b) : null;

    public List<RecommendationViewModel> Recommendations
    {
        get => _recommendations;
        private set
        {
            if (Set(ref _recommendations, value))
                OnPropertyChanged(nameof(HasRecommendations));
        }
    }

    public bool HasRecommendations => Recommendations.Count > 0;

    public long CleanupEstimateBytes
    {
        get => _cleanupEstimateBytes;
        private set
        {
            if (Set(ref _cleanupEstimateBytes, value))
                OnPropertyChanged(nameof(CleanupEstimateText));
        }
    }

    public string CleanupEstimateText => SizeFormat.Format(CleanupEstimateBytes);

    public List<ActivityViewModel> RecentActivity
    {
        get => _recentActivity;
        private set
        {
            if (Set(ref _recentActivity, value))
                OnPropertyChanged(nameof(HasActivity));
        }
    }

    public bool HasActivity => RecentActivity.Count > 0;

    public bool HasDrives => DriveCards.Count > 0;

    public DriveCardViewModel? SelectedDriveCard
    {
        get => _selectedDriveCard;
        set
        {
            if (Set(ref _selectedDriveCard, value) && value is not null)
            {
                foreach (var card in DriveCards)
                    if (card != value) card.IsSelected = false;
            }
        }
    }

    public void OnActivated()
    {
        _ = LoadDrivesAsync();
        RefreshFromScan(_scanManager.LastResult);
        if (_cleanup.CurrentGroups is { } groups) ApplyGroups(groups);
        else RefreshCleanupEstimateInBackground();
        RefreshActivity();
    }

    private async Task LoadDrivesAsync()
    {
        try
        {
            var drives = await Task.Run(_driveService.GetDrives);
            DriveCards.Clear();
            foreach (var drive in drives)
                DriveCards.Add(new DriveCardViewModel { Drive = drive, Owner = this });
            SelectedDriveCard ??= DriveCards.FirstOrDefault();
            OnPropertyChanged(nameof(HasDrives));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Drive enumeration failed");
        }
    }

    private string ResolveRoot()
    {
        var location = _settings.Current.DefaultScanLocation;
        if (!string.IsNullOrWhiteSpace(location) && System.IO.Directory.Exists(location)) return location;
        return DriveCards.FirstOrDefault()?.Drive.RootPath ?? "C:\\";
    }

    private async Task StartScanAsync()
    {
        if (_scanManager.IsScanning) return;
        var root = SelectedDriveCard?.Drive.RootPath ?? ResolveRoot();
        var drive = _driveService.FindDrive(root);
        await _scanManager.StartScanAsync(root, drive is null ? root : drive.DisplayName);
    }

    public void RefreshFromScan(ScanResult? result)
    {
        LastResult = result;

        if (result is null)
        {
            CategoryStats = [];
            CategoryTotal = 0;
            UnclassifiedBytes = null;
            return;
        }

        long total = result.TotalSizeBytes;
        CategoryTotal = total;
        CategoryStats = result.CategorySizes
            .Where(k => k.Value > 0)
            .OrderByDescending(k => k.Value)
            .Select(k => new CategoryStatViewModel { Category = k.Key, Bytes = k.Value, TotalBytes = total })
            .ToList();

        // Drive usage not attributed to any scanned file: inaccessible, compressed or
        // protected locations. Honest estimate — never an exact figure.
        UnclassifiedBytes = result.UnclassifiedBytes;
    }

    private void RefreshCleanupEstimateInBackground()
    {
        _ = Task.Run(async () =>
        {
            try
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(120));
                await _cleanup.DetectAsync(cts.Token);
            }
            catch (OperationCanceledException) { }
            catch (Exception ex) { _logger.LogWarning(ex, "Background cleanup detection failed"); }
        });
    }

    private void ApplyGroups(IReadOnlyList<CleanupGroup> groups)
    {
        long total = 0;
        var list = new List<RecommendationViewModel>();
        foreach (var g in groups.OrderByDescending(g => g.EstimatedBytes ?? 0))
        {
            if (g.Id == "windows-update") continue;
            if (g.EstimatedBytes is { } b && b > 0)
            {
                total += b;
                if (list.Count < 5)
                {
                    list.Add(new RecommendationViewModel
                    {
                        Title = g.Title,
                        SizeText = SizeFormat.Format(b),
                        CategoryText = g.CategoryLabel,
                        Confidence = g.SafetyText,
                        IsSafe = g.Safety == CleanupSafety.Safe
                    });
                }
            }
        }

        if (_oneDrive.IsAvailable)
        {
            list.Add(new RecommendationViewModel
            {
                Title = "OneDrive files can be made online-only",
                SizeText = "Analyze",
                CategoryText = "Cloud",
                Confidence = "Review",
                IsSafe = false
            });
        }

        CleanupEstimateBytes = total;
        Recommendations = list;
    }

    private void RefreshActivity()
    {
        try
        {
            RecentActivity = _history.GetActivity(8).Select(a => new ActivityViewModel { Record = a }).ToList();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Activity load failed");
        }
    }
}

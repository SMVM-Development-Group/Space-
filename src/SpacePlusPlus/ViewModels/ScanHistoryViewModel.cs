using System.Collections.ObjectModel;
using System.Windows.Input;
using Microsoft.Extensions.Logging;
using SpacePlusPlus.Infrastructure;
using SpacePlusPlus.Models;
using SpacePlusPlus.Services;

namespace SpacePlusPlus.ViewModels;

public sealed class ScanRecordViewModel
{
    public required ScanRecord Record { get; init; }

    public string TimestampText => Record.TimestampUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss");
    public string DisplayName => Record.DisplayName;
    public string RootPath => Record.RootPath;
    public string DurationText => SizeFormat.FormatDuration(TimeSpan.FromMilliseconds(Record.DurationMs));
    public string SizeText => SizeFormat.Format(Record.TotalBytes);
    public string AllocatedText => SizeFormat.Format(Record.TotalAllocatedBytes);
    public string FilesText => SizeFormat.FormatCount(Record.TotalFiles);
    public string DirsText => SizeFormat.FormatCount(Record.TotalDirectories);
    public string LargeFilesText => SizeFormat.FormatCount(Record.LargeFileCount);
    public string StatusText => Record.WasCancelled ? "Cancelled — partial results" : "Completed";
    public bool WasCancelled => Record.WasCancelled;

    public IReadOnlyList<TopDirStat> TopLevelDirs => Record.TopLevelDirs;

    public IReadOnlyList<(string Label, string SizeText, string Share)> Categories
    {
        get
        {
            var list = new List<(string, string, string)>();
            foreach (var (key, value) in Record.CategorySizes.OrderByDescending(k => k.Value))
            {
                var parsed = ParseCategory(key);
                list.Add((parsed.Title(), SizeFormat.Format(value),
                    Record.TotalBytes > 0 ? $"{value * 100.0 / Record.TotalBytes:F1}%" : "—"));
            }
            return list;
        }
    }

    private static StorageCategory ParseCategory(string key)
        => Enum.TryParse<StorageCategory>(key, out var c) ? c : StorageCategory.Other;
}

public sealed class CleanupRecordViewModel
{
    public required CleanupRecord Record { get; init; }

    public string TimestampText => Record.TimestampUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss");
    public string TotalText => SizeFormat.Format(Record.TotalFreedBytes);
    public string GroupsText => Record.Groups.Count.ToString("N0", System.Globalization.CultureInfo.InvariantCulture);
    public string Summary => Record.Summary;

    public IReadOnlyList<CleanupGroupStat> Entries => Record.Groups;
}

public sealed class ScanHistoryViewModel : ObservableObject, IPageLifecycle
{
    private readonly ScanHistoryService _history;
    private readonly DialogService _dialogs;
    private readonly ILogger<ScanHistoryViewModel> _logger;

    private ScanRecordViewModel? _selectedScan;
    private string _compareText = string.Empty;

    public ScanHistoryViewModel(ScanHistoryService history, DialogService dialogs, ILogger<ScanHistoryViewModel> logger)
    {
        _history = history;
        _dialogs = dialogs;
        _logger = logger;

        ClearHistoryCommand = new RelayCommand(ClearHistory);

        _history.Changed += () => System.Windows.Application.Current?.Dispatcher.BeginInvoke(Rebuild);
    }

    public ObservableCollection<ScanRecordViewModel> Scans { get; } = [];
    public ObservableCollection<CleanupRecordViewModel> Cleanups { get; } = [];

    public RelayCommand ClearHistoryCommand { get; }

    public bool HasScans => Scans.Count > 0;
    public bool HasCleanups => Cleanups.Count > 0;

    public ScanRecordViewModel? SelectedScan
    {
        get => _selectedScan;
        set
        {
            if (Set(ref _selectedScan, value))
                OnPropertyChanged(nameof(ComparisonText));
        }
    }

    /// <summary>Compares the selected scan against the previous scan of the same root.</summary>
    public string ComparisonText
    {
        get
        {
            if (_selectedScan is not { } selected) return string.Empty;
            var previous = Scans
                .SkipWhile(s => s != selected)
                .Skip(1)
                .FirstOrDefault(s => string.Equals(s.Record.RootPath, selected.Record.RootPath, StringComparison.OrdinalIgnoreCase));
            if (previous is null)
                return $"No earlier scan of {selected.RootPath} to compare with.";

            long delta = selected.Record.TotalBytes - previous.Record.TotalBytes;
            string direction = delta > 1024 ? "+" : delta < -1024 ? "−" : "±";
            string percent = previous.Record.TotalBytes > 0
                ? $"{Math.Abs(delta) * 100.0 / previous.Record.TotalBytes:F1}%"
                : "—";
            return $"Compared with {previous.TimestampText}: {direction}{SizeFormat.Format(Math.Abs(delta))} ({percent}) " +
                   $"— {SizeFormat.FormatCount(previous.Record.TotalFiles)} → {SizeFormat.FormatCount(selected.Record.TotalFiles)} files";
        }
    }

    public void OnActivated() => Rebuild();

    private void Rebuild()
    {
        try
        {
            var scans = _history.GetScans().Select(s => new ScanRecordViewModel { Record = s }).ToList();
            Scans.Clear();
            foreach (var s in scans) Scans.Add(s);
            SelectedScan ??= Scans.FirstOrDefault();

            var cleanups = _history.GetCleanups().Select(c => new CleanupRecordViewModel { Record = c }).ToList();
            Cleanups.Clear();
            foreach (var c in cleanups) Cleanups.Add(c);

            OnPropertyChanged(nameof(HasScans));
            OnPropertyChanged(nameof(HasCleanups));
            OnPropertyChanged(nameof(ComparisonText));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "History rebuild failed");
        }
    }

    private void ClearHistory()
    {
        bool confirmed = _dialogs.Confirm(
            "Clear history",
            "Delete all saved scan and cleanup records? This does not touch any files on disk.",
            null,
            "Clear history",
            danger: true);
        if (!confirmed) return;
        _history.ClearAsync();
        SelectedScan = null;
        Rebuild();
    }
}

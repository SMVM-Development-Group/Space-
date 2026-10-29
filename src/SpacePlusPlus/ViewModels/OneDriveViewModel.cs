using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows.Data;
using System.Windows.Input;
using Microsoft.Extensions.Logging;
using SpacePlusPlus.Infrastructure;
using SpacePlusPlus.Models;
using SpacePlusPlus.Services;

namespace SpacePlusPlus.ViewModels;

public sealed class OneDriveItemViewModel : ObservableObject
{
    private bool _isSelected;
    private OneDriveItem _item = null!;

    public OneDriveItem Item
    {
        get => _item;
        set
        {
            if (Set(ref _item, value))
            {
                OnPropertyChanged(nameof(Name));
                OnPropertyChanged(nameof(Path));
                OnPropertyChanged(nameof(RelativePath));
                OnPropertyChanged(nameof(SizeText));
                OnPropertyChanged(nameof(SizeBytes));
                OnPropertyChanged(nameof(StateText));
                OnPropertyChanged(nameof(IsOnlineOnly));
                OnPropertyChanged(nameof(IsPinned));
                OnPropertyChanged(nameof(IsLocallyAvailable));
                OnPropertyChanged(nameof(IsEligible));
            }
        }
    }

    public string Name => Item.Name;
    public string Path => Item.Path;
    public string RelativePath => Item.RelativePath;
    public string SizeText => SizeFormat.Format(Item.SizeBytes);
    public long SizeBytes => Item.SizeBytes;
    public string StateText => Item.State.Title();
    public bool IsOnlineOnly => Item.State == CloudState.OnlineOnly;
    public bool IsPinned => Item.State == CloudState.Pinned;
    public bool IsLocallyAvailable => Item.State == CloudState.LocallyAvailable;
    public bool IsEligible => Item.IsEligible;

    public bool IsSelected
    {
        get => _isSelected;
        set => Set(ref _isSelected, value);
    }
}

public sealed class OneDriveViewModel : ObservableObject, IPageLifecycle
{
    private readonly OneDriveService _oneDrive;
    private readonly DialogService _dialogs;
    private readonly ILogger<OneDriveViewModel> _logger;

    private bool _isAnalyzing;
    private bool _isDehydrating;
    private bool _analyzed;
    private string _statusText = "Analyzing…";
    private string _rootPath = string.Empty;
    private OneDriveSummary? _summary;
    private string _stateFilter = "All";
    private string _searchText = string.Empty;
    private string? _currentFile;
    private long _dehydrateDone;
    private long _dehydrateTotal;
    private string _resultSummary = string.Empty;
    private bool _cloudFilesAvailable = true;
    private CancellationTokenSource? _cts;

    public OneDriveViewModel(OneDriveService oneDrive, DialogService dialogs, ILogger<OneDriveViewModel> logger)
    {
        _oneDrive = oneDrive;
        _dialogs = dialogs;
        _logger = logger;

        AnalyzeCommand = new AsyncRelayCommand(AnalyzeAsync, () => !_isAnalyzing && !_isDehydrating);
        MakeOnlineOnlyCommand = new AsyncRelayCommand(MakeOnlineOnlyAsync, () => CanDehydrate);
        CancelCommand = new AsyncRelayCommand(() => { _cts?.Cancel(); return Task.CompletedTask; }, () => _isAnalyzing || _isDehydrating);
        SelectAllEligibleCommand = new RelayCommand(SelectAllEligible, () => Items.Count > 0);
        ClearSelectionCommand = new RelayCommand(() =>
        {
            foreach (var item in Items) item.IsSelected = false;
        });

        Items.CollectionChanged += (_, e) =>
        {
            if (e.Action == System.Collections.Specialized.NotifyCollectionChangedAction.Add && e.NewItems is not null)
            {
                foreach (var vm in e.NewItems.OfType<OneDriveItemViewModel>())
                    vm.PropertyChanged += (_, args) =>
                    {
                        if (args.PropertyName == nameof(OneDriveItemViewModel.IsSelected))
                            UpdateSelectionStats();
                    };
            }
            OnPropertyChanged(nameof(HasItems));
            UpdateSelectionStats();
        };
    }

    public ObservableCollection<OneDriveItemViewModel> Items { get; } = [];

    public AsyncRelayCommand AnalyzeCommand { get; }
    public AsyncRelayCommand MakeOnlineOnlyCommand { get; }
    public AsyncRelayCommand CancelCommand { get; }
    public ICommand SelectAllEligibleCommand { get; }
    public ICommand ClearSelectionCommand { get; }

    public bool IsAvailable => _oneDrive.IsAvailable;

    public bool CloudFilesAvailable
    {
        get => _cloudFilesAvailable;
        private set => Set(ref _cloudFilesAvailable, value);
    }

    public bool IsAnalyzing
    {
        get => _isAnalyzing;
        private set
        {
            if (Set(ref _isAnalyzing, value))
                CommandManager.InvalidateRequerySuggested();
        }
    }

    public bool IsDehydrating
    {
        get => _isDehydrating;
        private set
        {
            if (Set(ref _isDehydrating, value))
                CommandManager.InvalidateRequerySuggested();
        }
    }

    public bool IsBusy => IsAnalyzing || IsDehydrating;

    public bool HasAnalyzed
    {
        get => _analyzed;
        private set => Set(ref _analyzed, value);
    }

    public string StatusText
    {
        get => _statusText;
        private set => Set(ref _statusText, value);
    }

    public string RootPath
    {
        get => _rootPath;
        private set => Set(ref _rootPath, value);
    }

    public OneDriveSummary? Summary
    {
        get => _summary;
        private set
        {
            if (Set(ref _summary, value))
            {
                OnPropertyChanged(nameof(LocalAvailableText));
                OnPropertyChanged(nameof(OnlineOnlyText));
                OnPropertyChanged(nameof(PinnedText));
                OnPropertyChanged(nameof(ReclaimableText));
                OnPropertyChanged(nameof(LocalFilesText));
                OnPropertyChanged(nameof(OnlineFilesText));
                OnPropertyChanged(nameof(PinnedFilesText));
                OnPropertyChanged(nameof(ReclaimableFilesText));
                OnPropertyChanged(nameof(HasSummary));
            }
        }
    }

    public bool HasSummary => Summary is not null;

    public string LocalAvailableText => Summary is { } s ? SizeFormat.Format(s.LocalBytes) : "—";
    public string OnlineOnlyText => Summary is { } s ? SizeFormat.Format(s.OnlineOnlyBytes) : "—";
    public string PinnedText => Summary is { } s ? SizeFormat.Format(s.PinnedBytes) : "—";
    public string ReclaimableText => Summary is { } s ? SizeFormat.Format(s.ReclaimableBytes) : "—";
    public string LocalFilesText => Summary is { } s ? $"{SizeFormat.FormatCount(s.LocalFiles)} files" : "";
    public string OnlineFilesText => Summary is { } s ? $"{SizeFormat.FormatCount(s.OnlineOnlyFiles)} files" : "";
    public string PinnedFilesText => Summary is { } s ? $"{SizeFormat.FormatCount(s.PinnedFiles)} files" : "";
    public string ReclaimableFilesText => Summary is { } s ? $"{SizeFormat.FormatCount(s.ReclaimableFiles)} files" : "";

    public string StateFilter
    {
        get => _stateFilter;
        set
        {
            if (Set(ref _stateFilter, value))
                ItemsView?.Refresh();
        }
    }

    public IReadOnlyList<string> StateFilters { get; } = ["All", "Online-only", "Locally available", "Always kept", "Eligible"];

    public string SearchText
    {
        get => _searchText;
        set
        {
            if (Set(ref _searchText, value))
                ItemsView?.Refresh();
        }
    }

    public ICollectionView? ItemsView => CollectionViewSource.GetDefaultView(Items);

    public bool HasItems => Items.Count > 0;

    public long SelectedCount => Items.Count(i => i.IsSelected);

    public long SelectedBytes => Items.Where(i => i.IsSelected).Sum(i => i.SizeBytes);

    public string SelectedText => SelectedCount == 0
        ? "No files selected"
        : $"{SizeFormat.FormatCount(SelectedCount)} files — approx. {SizeFormat.Format(SelectedBytes)} of local storage can be reclaimed";

    public bool CanDehydrate => SelectedCount > 0 && !IsBusy && CloudFilesAvailable && _oneDrive.IsAvailable;

    public string DehydrateProgressText => _dehydrateTotal > 0
        ? $"Dehydrating… {_dehydrateDone}/{_dehydrateTotal}"
        : "Preparing…";

    public string CurrentFile => _currentFile ?? string.Empty;

    public string ResultSummary
    {
        get => _resultSummary;
        private set
        {
            if (Set(ref _resultSummary, value))
                OnPropertyChanged(nameof(HasResultSummary));
        }
    }

    public bool HasResultSummary => !string.IsNullOrEmpty(ResultSummary);

    public bool ListTruncated => Summary?.Truncated == true;

    public void OnActivated()
    {
        if (_oneDrive.IsAvailable && !_analyzed && !IsBusy)
            _ = AnalyzeAsync();
    }

    private bool FilterItem(object obj)
    {
        if (obj is not OneDriveItemViewModel vm) return false;
        if (!string.IsNullOrWhiteSpace(SearchText) &&
            !vm.Name.Contains(SearchText.Trim(), StringComparison.OrdinalIgnoreCase) &&
            !vm.RelativePath.Contains(SearchText.Trim(), StringComparison.OrdinalIgnoreCase))
            return false;
        return StateFilter switch
        {
            "Online-only" => vm.IsOnlineOnly,
            "Locally available" => vm.IsLocallyAvailable,
            "Always kept" => vm.IsPinned,
            "Eligible" => vm.IsEligible,
            _ => true
        };
    }

    private async Task AnalyzeAsync()
    {
        if (!_oneDrive.IsAvailable)
        {
            StatusText = "OneDrive was not detected on this system.";
            return;
        }
        if (IsBusy) return;

        IsAnalyzing = true;
        _cts = new CancellationTokenSource();
        try
        {
            RootPath = _oneDrive.RootPath ?? string.Empty;
            CloudFilesAvailable = _oneDrive.IsCloudFilesFilterEnabled();
            StatusText = "Reading file states…";
            var items = new List<OneDriveItem>();
            var progress = new Progress<string>(dir => StatusText = $"Reading {dir}");
            var summary = await _oneDrive.AnalyzeAsync(_cts.Token, progress, 4000, items);

            Summary = summary;
            Items.Clear();
            foreach (var item in items)
                Items.Add(new OneDriveItemViewModel { Item = item });
            ItemsView!.Filter = FilterItem;
            HasAnalyzed = true;

            StatusText = summary.Truncated
                ? $"Analysis complete — showing the first {items.Count} files. All totals cover the full folder."
                : "Analysis complete.";
        }
        catch (OperationCanceledException)
        {
            StatusText = "Analysis cancelled.";
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "OneDrive analysis failed");
            StatusText = $"Analysis failed: {ex.Message}";
        }
        finally
        {
            IsAnalyzing = false;
        }
    }

    private async Task MakeOnlineOnlyAsync()
    {
        var selected = Items.Where(i => i.IsSelected && i.IsEligible).Select(i => i.Item.Path).ToList();
        if (selected.Count == 0) return;

        long bytes = Items.Where(i => i.IsSelected && i.IsEligible).Sum(i => i.SizeBytes);
        bool confirmed = _dialogs.Confirm(
            "Free up local space",
            $"Make {selected.Count} file(s) — approximately {SizeFormat.Format(bytes)} — online-only?\n\n" +
            "The cloud copy stays untouched. Local copies are removed; you need an internet connection to open these files again.",
            "Files with unsynced changes cannot be dehydrated and are reported as failures.",
            "Free up space",
            danger: false);
        if (!confirmed) return;

        IsDehydrating = true;
        _cts = new CancellationTokenSource();
        _dehydrateDone = 0;
        _dehydrateTotal = selected.Count;
        OnPropertyChanged(nameof(DehydrateProgressText));
        try
        {
            long done = 0;
            var progress = new Progress<string>(file =>
            {
                done++;
                _dehydrateDone = done;
                _currentFile = file;
                OnPropertyChanged(nameof(DehydrateProgressText));
                OnPropertyChanged(nameof(CurrentFile));
            });

            var result = await _oneDrive.DehydrateManyAsync(selected, f => Task.Run(() => ((IProgress<string>)progress).Report(f)), _cts.Token);

            ResultSummary = result.Failed == 0
                ? $"{SizeFormat.FormatCount(result.Succeeded)} file(s) made online-only — approximately {SizeFormat.Format(result.BytesReclaimed)} of local storage reclaimed. Cloud copies are unaffected."
                : $"{SizeFormat.FormatCount(result.Succeeded)} of {result.Attempted} file(s) made online-only ({SizeFormat.Format(result.BytesReclaimed)} reclaimed). {result.Failed} failed: {result.FirstError}";

            // Update the visible item states.
            var succeeded = new HashSet<string>(selected, StringComparer.OrdinalIgnoreCase);
            var failedSet = new HashSet<string>();
            foreach (var vm in Items)
            {
                if (vm.IsEligible && succeeded.Contains(vm.Path))
                {
                    // Dehydrated items become online-only; the authoritative state comes from
                    // the next analysis, this is an immediate best-effort update.
                    vm.Item = vm.Item with { State = CloudState.OnlineOnly, IsEligible = false };
                    vm.IsSelected = false;
                }
            }
            ItemsView?.Refresh();

            await AnalyzeQuietAsync();
        }
        catch (OperationCanceledException)
        {
            ResultSummary = "Dehydration was cancelled.";
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Dehydration failed");
            StatusText = $"Dehydration failed: {ex.Message}";
        }
        finally
        {
            IsDehydrating = false;
        }
    }

    private async Task AnalyzeQuietAsync()
    {
        try
        {
            var items = new List<OneDriveItem>();
            var summary = await _oneDrive.AnalyzeAsync(new CancellationTokenSource(TimeSpan.FromSeconds(120)).Token, null, 4000, items);
            Summary = summary;
            Items.Clear();
            foreach (var item in items)
                Items.Add(new OneDriveItemViewModel { Item = item });
            ItemsView!.Filter = FilterItem;
            StatusText = "States refreshed after dehydration.";
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Post-dehydrate refresh failed");
        }
    }

    private void SelectAllEligible()
    {
        foreach (var vm in Items)
            vm.IsSelected = vm.IsEligible;
    }

    private void UpdateSelectionStats()
    {
        OnPropertyChanged(nameof(SelectedCount));
        OnPropertyChanged(nameof(SelectedBytes));
        OnPropertyChanged(nameof(SelectedText));
        OnPropertyChanged(nameof(CanDehydrate));
    }
}

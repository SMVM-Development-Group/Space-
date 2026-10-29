using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Input;
using Microsoft.Extensions.Logging;
using SpacePlusPlus.Infrastructure;
using SpacePlusPlus.Models;
using SpacePlusPlus.Services;

namespace SpacePlusPlus.ViewModels;

public sealed class CleanupGroupViewModel : ObservableObject
{
    private bool _isSelected;
    private string? _extraNote;

    public required CleanupGroup Group { get; init; }
    public required SmartCleanupViewModel Owner { get; init; }

    public string Id => Group.Id;
    public string Title => Group.Title;
    public string Description => Group.Description;
    public string CategoryLabel => Group.CategoryLabel;
    public bool RequiresAdmin => Group.RequiresAdmin;
    public string? Warning => Group.Warning;
    public string? LockedReason => Group.LockedReason;
    public IReadOnlyList<string> SamplePaths => Group.SamplePaths;
    public bool HasSamples => Group.SamplePaths.Count > 0;

    public string SizeText => Group.EstimatedBytes is { } b ? SizeFormat.Format(b) : "Analyze to estimate";
    public string FilesText => Group.EstimatedFiles is { } f ? $"{SizeFormat.FormatCount(f)} items" : "—";

    public string SafetyText => Group.SafetyText;
    public bool IsSafe => Group.Safety == CleanupSafety.Safe && !Group.RequiresAdmin;
    public bool IsBlocked => LockedReason is not null;

    public string ExtraNote
    {
        get => _extraNote ?? string.Empty;
        set
        {
            if (Set(ref _extraNote, value))
                OnPropertyChanged(nameof(HasExtraNote));
        }
    }

    public bool HasExtraNote => !string.IsNullOrEmpty(ExtraNote);

    public bool IsSelected
    {
        get => _isSelected;
        set => Set(ref _isSelected, value);
    }
}

public sealed class SmartCleanupViewModel : ObservableObject, IPageLifecycle
{
    private readonly CleanupService _cleanup;
    private readonly WindowsUpdateService _windowsUpdate;
    private readonly DialogService _dialogs;
    private readonly ILogger<SmartCleanupViewModel> _logger;

    private bool _isDetecting;
    private bool _isCleaning;
    private string _progressText = string.Empty;
    private long _progressBytes;
    private string _currentItem = string.Empty;
    private string _detectedAtText = string.Empty;
    private bool _resetBase;
    private CleanupRunResult? _lastResult;
    private CancellationTokenSource? _cleanCts;

    public SmartCleanupViewModel(
        CleanupService cleanup,
        WindowsUpdateService windowsUpdate,
        DialogService dialogs,
        ILogger<SmartCleanupViewModel> logger)
    {
        _cleanup = cleanup;
        _windowsUpdate = windowsUpdate;
        _dialogs = dialogs;
        _logger = logger;

        DetectCommand = new AsyncRelayCommand(DetectAsync, () => !_isDetecting && !_isCleaning);
        CleanSelectedCommand = new AsyncRelayCommand(CleanSelectedAsync, () => CanClean);
        CancelCleanCommand = new AsyncRelayCommand(() => { _cleanCts?.Cancel(); return Task.CompletedTask; }, () => _isCleaning);
        AnalyzeComponentStoreCommand = new AsyncRelayCommand(AnalyzeComponentStoreAsync, () => !_isDetecting);

        _cleanup.DetectionChanged += () => Application.Current?.Dispatcher.BeginInvoke(() =>
        {
            RebuildGroups();
            OnPropertyChanged(nameof(DetectedAtText));
        });
    }

    public ObservableCollection<CleanupGroupViewModel> Groups { get; } = [];

    public AsyncRelayCommand DetectCommand { get; }
    public AsyncRelayCommand CleanSelectedCommand { get; }
    public AsyncRelayCommand CancelCleanCommand { get; }
    public AsyncRelayCommand AnalyzeComponentStoreCommand { get; }

    public bool IsDetecting
    {
        get => _isDetecting;
        private set
        {
            if (Set(ref _isDetecting, value))
                CommandManager.InvalidateRequerySuggested();
        }
    }

    public bool IsCleaning
    {
        get => _isCleaning;
        private set
        {
            if (Set(ref _isCleaning, value))
                CommandManager.InvalidateRequerySuggested();
        }
    }

    public string ProgressText
    {
        get => _progressText;
        private set => Set(ref _progressText, value);
    }

    public string CurrentItem
    {
        get => _currentItem;
        private set => Set(ref _currentItem, value);
    }

    public string ProgressBytesText => SizeFormat.Format(_progressBytes);

    public string DetectedAtText => _cleanup.DetectedAt is { } t
        ? $"Detected {t:HH:mm:ss} — detection reads locations, it never deletes anything."
        : "Not detected yet.";

    public long SelectedBytes => Groups
        .Where(g => g.IsSelected && g.Group.EstimatedBytes is { } b)
        .Sum(g => g.Group.EstimatedBytes!.Value);

    public int SelectedCount => Groups.Count(g => g.IsSelected);

    public bool CanClean => !IsCleaning && !IsDetecting && SelectedCount > 0
        && SelectedBytes > 0
        && Groups.Where(g => g.IsSelected).All(g => g.Group.EstimatedBytes is not null);

    public string SelectedTotalText => SizeFormat.Format(SelectedBytes);

    public bool HasResults => _lastResult is not null;

    public bool ResetBase
    {
        get => _resetBase;
        set => Set(ref _resetBase, value);
    }

    public void OnActivated()
    {
        if (_cleanup.CurrentGroups is null && !IsDetecting)
            _ = DetectAsync();
        else
            RebuildGroups();
        OnPropertyChanged(nameof(DetectedAtText));
    }

    private async Task DetectAsync()
    {
        if (IsDetecting) return;
        IsDetecting = true;
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(180));
            await _cleanup.DetectAsync(cts.Token);
        }
        catch (OperationCanceledException)
        {
            StatusNote("Detection was cancelled.");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Cleanup detection failed");
            StatusNote($"Detection failed: {ex.Message}");
        }
        finally
        {
            IsDetecting = false;
        }
    }

    private void StatusNote(string text)
    {
        ProgressText = text;
    }

    private void RebuildGroups()
    {
        var selection = Groups.ToDictionary(g => g.Id, g => g.IsSelected);
        Groups.Clear();
        if (_cleanup.CurrentGroups is null) return;

        foreach (var group in _cleanup.CurrentGroups)
        {
            var vm = new CleanupGroupViewModel { Group = group, Owner = this };
            vm.IsSelected = group.Safety == CleanupSafety.Safe && !group.RequiresAdmin && group.LockedReason is null;
            if (selection.TryGetValue(group.Id, out var previous)) vm.IsSelected = previous;
            if (group.Id == "windows-update" && _cleanup.ComponentStoreBytes is { } b)
            {
                vm.ExtraNote = $"DISM analysis: component store is about {SizeFormat.Format(b)}" +
                               (_cleanup.ComponentStoreCleanupRecommended ? " — cleanup recommended by Windows." : ".");
            }
            Groups.Add(vm);
        }
        foreach (var g in Groups)
            g.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(CleanupGroupViewModel.IsSelected))
                {
                    OnPropertyChanged(nameof(SelectedBytes));
                    OnPropertyChanged(nameof(SelectedTotalText));
                    OnPropertyChanged(nameof(SelectedCount));
                    OnPropertyChanged(nameof(CanClean));
                }
            };
        OnPropertyChanged(nameof(SelectedBytes));
        OnPropertyChanged(nameof(SelectedTotalText));
        OnPropertyChanged(nameof(SelectedCount));
        OnPropertyChanged(nameof(CanClean));
    }

    private async Task AnalyzeComponentStoreAsync()
    {
        try
        {
            ProgressText = "Analyzing the Windows component store (elevated DISM run — respond to the UAC prompt)…";
            var analysis = await _cleanup.AnalyzeComponentStoreAsync(CancellationToken.None);
            if (analysis is { HasData: true })
            {
                string size = analysis.SizeBytes is { } b ? SizeFormat.Format(b) : "unknown";
                ProgressText = $"Component store: approx. {size}. Cleanup recommended by Windows: {(analysis.CleanupRecommended is { } r ? (r ? "Yes" : "No") : "output not parseable on this Windows language")}.";
            }
            else
            {
                ProgressText = analysis?.Error ?? "The component store could not be analyzed.";
            }
            RebuildGroups();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Component store analysis failed");
            ProgressText = $"Analysis failed: {ex.Message}";
        }
    }

    private async Task CleanSelectedAsync()
    {
        var selected = Groups.Where(g => g.IsSelected).Select(g => g.Group).ToList();
        if (selected.Count == 0) return;

        string warningText = string.Join("\n", selected
            .Where(g => g.Warning is not null)
            .Select(g => $"• {g.Title}: {g.Warning}"));

        bool confirmed = _dialogs.Confirm(
            "Clean selected categories",
            $"Remove approximately {SizeFormat.Format(SelectedBytes)} across {selected.Count} categorie(s)?\n\nDeleted cache and temporary files are re-created by their applications. Permanently deleted items cannot be recovered.",
            string.IsNullOrEmpty(warningText) ? null : warningText,
            $"Clean {selected.Count} categorie(s)",
            danger: true);
        if (!confirmed) return;

        IsCleaning = true;
        _cleanCts = new CancellationTokenSource();
        try
        {
            var progress = new Progress<CleanupProgressReport>(p =>
            {
                ProgressText = p.GroupTitle;
                CurrentItem = p.CurrentItem ?? string.Empty;
                _progressBytes = p.BytesFreed;
                OnPropertyChanged(nameof(ProgressBytesText));
            });

            bool needsElevation = selected.Any(g => g.RequiresAdmin) && !WindowsUpdateService.IsElevated;
            if (needsElevation)
            {
                var elevatedResult = await RunElevatedCleanupAsync(selected, _cleanCts.Token);
                _lastResult = elevatedResult;
            }
            else
            {
                _lastResult = await _cleanup.CleanAsync(selected, progress, _cleanCts.Token);
            }

            ShowResults(cancelled: false);
        }
        catch (OperationCanceledException)
        {
            ShowResults(cancelled: true);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Cleanup failed");
            _dialogs.Info("Cleanup failed", ex.Message, error: true);
        }
        finally
        {
            IsCleaning = false;
            ProgressText = string.Empty;
            CurrentItem = string.Empty;
            OnPropertyChanged(nameof(HasResults));
            RebuildGroups();
        }
    }

    private async Task<CleanupRunResult> RunElevatedCleanupAsync(IReadOnlyList<CleanupGroup> groups, CancellationToken ct)
    {
        // Categories that need elevation (Windows temp, system dumps, Windows Update)
        // are executed by an elevated Space++ instance; user-temp style groups that do
        // not need it run directly in this instance.
        var direct = groups.Where(g => !g.RequiresAdmin).ToList();
        var elevated = groups.Where(g => g.RequiresAdmin).ToList();

        var results = new List<CleanupResultItem>();
        long total = 0;

        if (direct.Count > 0)
        {
            var run = await _cleanup.CleanAsync(direct, null, ct);
            results.AddRange(run.Items);
            total += run.TotalFreedBytes;
        }

        if (elevated.Count > 0)
        {
            string jobPath = Path.Combine(Path.GetTempPath(), $"SpacePlusPlus-job-{Guid.NewGuid():N}.json");
            var job = new { Groups = elevated.Select(g => g.Id).ToList() };
            File.WriteAllText(jobPath, System.Text.Json.JsonSerializer.Serialize(job));

            var psi = new System.Diagnostics.ProcessStartInfo
            {
                FileName = Environment.ProcessPath ?? "SpacePlusPlus",
                Arguments = $"--cleanup-job:{jobPath}",
                UseShellExecute = true,
                Verb = "runas"
            };
            using var process = System.Diagnostics.Process.Start(psi)
                ?? throw new InvalidOperationException("The elevated cleanup process could not be started.");
            await process.WaitForExitAsync(ct).WaitAsync(TimeSpan.FromHours(2), ct);

            string resultFile = jobPath + ".result";
            if (File.Exists(resultFile))
            {
                var json = File.ReadAllText(resultFile);
                var doc = System.Text.Json.JsonDocument.Parse(json);
                if (doc.RootElement.TryGetProperty("Items", out var items) && items.ValueKind == System.Text.Json.JsonValueKind.Array)
                {
                    foreach (var item in items.EnumerateArray())
                    {
                        string id = item.GetProperty("GroupId").GetString() ?? "";
                        string title = item.GetProperty("Title").GetString() ?? "";
                        long freed = item.TryGetProperty("FreedBytes", out var fb) && fb.ValueKind == System.Text.Json.JsonValueKind.Number ? fb.GetInt64() : 0;
                        long deleted = item.TryGetProperty("DeletedFiles", out var df) && df.ValueKind == System.Text.Json.JsonValueKind.Number ? df.GetInt64() : 0;
                        long skipped = item.TryGetProperty("SkippedFiles", out var sf) && sf.ValueKind == System.Text.Json.JsonValueKind.Number ? sf.GetInt64() : 0;
                        long failed = item.TryGetProperty("FailedFiles", out var ff) && ff.ValueKind == System.Text.Json.JsonValueKind.Number ? ff.GetInt64() : 0;
                        string? notes = item.TryGetProperty("Notes", out var n) && n.ValueKind == System.Text.Json.JsonValueKind.String ? n.GetString() : null;
                        bool success = item.TryGetProperty("Success", out var s) && s.GetBoolean();
                        results.Add(new CleanupResultItem(id, title, freed, deleted, skipped, failed, notes, success));
                        total += freed;
                    }
                }
                try { File.Delete(jobPath); File.Delete(resultFile); } catch { }
            }
            else
            {
                results.Add(new CleanupResultItem("elevated", "Elevated cleanup", 0, 0, 0, 1,
                    "The elevated process did not return results.", false));
            }
        }

        var record = new CleanupRecord(Guid.NewGuid(), DateTime.UtcNow,
            results.Select(i => new CleanupGroupStat(i.Title, i.FreedBytes, i.DeletedFiles, i.FailedFiles)).ToList(), total);
        await _cleanup.RecordCleanupAsync(record);

        return new CleanupRunResult(results, total);
    }

    private void ShowResults(bool cancelled)
    {
        if (_lastResult is not { } result) return;
        _dialogs.ShowCleanupResults(result.Items, result.TotalFreedBytes, cancelled);
    }
}

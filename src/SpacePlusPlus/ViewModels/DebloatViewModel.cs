using System.Collections.ObjectModel;
using System.Windows.Input;
using Microsoft.Extensions.Logging;
using SpacePlusPlus.Infrastructure;
using SpacePlusPlus.Models;
using SpacePlusPlus.Services;

namespace SpacePlusPlus.ViewModels;

public sealed class BloatAppViewModel : ObservableObject
{
    public required BloatApp App { get; init; }

    private bool _isSelected;
    public bool IsSelected { get => _isSelected; set => Set(ref _isSelected, value); }

    public string Name => App.Name;
    public string Category => App.Category;
    public bool Caution => App.Caution;
    public string SizeText => App.SizeBytes > 0 ? SizeFormat.Format(App.SizeBytes) : "—";
}

public sealed class StartupEntryViewModel : ObservableObject
{
    private StartupEntry _entry;

    public StartupEntryViewModel(StartupEntry entry) => _entry = entry;

    public StartupEntry Entry
    {
        get => _entry;
        set
        {
            if (!ReferenceEquals(_entry, value))
            {
                _entry = value;
                // All display properties derive from Entry.
                OnPropertyChanged(string.Empty);
            }
        }
    }

    private bool _isSelected;
    public bool IsSelected { get => _isSelected; set => Set(ref _isSelected, value); }

    public string Name => Entry.Name;
    public string SourceText => Entry.Source == StartupSource.RunKey ? "Registry (Run)" : "Startup folder";
    public string Command => Entry.Command;
    public string SizeText => Entry.SizeBytes > 0 ? SizeFormat.Format(Entry.SizeBytes) : "";
    public string StateText => Entry.CurrentlyDisabled ? "Disabled" : "Active";
    public bool IsDisabled => Entry.CurrentlyDisabled;
}

public sealed class WindowsTweakViewModel : ObservableObject
{
    private WindowsTweak _tweak;
    private bool _isSelected;

    public WindowsTweakViewModel(WindowsTweak tweak) => _tweak = tweak;
    public WindowsTweak Tweak { get => _tweak; set { _tweak = value; OnPropertyChanged(string.Empty); } }
    public bool IsSelected { get => _isSelected; set => Set(ref _isSelected, value); }
    public string Title => Tweak.Title;
    public string Description => Tweak.Description;
    public string Category => Tweak.Category;
    public bool Caution => Tweak.Caution;
    public bool IsApplied => Tweak.IsApplied;
    public string StateText => IsApplied ? "Applied" : "Available";
}

public sealed class DebloatViewModel : ObservableObject, IPageLifecycle
{
    private readonly DebloatService _debloat;
    private readonly DialogService _dialogs;
    private readonly ScanHistoryService _history;
    private readonly ILogger<DebloatViewModel> _logger;

    private bool _isBusy;
    private string _statusText = "Analyzing — nothing is changed until you confirm an action.";
    private string _currentItem = "";
    private DebloatSnapshot? _beforeSnapshot;
    private DebloatOutcome? _lastOutcome;
    private bool _analyzed;

    public DebloatViewModel(DebloatService debloat, DialogService dialogs, ScanHistoryService history,
        ILogger<DebloatViewModel> logger)
    {
        _debloat = debloat;
        _dialogs = dialogs;
        _history = history;
        _logger = logger;

        AnalyzeCommand = new AsyncRelayCommand(() => AnalyzeAsync());
        RemoveSelectedCommand = new AsyncRelayCommand(RemoveSelectedAsync,
            () => !IsBusy && SelectedAppCount > 0);
        DisableSelectedCommand = new AsyncRelayCommand(DisableSelectedAsync,
            () => !IsBusy && Startup.Any(s => s.IsSelected && !s.IsDisabled));
        EnableSelectedCommand = new AsyncRelayCommand(EnableSelectedAsync,
            () => !IsBusy && Startup.Any(s => s.IsSelected && s.IsDisabled));
        ApplySelectedTweaksCommand = new AsyncRelayCommand(() => SetTweaksAsync(apply: true),
            () => !IsBusy && Tweaks.Any(t => t.IsSelected && !t.IsApplied));
        RestoreSelectedTweaksCommand = new AsyncRelayCommand(() => SetTweaksAsync(apply: false),
            () => !IsBusy && Tweaks.Any(t => t.IsSelected && t.IsApplied));
        SelectAllAppsCommand = new RelayCommand(() => SetAll(Apps, true), () => HasApps && !IsBusy);
        ClearAppsCommand = new RelayCommand(() => SetAll(Apps, false), () => HasApps && !IsBusy);
        SelectAllStartupCommand = new RelayCommand(() => SetAll(Startup, true), () => HasStartup && !IsBusy);
        ClearStartupCommand = new RelayCommand(() => SetAll(Startup, false), () => HasStartup && !IsBusy);

        Apps.CollectionChanged += (_, e) => WireSelection<BloatAppViewModel>(e, nameof(BloatAppViewModel.IsSelected));
        Startup.CollectionChanged += (_, e) => WireSelection<StartupEntryViewModel>(e, nameof(StartupEntryViewModel.IsSelected));
        Tweaks.CollectionChanged += (_, e) => WireSelection<WindowsTweakViewModel>(e, nameof(WindowsTweakViewModel.IsSelected));
    }

    public ObservableCollection<BloatAppViewModel> Apps { get; } = [];
    public ObservableCollection<StartupEntryViewModel> Startup { get; } = [];
    public ObservableCollection<WindowsTweakViewModel> Tweaks { get; } = [];

    public ICommand AnalyzeCommand { get; }
    public ICommand RemoveSelectedCommand { get; }
    public ICommand DisableSelectedCommand { get; }
    public ICommand EnableSelectedCommand { get; }
    public ICommand SelectAllAppsCommand { get; }
    public ICommand ClearAppsCommand { get; }
    public ICommand SelectAllStartupCommand { get; }
    public ICommand ClearStartupCommand { get; }
    public ICommand ApplySelectedTweaksCommand { get; }
    public ICommand RestoreSelectedTweaksCommand { get; }

    public bool IsBusy { get => _isBusy; private set { if (Set(ref _isBusy, value)) CommandManager.InvalidateRequerySuggested(); } }
    public string StatusText { get => _statusText; private set => Set(ref _statusText, value); }
    public string CurrentItem { get => _currentItem; private set => Set(ref _currentItem, value); }

    public bool HasApps => Apps.Count > 0;
    public bool HasStartup => Startup.Count > 0;
    public bool HasTweaks => Tweaks.Count > 0;
    public bool HasNothing => !HasApps && !HasStartup && !HasTweaks && !IsBusy && _analyzed;

    // These values deliberately derive from the current collection. The former
    // stored values were never updated when a checkbox changed, which left the
    // remove command permanently disabled.
    public int SelectedAppCount => Apps.Count(a => a.IsSelected);
    public long SelectedAppBytes => Apps.Where(a => a.IsSelected).Sum(a => a.App.SizeBytes);
    public string SelectedAppText => SelectedAppCount == 0
        ? "No apps selected"
        : $"{SelectedAppCount} app(s) selected — {SizeFormat.Format(SelectedAppBytes)} on disk";

    public string SelectedStartupText
    {
        get
        {
            int sel = Startup.Count(s => s.IsSelected);
            return sel == 0 ? "No entries selected" : $"{sel} autostart entry(ies) selected";
        }
    }

    public string SelectedTweakText
    {
        get
        {
            int selected = Tweaks.Count(t => t.IsSelected);
            return selected == 0 ? "No Windows settings selected" : $"{selected} Windows setting(s) selected";
        }
    }

    public int DisabledCount => Startup.Count(s => s.IsDisabled);
    public int ActiveStartupCount => Startup.Count - DisabledCount;
    public bool HasStartupEntries => Startup.Any(s => !s.IsDisabled);
    public bool HasDisabledEntries => DisabledCount > 0;

    public DebloatOutcome? LastOutcome
    {
        get => _lastOutcome;
        private set
        {
            if (Set(ref _lastOutcome, value))
            {
                OnPropertyChanged(nameof(HasOutcome));
                OnPropertyChanged(nameof(ImprovementLines));
                OnPropertyChanged(nameof(OutcomeSummary));
            }
        }
    }

    public bool HasOutcome => LastOutcome is not null;

    public string OutcomeSummary => LastOutcome?.SummaryText ?? "";

    /// <summary>Before → after table for the improvement card. Real, verified numbers only.</summary>
    public IReadOnlyList<(string Label, string Before, string After)> ImprovementLines
    {
        get
        {
            var list = new List<(string, string, string)>();
            if (LastOutcome is not { } o) return list;

            list.Add(("Bloat apps installed",
                o.Before.BloatAppCount.ToString("N0"),
                o.After.BloatAppCount.ToString("N0")));
            list.Add(("Bloat on disk", SizeFormat.Format(o.Before.BloatAppBytes), SizeFormat.Format(o.After.BloatAppBytes)));
            list.Add(("Active autostart entries",
                o.Before.ActiveStartupCount.ToString("N0"),
                o.After.ActiveStartupCount.ToString("N0")));
            if (o.RunningProcessesDetected > 0)
                list.Add(("Related processes at removal",
                    o.RunningProcessesDetected.ToString("N0"),
                    "Removal requested"));
            if (o.StartupDisabled > 0)
                list.Add(("Autostart entries disabled", "—", o.StartupDisabled.ToString("N0")));
            list.Add(("Disk space freed", "—", SizeFormat.Format(o.FreedBytes)));
            return list;
        }
    }

    public async void OnActivated()
    {
        if (!_analyzed && !IsBusy)
            await AnalyzeAsync();
    }

    private async Task AnalyzeAsync()
    {
        if (IsBusy) return;
        IsBusy = true;
        CurrentItem = "";
        try
        {
            var progress = new Progress<string>(s => CurrentItem = s);
            var result = await _debloat.AnalyzeAsync(progress, CancellationToken.None);
            _beforeSnapshot ??= result.Snapshot;

            Apps.Clear();
            foreach (var a in result.Apps) Apps.Add(new BloatAppViewModel { App = a });
            Startup.Clear();
            foreach (var s in result.StartupEntries) Startup.Add(new StartupEntryViewModel(s));
            Tweaks.Clear();
            foreach (var t in result.Tweaks) Tweaks.Add(new WindowsTweakViewModel(t));

            _analyzed = true;
            OnListMembershipChanged();

            long bytes = result.Apps.Sum(a => a.SizeBytes);
            StatusText = result.Apps.Count == 0 && result.StartupEntries.Count == 0
                ? "No preinstalled bloat and no user autostart entries found — this system is already clean."
                : $"{result.Apps.Count} preinstalled app(s) ({SizeFormat.Format(bytes)}) and {result.StartupEntries.Count} autostart entr(y/ies) found.";
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Debloat analysis failed");
            StatusText = "Analysis failed: " + ex.Message;
        }
        finally
        {
            IsBusy = false;
            CurrentItem = "";
            CommandManager.InvalidateRequerySuggested();
        }
    }

    private async Task RemoveSelectedAsync()
    {
        var selected = Apps.Where(a => a.IsSelected).Select(a => a.App).ToList();
        if (selected.Count == 0) return;

        long bytes = selected.Sum(a => a.SizeBytes);
        var caution = selected.Where(a => a.Caution).ToList();
        string detail = $"{selected.Count} app(s) — {SizeFormat.Format(bytes)}\n\n" +
                        string.Join("\n", selected.Select(a => "• " + a.Name));
        if (caution.Count > 0)
            detail += "\n\nCaution: " + string.Join(", ", caution.Select(c => c.Name)) +
                      "\nThese can affect Windows features (e.g. Xbox/game services). They can usually be reinstalled from the Microsoft Store.";

        bool confirmed = _dialogs.Confirm(
            "Remove preinstalled apps",
            $"Remove these apps for the current user?\n\n{detail}",
            null,
            "Remove apps",
            danger: true);
        if (!confirmed) return;

        IsBusy = true;
        try
        {
            var before = _beforeSnapshot ?? new DebloatSnapshot(Apps.Count, Apps.Sum(a => a.App.SizeBytes), ActiveStartupCount);
            var progress = new Progress<string>(s => CurrentItem = s);
            var run = await _debloat.RemoveAppsAsync(selected, progress, CancellationToken.None);
            await RecordHistoryAsync(run);

            // Verified after-state from a fresh analysis.
            var recheck = await _debloat.AnalyzeAsync(null, CancellationToken.None);
            var after = new DebloatSnapshot(recheck.Apps.Count, recheck.Apps.Sum(a => a.SizeBytes),
                recheck.StartupEntries.Count(s => !s.CurrentlyDisabled));

            var outcome = new DebloatOutcome(
                run.Items, run.FreedBytes, run.Succeeded, run.Failed, 0, 0,
                run.RunningProcessesDetected, run.ProcessMemoryBytes, before, after);
            LastOutcome = outcome;
            _beforeSnapshot = after;

            var removedNames = run.Items.Where(i => i.Success).Select(i => i.Title).ToHashSet(StringComparer.OrdinalIgnoreCase);
            for (int i = Apps.Count - 1; i >= 0; i--)
                if (removedNames.Contains(Apps[i].Name))
                    Apps.RemoveAt(i);
            OnListMembershipChanged();

            StatusText = outcome.SummaryText + " — effects on memory and boot appear after a restart.";
            _dialogs.ShowCleanupResults(
                run.Items.Select(i => new CleanupResultItem("debloat", i.Title, i.FreedBytes, i.Success ? 1 : 0, 0,
                    i.Success ? 0 : 1, i.Error, i.Success)).ToList(),
                run.FreedBytes, cancelled: false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Debloat removal failed");
            StatusText = "Removal failed: " + ex.Message;
        }
        finally
        {
            IsBusy = false;
            CurrentItem = "";
            CommandManager.InvalidateRequerySuggested();
        }
    }

    private async Task DisableSelectedAsync()
    {
        var selected = Startup.Where(s => s.IsSelected && !s.IsDisabled).Select(s => s.Entry).ToList();
        if (selected.Count == 0) return;

        string detail = string.Join("\n", selected.Select(s =>
            $"• {s.Name}  ({(s.Source == StartupSource.RunKey ? "Registry (Run)" : "Startup folder")})"));
        bool confirmed = _dialogs.Confirm(
            "Disable autostart entries",
            $"Disable these autostart entries? They stay in place and can be re-enabled at any time.\n\n{detail}",
            null,
            "Disable",
            danger: false);
        if (!confirmed) return;

        IsBusy = true;
        try
        {
            var before = _beforeSnapshot ?? new DebloatSnapshot(Apps.Count, Apps.Sum(a => a.App.SizeBytes), ActiveStartupCount);
            var progress = new Progress<string>(s => CurrentItem = s);
            var run = _debloat.DisableStartup(selected, progress, CancellationToken.None);

            var recheck = await _debloat.AnalyzeAsync(null, CancellationToken.None);
            var after = new DebloatSnapshot(recheck.Apps.Count, recheck.Apps.Sum(a => a.SizeBytes),
                recheck.StartupEntries.Count(s => !s.CurrentlyDisabled));

            LastOutcome = new DebloatOutcome(run.Items, 0, 0, 0, run.Succeeded, 0, 0, 0, before, after);
            _beforeSnapshot = after;

            var okNames = run.Items.Where(i => i.Success).Select(i => i.Title).ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var vm in Startup.Where(v => okNames.Contains(v.Name)))
                vm.Entry = vm.Entry with { CurrentlyDisabled = true };
            OnListMembershipChanged();

            StatusText = outcomeSummary(run) + " — takes full effect after a restart.";
            _dialogs.ShowCleanupResults(
                run.Items.Select(i => new CleanupResultItem("debloat-startup", i.Title, 0, 0, 0,
                    i.Success ? 0 : 1, i.Error, i.Success)).ToList(),
                0, cancelled: false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Disabling startup entries failed");
            StatusText = "Disabling failed: " + ex.Message;
        }
        finally
        {
            IsBusy = false;
            CurrentItem = "";
            CommandManager.InvalidateRequerySuggested();
        }
    }

    private async Task EnableSelectedAsync()
    {
        var selected = Startup.Where(s => s.IsSelected && s.IsDisabled).Select(s => s.Entry).ToList();
        if (selected.Count == 0) return;

        IsBusy = true;
        try
        {
            var progress = new Progress<string>(s => CurrentItem = s);
            var run = _debloat.EnableStartup(selected, progress, CancellationToken.None);

            var recheck = await _debloat.AnalyzeAsync(null, CancellationToken.None);
            var after = new DebloatSnapshot(recheck.Apps.Count, recheck.Apps.Sum(a => a.SizeBytes),
                recheck.StartupEntries.Count(s => !s.CurrentlyDisabled));
            if (LastOutcome is { } prev)
                LastOutcome = prev with { Items = run.Items, StartupEnabled = run.Succeeded, After = after };
            _beforeSnapshot = after;

            var okNames = run.Items.Where(i => i.Success).Select(i => i.Title).ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var vm in Startup.Where(v => okNames.Contains(v.Name)))
                vm.Entry = vm.Entry with { CurrentlyDisabled = false };
            OnListMembershipChanged();

            StatusText = $"{run.Succeeded} autostart entry(ies) re-enabled.";
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Enabling startup entries failed");
            StatusText = "Re-enabling failed: " + ex.Message;
        }
        finally
        {
            IsBusy = false;
            CurrentItem = "";
            CommandManager.InvalidateRequerySuggested();
        }
    }

    private async Task SetTweaksAsync(bool apply)
    {
        var selected = Tweaks.Where(t => t.IsSelected && t.IsApplied != apply).Select(t => t.Tweak).ToList();
        if (selected.Count == 0) return;

        string action = apply ? "Apply" : "Restore";
        string detail = string.Join("\n", selected.Select(t => "• " + t.Title));
        if (!_dialogs.Confirm($"{action} Windows settings",
                $"{action} these per-user Windows settings? They can be reversed here at any time.\n\n{detail}",
                null, action, danger: false)) return;

        IsBusy = true;
        try
        {
            var run = _debloat.SetTweaks(selected, apply, new Progress<string>(s => CurrentItem = s), CancellationToken.None);
            var result = await _debloat.AnalyzeAsync(null, CancellationToken.None);
            var byId = result.Tweaks.ToDictionary(t => t.Id);
            foreach (var vm in Tweaks)
                if (byId.TryGetValue(vm.Tweak.Id, out var refreshed)) vm.Tweak = refreshed;
            OnListMembershipChanged();
            StatusText = run.Failed == 0
                ? $"{run.Succeeded} Windows setting(s) {(apply ? "applied" : "restored")}. Some changes take effect after signing out."
                : $"{run.Succeeded} changed, {run.Failed} could not be changed.";
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed changing debloat settings");
            StatusText = $"{action} failed: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
            CurrentItem = "";
            CommandManager.InvalidateRequerySuggested();
        }
    }

    private static string outcomeSummary(DebloatService.DebloatRunResult run)
        => run.Failed == 0
            ? $"{run.Succeeded} autostart entry(ies) disabled."
            : $"{run.Succeeded} disabled, {run.Failed} failed.";

    private async Task RecordHistoryAsync(DebloatService.DebloatRunResult run)
    {
        try
        {
            var record = new CleanupRecord(Guid.NewGuid(), DateTime.UtcNow,
                run.Items.Select(i => new CleanupGroupStat(i.Title, i.FreedBytes, i.Success ? 1 : 0, i.Success ? 0 : 1)).ToList(),
                run.FreedBytes);
            await _history.AddCleanupAsync(record);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed recording debloat run in history");
        }
    }

    private void WireSelection<T>(System.Collections.Specialized.NotifyCollectionChangedEventArgs e, string property)
        where T : ObservableObject
    {
        if (e.Action == System.Collections.Specialized.NotifyCollectionChangedAction.Add && e.NewItems is not null)
            foreach (var vm in e.NewItems.OfType<T>())
                vm.PropertyChanged += (_, args) =>
                {
                    if (args.PropertyName == property)
                    {
                        OnPropertyChanged(nameof(SelectedAppText));
                        OnPropertyChanged(nameof(SelectedAppCount));
                        OnPropertyChanged(nameof(SelectedAppBytes));
                        OnPropertyChanged(nameof(SelectedStartupText));
                        OnPropertyChanged(nameof(SelectedTweakText));
                        CommandManager.InvalidateRequerySuggested();
                    }
                };
        OnListMembershipChanged();
    }

    private void OnListMembershipChanged()
    {
        OnPropertyChanged(nameof(HasApps));
        OnPropertyChanged(nameof(HasStartup));
        OnPropertyChanged(nameof(HasTweaks));
        OnPropertyChanged(nameof(HasNothing));
        OnPropertyChanged(nameof(DisabledCount));
        OnPropertyChanged(nameof(ActiveStartupCount));
        OnPropertyChanged(nameof(HasStartupEntries));
        OnPropertyChanged(nameof(HasDisabledEntries));
        OnPropertyChanged(nameof(SelectedAppText));
        OnPropertyChanged(nameof(SelectedAppCount));
        OnPropertyChanged(nameof(SelectedAppBytes));
        OnPropertyChanged(nameof(SelectedStartupText));
        OnPropertyChanged(nameof(SelectedTweakText));
        CommandManager.InvalidateRequerySuggested();
    }

    private static void SetAll<T>(IEnumerable<T> items, bool value) where T : ObservableObject
    {
        foreach (var item in items)
        {
            switch (item)
            {
                case BloatAppViewModel b: b.IsSelected = value; break;
                case StartupEntryViewModel s: s.IsSelected = value; break;
                case WindowsTweakViewModel t: t.IsSelected = value; break;
            }
        }
    }
}

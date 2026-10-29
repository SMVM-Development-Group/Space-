using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows;
using System.Windows.Data;
using System.Windows.Input;
using Microsoft.Extensions.Logging;
using SpacePlusPlus.Infrastructure;
using SpacePlusPlus.Models;
using SpacePlusPlus.Services;

namespace SpacePlusPlus.ViewModels;

public sealed class ApplicationsViewModel : ObservableObject, IPageLifecycle
{
    private readonly ApplicationInventoryService _inventory;
    private readonly DialogService _dialogs;
    private readonly ILogger<ApplicationsViewModel> _logger;

    private ObservableCollection<InstalledApp> _apps = [];
    private ICollectionView? _appsView;
    private string _filterText = string.Empty;
    private bool _includeSystemComponents;
    private bool _isMeasuring;
    private string _statusText = string.Empty;
    private bool _loadedOnce;
    private System.Timers.Timer? _filterDebounce;

    public ApplicationsViewModel(
        ApplicationInventoryService inventory,
        DialogService dialogs,
        ILogger<ApplicationsViewModel> logger)
    {
        _inventory = inventory;
        _dialogs = dialogs;
        _logger = logger;

        RefreshCommand = new AsyncRelayCommand(RefreshAsync);
        MeasureCommand = new AsyncRelayCommand(MeasureAsync, () => !_isMeasuring);
        UninstallCommand = new AsyncRelayCommand<InstalledApp>(UninstallAsync);
        OpenLocationCommand = new RelayCommand<InstalledApp>(OpenLocation);
    }

    private InstalledApp? _selectedApp;

    public InstalledApp? SelectedApp
    {
        get => _selectedApp;
        set => Set(ref _selectedApp, value);
    }

    public ObservableCollection<InstalledApp> Apps { get; } = [];

    public ICollectionView? AppsView => CollectionViewSource.GetDefaultView(Apps);

    public ICommand RefreshCommand { get; }
    public ICommand MeasureCommand { get; }
    public ICommand UninstallCommand { get; }
    public ICommand OpenLocationCommand { get; }

    public string FilterText
    {
        get => _filterText;
        set
        {
            if (Set(ref _filterText, value))
            {
                _filterDebounce?.Stop();
                _filterDebounce?.Dispose();
                _filterDebounce = new System.Timers.Timer(150) { AutoReset = false };
                _filterDebounce.Elapsed += (_, _) => Application.Current?.Dispatcher.BeginInvoke(() =>
                {
                    AppsView?.Refresh();
                    OnPropertyChanged(nameof(VisibleCountText));
                });
                _filterDebounce.Start();
            }
        }
    }

    public bool IncludeSystemComponents
    {
        get => _includeSystemComponents;
        set
        {
            if (Set(ref _includeSystemComponents, value))
                _ = RefreshAsync();
        }
    }

    public bool IsMeasuring
    {
        get => _isMeasuring;
        private set
        {
            if (Set(ref _isMeasuring, value))
                OnPropertyChanged(nameof(MeasureButtonEnabled));
        }
    }

    public bool MeasureButtonEnabled => !IsMeasuring;

    public string StatusText
    {
        get => _statusText;
        private set => Set(ref _statusText, value);
    }

    public string VisibleCountText
    {
        get
        {
            int shown = 0;
            if (AppsView is not null)
                foreach (var _ in AppsView) shown++;
            return $"{shown} of {Apps.Count} applications";
        }
    }

    public void OnActivated()
    {
        if (!_loadedOnce)
        {
            _loadedOnce = true;
            _ = RefreshAsync();
        }
    }

    private bool FilterApp(object item)
    {
        if (item is not InstalledApp app) return false;
        if (string.IsNullOrWhiteSpace(_filterText)) return true;
        var q = _filterText.Trim();
        return app.Name.Contains(q, StringComparison.OrdinalIgnoreCase)
               || (app.Publisher?.Contains(q, StringComparison.OrdinalIgnoreCase) ?? false);
    }

    private async Task RefreshAsync()
    {
        try
        {
            StatusText = "Reading installed programs…";
            var apps = await Task.Run(() => _inventory.GetInstalledApps(_includeSystemComponents));
            Apps.Clear();
            foreach (var app in apps)
                Apps.Add(app);
            AppsView!.Filter = FilterApp;
            StatusText = string.Empty;
            AppsView?.Refresh();
            OnPropertyChanged(nameof(VisibleCountText));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Inventory refresh failed");
            StatusText = $"Could not read the program list: {ex.Message}";
        }
    }

    private async Task MeasureAsync()
    {
        if (IsMeasuring) return;
        IsMeasuring = true;
        try
        {
            var progress = new Progress<(int Done, int Total)>(p =>
                StatusText = p.Total > 0 ? $"Measuring install folders… {p.Done}/{p.Total}" : "Measuring install folders…");
            await _inventory.MeasureSizesAsync(Apps.ToList(), progress, CancellationToken.None);

            // Replace items so the UI reflects measured values.
            var updated = Apps.ToList();
            Apps.Clear();
            foreach (var app in updated)
                Apps.Add(app);
            AppsView?.Refresh();
            StatusText = "Measured sizes are based on the install folder reported by each application — shared components may be counted twice.";
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Size measurement failed");
            StatusText = $"Measurement failed: {ex.Message}";
        }
        finally
        {
            IsMeasuring = false;
            OnPropertyChanged(nameof(VisibleCountText));
        }
    }

    private async Task UninstallAsync(InstalledApp? app)
    {
        if (app is null) return;
        if (string.IsNullOrWhiteSpace(app.UninstallString))
        {
            _dialogs.Info("No uninstaller", "This application does not register an uninstall mechanism with Windows.", error: true);
            return;
        }

        bool confirmed = _dialogs.Confirm(
            "Uninstall application",
            $"Launch the registered uninstaller for '{app.Name}'?",
            app.Publisher is { } p ? $"Publisher: {p}" : null,
            "Open uninstaller",
            danger: false);
        if (!confirmed) return;

        bool ok = _inventory.Uninstall(app, out var error);
        if (!ok)
            _dialogs.Info("Uninstall failed", error ?? "Unknown error.", error: true);
        else
            StatusText = $"The uninstaller for '{app.Name}' was started. Refresh after it finishes.";
        await Task.CompletedTask;
    }

    private void OpenLocation(InstalledApp? app)
    {
        if (app?.InstallLocation is { } loc && System.IO.Directory.Exists(loc))
        {
            try
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("explorer.exe", loc) { UseShellExecute = true });
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Could not open {Loc}", loc);
            }
        }
        else
        {
            _dialogs.Info("No install folder", "This application did not report an installation location.");
        }
    }
}

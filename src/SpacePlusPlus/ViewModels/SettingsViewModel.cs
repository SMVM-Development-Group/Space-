using System.Collections.ObjectModel;
using System.Windows.Input;
using Microsoft.Extensions.Logging;
using Microsoft.Win32;
using SpacePlusPlus.Infrastructure;
using SpacePlusPlus.Models;
using SpacePlusPlus.Services;

namespace SpacePlusPlus.ViewModels;

public sealed class SettingsViewModel : ObservableObject, IPageLifecycle
{
    private readonly SettingsService _settings;
    private readonly DialogService _dialogs;
    private readonly ILogger<SettingsViewModel> _logger;

    private string _defaultScanLocation;
    private string _thresholdOption;
    private double _concurrency;
    private string _theme;
    private bool _startWithWindows;
    private bool _confirmDeletions;
    private bool _confirmCleanup;
    private double _historyRetention;
    private bool _enableFileLogging;
    private string _logLevel;
    private string _statusText = string.Empty;
    private string _newExclusion = string.Empty;

    public SettingsViewModel(SettingsService settings, DialogService dialogs, ILogger<SettingsViewModel> logger)
    {
        _settings = settings;
        _dialogs = dialogs;
        _logger = logger;

        var s = settings.Current;
        _defaultScanLocation = s.DefaultScanLocation;
        _thresholdOption = ToThresholdOption(s.LargeFileThresholdBytes);
        _concurrency = s.ScanConcurrency;
        _theme = s.Theme;
        _startWithWindows = settings.GetStartWithWindowsActual();
        _confirmDeletions = s.ConfirmDeletions;
        _confirmCleanup = s.ConfirmCleanup;
        _historyRetention = s.HistoryRetention;
        _enableFileLogging = s.EnableFileLogging;
        _logLevel = s.LogLevel;

        foreach (var e in s.ExcludedDirectories)
            ExcludedDirectories.Add(e);

        BrowseLocationCommand = new RelayCommand(BrowseLocation);
        SaveCommand = new RelayCommand(Save, () => true);
        AddExclusionCommand = new RelayCommand(AddExclusion, () => !string.IsNullOrWhiteSpace(NewExclusion));
        RemoveExclusionCommand = new RelayCommand<string>(RemoveExclusion);
        ClearLocationCommand = new RelayCommand(() => DefaultScanLocation = string.Empty);
        ResetDefaultsCommand = new RelayCommand(ResetDefaults);
    }

    public ObservableCollection<string> ExcludedDirectories { get; } = [];

    public ICommand BrowseLocationCommand { get; }
    public ICommand SaveCommand { get; }
    public ICommand AddExclusionCommand { get; }
    public ICommand RemoveExclusionCommand { get; }
    public ICommand ClearLocationCommand { get; }
    public ICommand ResetDefaultsCommand { get; }

    public IReadOnlyList<string> ThresholdOptions { get; } = ["10 MB", "50 MB", "100 MB", "200 MB", "500 MB", "1 GB"];
    public IReadOnlyList<string> Themes { get; } = [ThemeManager.Dark, ThemeManager.Light];
    public IReadOnlyList<string> LogLevels { get; } = ["Information", "Debug", "Warning", "Error"];
    public double ConcurrencyMinimum => 1;
    public double ConcurrencyMaximum => 16;

    public string DefaultScanLocation
    {
        get => _defaultScanLocation;
        set => Set(ref _defaultScanLocation, value);
    }

    public string ThresholdOption
    {
        get => _thresholdOption;
        set
        {
            if (Set(ref _thresholdOption, value))
                OnPropertyChanged(nameof(ThresholdHintText));
        }
    }

    public string ThresholdHintText =>
        $"Files at or above this size appear in Large Files and the Storage Map file layer. Smaller files are counted as aggregates. Selected: {ThresholdOption} (stored as {FromThresholdOption(ThresholdOption):N0} bytes).";

    public double Concurrency
    {
        get => _concurrency;
        set
        {
            if (Set(ref _concurrency, value))
                OnPropertyChanged(nameof(ConcurrencyText));
        }
    }

    public string ConcurrencyText => $"{Concurrency:N0} parallel workers";

    public string Theme
    {
        get => _theme;
        set
        {
            if (Set(ref _theme, value))
            {
                ThemeManager.Apply(value);
                StatusText = $"Theme switched to {value}. Save to persist.";
            }
        }
    }

    public bool StartWithWindows
    {
        get => _startWithWindows;
        set
        {
            if (Set(ref _startWithWindows, value))
            {
                bool ok = _settings.SetStartWithWindows(value);
                if (!ok) StatusText = "Windows refused to update the startup registration.";
            }
        }
    }

    public bool ConfirmDeletions
    {
        get => _confirmDeletions;
        set => Set(ref _confirmDeletions, value);
    }

    public bool ConfirmCleanup
    {
        get => _confirmCleanup;
        set => Set(ref _confirmCleanup, value);
    }

    public double HistoryRetention
    {
        get => _historyRetention;
        set
        {
            if (Set(ref _historyRetention, value))
                OnPropertyChanged(nameof(RetentionText));
        }
    }

    public string RetentionText => $"{HistoryRetention:N0} records kept per list";

    public bool EnableFileLogging
    {
        get => _enableFileLogging;
        set => Set(ref _enableFileLogging, value);
    }

    public string LogLevel
    {
        get => _logLevel;
        set => Set(ref _logLevel, value);
    }

    public string NewExclusion
    {
        get => _newExclusion;
        set
        {
            if (Set(ref _newExclusion, value))
                CommandManager.InvalidateRequerySuggested();
        }
    }

    public string StatusText
    {
        get => _statusText;
        private set => Set(ref _statusText, value);
    }

    public string LogDirectoryText => _settings.LogDirectory;

    public void OnActivated()
    {
        // Startup state can change outside the app; reflect reality.
        StartWithWindows = _settings.GetStartWithWindowsActual();
    }

    private static string ToThresholdOption(long bytes) => bytes switch
    {
        10L << 20 => "10 MB",
        50L << 20 => "50 MB",
        200L << 20 => "200 MB",
        500L << 20 => "500 MB",
        1L << 30 => "1 GB",
        _ => "100 MB"
    };

    private static long FromThresholdOption(string option) => option switch
    {
        "10 MB" => 10L << 20,
        "50 MB" => 50L << 20,
        "200 MB" => 200L << 20,
        "500 MB" => 500L << 20,
        "1 GB" => 1L << 30,
        _ => 100L << 20
    };

    private void BrowseLocation()
    {
        try
        {
            var dialog = new OpenFolderDialog
            {
                Title = "Choose the default scan location",
                InitialDirectory = System.IO.Directory.Exists(DefaultScanLocation)
                    ? DefaultScanLocation
                    : string.Empty
            };
            if (dialog.ShowDialog() == true)
                DefaultScanLocation = dialog.FolderName;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Folder dialog failed");
        }
    }

    private void AddExclusion()
    {
        var raw = NewExclusion.Trim().TrimEnd('\\', '/');
        if (raw.Length == 0) return;
        if (ExcludedDirectories.Any(e => string.Equals(e, raw, StringComparison.OrdinalIgnoreCase)))
        {
            NewExclusion = string.Empty;
            return;
        }
        ExcludedDirectories.Add(raw);
        NewExclusion = string.Empty;
    }

    private void RemoveExclusion(string? item)
    {
        if (item is not null) ExcludedDirectories.Remove(item);
    }

    private void ResetDefaults()
    {
        DefaultScanLocation = string.Empty;
        ThresholdOption = "100 MB";
        Concurrency = 4;
        Theme = ThemeManager.Dark;
        ConfirmDeletions = true;
        ConfirmCleanup = true;
        HistoryRetention = 30;
        EnableFileLogging = true;
        LogLevel = "Information";
        Save();
    }

    private void Save()
    {
        var s = _settings.Current;
        s.DefaultScanLocation = System.IO.Directory.Exists(DefaultScanLocation) ? DefaultScanLocation : string.Empty;
        s.LargeFileThresholdBytes = FromThresholdOption(ThresholdOption);
        s.ScanConcurrency = (int)Math.Clamp(Concurrency, 1, 16);
        s.Theme = Theme;
        s.ConfirmDeletions = ConfirmDeletions;
        s.ConfirmCleanup = ConfirmCleanup;
        s.HistoryRetention = (int)Math.Clamp(HistoryRetention, 1, 500);
        s.EnableFileLogging = EnableFileLogging;
        s.LogLevel = LogLevel;
        s.ExcludedDirectories = ExcludedDirectories.ToList();

        _settings.Save();
        StatusText = $"Settings saved to {_settings.SettingsPath}";
    }
}

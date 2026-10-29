using System.Diagnostics;
using System.IO;
using Microsoft.Extensions.Logging;
using SpacePlusPlus.Infrastructure;
using SpacePlusPlus.Models;

namespace SpacePlusPlus.Services;

/// <summary>
/// Owns the lifecycle of the active scan and shares results across ViewModels.
/// </summary>
public sealed class ScanManager(
    StorageScannerService scanner,
    SettingsService settings,
    ScanHistoryService history,
    ILogger<ScanManager> logger) : ObservableObject
{
    private CancellationTokenSource? _cts;
    private PauseToken _pause = new();
    private bool _isScanning;
    private bool _isPaused;
    private ScanProgress? _progress;
    private ScanResult? _lastResult;
    private string? _lastError;

    public bool IsScanning { get => _isScanning; private set => Set(ref _isScanning, value); }
    public bool IsPaused { get => _isPaused; private set => Set(ref _isPaused, value); }
    public ScanProgress? Progress { get => _progress; private set => Set(ref _progress, value); }
    public ScanResult? LastResult { get => _lastResult; private set => Set(ref _lastResult, value); }
    public string? LastError { get => _lastError; private set => Set(ref _lastError, value); }

    public event Action<ScanResult?>? ScanCompleted;
    public event Action? ScanStarted;
    public event Action? ScanFinished;

    public bool CanStart => !IsScanning;

    public async Task StartScanAsync(string rootPath, string? displayName = null)
    {
        if (IsScanning || string.IsNullOrWhiteSpace(rootPath)) return;
        if (!Directory.Exists(rootPath))
        {
            LastError = $"The location '{rootPath}' does not exist.";
            return;
        }

        _cts?.Dispose();
        _cts = new CancellationTokenSource();
        _pause = new PauseToken();
        LastError = null;
        IsScanning = true;
        IsPaused = false;
        Progress = null;
        ScanStarted?.Invoke();

        var s = settings.Current;
        var options = new ScanOptions
        {
            RootPath = rootPath,
            DisplayName = displayName ?? rootPath,
            LargeFileThresholdBytes = s.LargeFileThresholdBytes,
            Concurrency = s.ScanConcurrency,
            ExcludedDirectories = s.ExcludedDirectories.ToArray()
        };

        // Progress<T> captures the current synchronization context, marshalling reports to the UI thread.
        var progress = new Progress<ScanProgress>(p => Progress = p);

        try
        {
            var result = await scanner.ScanAsync(options, _pause, progress, _cts.Token).ConfigureAwait(true);
            LastResult = result;
            LastError = result.WasCancelled
                ? "Scan was cancelled. Results shown are partial and may be incomplete."
                : result.ErrorCount > 0
                    ? "Some locations could not be read. Sizes for those areas are not included."
                    : null;
            ScanCompleted?.Invoke(result);
            await history.AddScanAsync(ToRecord(result)).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            logger.LogInformation("Scan cancelled by user");
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Scan failed");
            LastError = $"Scan failed: {ex.Message}";
        }
        finally
        {
            IsScanning = false;
            IsPaused = false;
            ScanFinished?.Invoke();
        }
    }

    public void Cancel()
    {
        if (IsScanning) _cts?.Cancel();
    }

    public void SetPaused(bool paused)
    {
        if (!IsScanning) return;
        _pause.IsPaused = paused;
        IsPaused = paused;
    }

    private static ScanRecord ToRecord(ScanResult r) => new(
        Guid.NewGuid(),
        DateTime.UtcNow,
        r.RootPath,
        r.DisplayName,
        (long)r.Duration.TotalMilliseconds,
        r.TotalFiles,
        r.TotalDirectories,
        r.TotalSizeBytes,
        r.TotalAllocatedBytes,
        r.ErrorCount + r.JunctionsSkipped,
        r.CategorySizes.ToDictionary(k => k.Key.ToString(), k => k.Value),
        r.Root.Children
            .OrderByDescending(c => c.TotalSize)
            .Take(12)
            .Select(c => new TopDirStat(c.Name, c.TotalSize, c.TotalFiles))
            .ToList(),
        r.LargeFiles.Count,
        r.WasCancelled);
}

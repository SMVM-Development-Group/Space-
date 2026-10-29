using Microsoft.Extensions.Logging;
using SpacePlusPlus.Infrastructure;
using SpacePlusPlus.Models;

namespace SpacePlusPlus.Services;

public sealed class HistoryFile
{
    public List<ScanRecord> Scans { get; set; } = [];
    public List<CleanupRecord> Cleanups { get; set; } = [];
}

public sealed class ScanHistoryService(SettingsService settings, ILogger<ScanHistoryService> logger)
{
    private readonly object _gate = new();
    private HistoryFile _file = new();

    public event Action? Changed;

    public HistoryFile Load()
    {
        lock (_gate)
        {
            _file = JsonStore.Load<HistoryFile>(settings.HistoryPath) ?? new HistoryFile();
            return _file;
        }
    }

    private void Save()
    {
        lock (_gate)
        {
            var retention = Math.Clamp(settings.Current.HistoryRetention, 1, 500);
            if (_file.Scans.Count > retention)
                _file.Scans = _file.Scans.OrderByDescending(s => s.TimestampUtc).Take(retention).ToList();
            if (_file.Cleanups.Count > retention)
                _file.Cleanups = _file.Cleanups.OrderByDescending(c => c.TimestampUtc).Take(retention).ToList();
            if (!JsonStore.Save(settings.HistoryPath, _file))
                logger.LogWarning("Failed to persist scan history");
        }
    }

    public Task AddScanAsync(ScanRecord record)
    {
        lock (_gate)
        {
            _file.Scans.Add(record);
        }
        Save();
        Changed?.Invoke();
        return Task.CompletedTask;
    }

    public Task AddCleanupAsync(CleanupRecord record)
    {
        lock (_gate)
        {
            _file.Cleanups.Add(record);
        }
        Save();
        Changed?.Invoke();
        return Task.CompletedTask;
    }

    public List<ScanRecord> GetScans()
    {
        Load();
        lock (_gate) return _file.Scans.OrderByDescending(s => s.TimestampUtc).ToList();
    }

    public List<CleanupRecord> GetCleanups()
    {
        Load();
        lock (_gate) return _file.Cleanups.OrderByDescending(c => c.TimestampUtc).ToList();
    }

    public List<ActivityRecord> GetActivity(int limit = 8)
    {
        Load();
        lock (_gate)
        {
            var items = new List<ActivityRecord>(_file.Scans.Count + _file.Cleanups.Count);
            foreach (var s in _file.Scans)
                items.Add(new ActivityRecord(s.TimestampUtc, "scan",
                    $"Scanned {s.DisplayName}",
                    $"{SizeFormat.Format(s.TotalBytes)} across {SizeFormat.FormatCount(s.TotalFiles)} files — {(s.WasCancelled ? "cancelled" : "completed in " + SizeFormat.FormatDuration(TimeSpan.FromMilliseconds(s.DurationMs)))}"));
            foreach (var c in _file.Cleanups)
                items.Add(new ActivityRecord(c.TimestampUtc, "cleanup", "Cleanup completed", c.Summary));
            return items.OrderByDescending(a => a.TimeUtc).Take(limit).ToList();
        }
    }

    public Task ClearAsync()
    {
        lock (_gate)
        {
            _file.Scans.Clear();
            _file.Cleanups.Clear();
        }
        Save();
        Changed?.Invoke();
        return Task.CompletedTask;
    }
}

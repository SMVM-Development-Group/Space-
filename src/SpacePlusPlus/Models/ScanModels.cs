namespace SpacePlusPlus.Models;

public sealed class ScanOptions
{
    public required string RootPath { get; init; }
    public string? DisplayName { get; init; }
    public long LargeFileThresholdBytes { get; init; } = 100L << 20;
    public int MaxTrackedFiles { get; init; } = 50_000;
    public int Concurrency { get; init; } = 4;
    public IReadOnlyList<string> ExcludedDirectories { get; init; } = [];
}

public sealed record ScanProgress(
    long FilesScanned,
    long DirectoriesScanned,
    long BytesProcessed,
    long Errors,
    long JunctionsSkipped,
    string? CurrentDirectory,
    TimeSpan Elapsed,
    bool Paused);

public sealed record ScanError(string Path, string Message);

public sealed class ScanResult
{
    public required string RootPath { get; init; }
    public required string DisplayName { get; init; }
    public DateTime StartedAtUtc { get; init; }
    public TimeSpan Duration { get; set; }
    public required DirectoryNode Root { get; init; }
    public required IReadOnlyList<FileNode> LargeFiles { get; init; }
    public required IReadOnlyList<ScanError> Errors { get; init; }
    public required IReadOnlyDictionary<StorageCategory, long> CategorySizes { get; init; }
    public required IReadOnlyDictionary<StorageCategory, long> CategoryFileCounts { get; init; }
    public required bool WasCancelled { get; init; }
    public required bool LargeFileTrackingCapped { get; init; }
    public long TotalSizeBytes { get; set; }
    public long TotalAllocatedBytes { get; set; }
    public long TotalFiles { get; set; }
    public long TotalDirectories { get; set; }
    public int ErrorCount { get; set; }
    public int JunctionsSkipped { get; set; }
    public long? DriveTotalBytes { get; init; }
    public long? DriveFreeBytes { get; init; }
    public string? DriveLetter { get; init; }

    /// <summary>Drive usage not attributed by the scan. Non-zero when some locations were inaccessible or skipped.</summary>
    public long? UnclassifiedBytes
    {
        get
        {
            if (DriveTotalBytes is not { } total || DriveFreeBytes is not { } free) return null;
            long used = total - free;
            long value = used - TotalSizeBytes;
            return value > 0 ? value : 0;
        }
    }
}

namespace SpacePlusPlus.Models;

public sealed record TopDirStat(string Name, long Bytes, long Files);

public sealed record ScanRecord(
    Guid Id,
    DateTime TimestampUtc,
    string RootPath,
    string DisplayName,
    long DurationMs,
    long TotalFiles,
    long TotalDirectories,
    long TotalBytes,
    long TotalAllocatedBytes,
    int InaccessibleCount,
    Dictionary<string, long> CategorySizes,
    List<TopDirStat> TopLevelDirs,
    long LargeFileCount,
    bool WasCancelled);

/// <summary>Per-category outcome of a cleanup run — JSON-serializable (no tuples).</summary>
public sealed record CleanupGroupStat(string Title, long FreedBytes, long DeletedFiles, long FailedFiles);

public sealed record CleanupRecord(
    Guid Id,
    DateTime TimestampUtc,
    List<CleanupGroupStat> Groups,
    long TotalFreedBytes)
{
    public string Summary
    {
        get
        {
            long files = 0;
            foreach (var g in Groups) files += g.DeletedFiles;
            return $"{Groups.Count} categories — {Infrastructure.SizeFormat.Format(TotalFreedBytes)} freed, {files} files removed";
        }
    }
}

public sealed record ActivityRecord(DateTime TimeUtc, string Kind, string Title, string Detail)
{
    public string TimeText => TimeUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm");
    public string KindText => Kind switch { "scan" => "Scan", "cleanup" => "Cleanup", _ => Kind };
}

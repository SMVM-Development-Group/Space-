namespace SpacePlusPlus.Models;

public enum CleanupSafety
{
    Safe,
    Caution
}

/// <summary>A detected cleanup opportunity. Targets are either directories (contents are removed) or individual files.</summary>
public sealed record CleanupGroup(
    string Id,
    string Title,
    string Description,
    string CategoryLabel,
    CleanupSafety Safety,
    bool RequiresAdmin,
    string? Warning,
    long? EstimatedBytes,
    long? EstimatedFiles,
    IReadOnlyList<string> Targets,
    IReadOnlyList<string> SamplePaths,
    string? LockedReason = null)
{
    public string SafetyText => RequiresAdmin ? "Administrator" : Safety switch
    {
        CleanupSafety.Safe => "Safe",
        _ => "Review"
    };
}

public sealed record CleanupProgressReport(string GroupTitle, string? CurrentItem, long FilesProcessed, long BytesFreed);

public sealed record CleanupResultItem(
    string GroupId,
    string Title,
    long FreedBytes,
    long DeletedFiles,
    long SkippedFiles,
    long FailedFiles,
    string? Notes,
    bool Success);

public sealed record CleanupRunResult(IReadOnlyList<CleanupResultItem> Items, long TotalFreedBytes);

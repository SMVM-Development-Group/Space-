using System.IO;

namespace SpacePlusPlus.Models;

public sealed class FileNode
{
    public required string Name { get; init; }
    public required string Path { get; init; }
    public required long SizeBytes { get; init; }
    public required long AllocatedBytes { get; init; }
    public required DateTime ModifiedUtc { get; init; }
    public required string Extension { get; init; }
    public required StorageCategory Category { get; init; }
    public FileAttributes Attributes { get; init; }

    public string SizeText => Infrastructure.SizeFormat.Format(SizeBytes);
    public string ModifiedText => ModifiedUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm");
    public string TypeText => string.IsNullOrEmpty(Extension) ? "File" : Extension.TrimStart('.').ToUpperInvariant();
    public string CategoryText => Category.Title();
}

public sealed class DirectoryNode
{
    public required string Name { get; init; }
    public required string Path { get; init; }
    public DirectoryNode? Parent { get; set; }
    public List<DirectoryNode> Children { get; } = [];
    public List<FileNode> LargeFiles { get; } = [];

    // Owned exclusively by the scanning worker while the scan runs.
    public long SmallFilesCount;
    public long SmallFilesSize;
    public long SmallFilesAllocated;
    public int Errors;
    public int JunctionsSkipped;

    public long TotalSize { get; private set; }
    public long TotalAllocated { get; private set; }
    public long TotalFiles { get; private set; }
    public long TotalDirectories { get; private set; }
    public int TotalErrors { get; private set; }
    public int TotalJunctionsSkipped { get; private set; }

    internal void AddSmallFile(long size)
    {
        SmallFilesCount++;
        SmallFilesSize += size;
        SmallFilesAllocated += Infrastructure.SizeFormat.AlignToCluster(size);
    }

    /// <summary>Aggregates descendant totals. Called once after enumeration finishes.</summary>
    public void ComputeTotals()
    {
        long size = SmallFilesSize;
        long allocated = SmallFilesAllocated;
        long files = SmallFilesCount + LargeFiles.Count;
        long dirs = 0;
        int errors = Errors;
        int junctions = JunctionsSkipped;
        foreach (var child in Children)
        {
            child.ComputeTotals();
            size += child.TotalSize;
            allocated += child.TotalAllocated;
            files += child.TotalFiles;
            dirs += child.TotalDirectories + 1;
            errors += child.TotalErrors;
            junctions += child.TotalJunctionsSkipped;
        }
        foreach (var f in LargeFiles)
        {
            size += f.SizeBytes;
            allocated += f.AllocatedBytes;
        }
        TotalSize = size;
        TotalAllocated = allocated;
        TotalFiles = files;
        TotalDirectories = dirs;
        TotalErrors = errors;
        TotalJunctionsSkipped = junctions;
    }
}

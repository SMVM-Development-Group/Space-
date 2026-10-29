using System.Diagnostics;
using System.IO;
using System.IO.Enumeration;
using Microsoft.Extensions.Logging;
using SpacePlusPlus.Infrastructure;
using SpacePlusPlus.Models;

namespace SpacePlusPlus.Services;

/// <summary>
/// OneDrive integration built on the Windows Cloud Files (cldflt / cfapi) filter driver.
/// "Freeing" local space dehydrates placeholders — the cloud copy is never touched.
/// </summary>
public sealed class OneDriveService(ILogger<OneDriveService> logger)
{
    public string? RootPath { get; private set; }

    public bool IsAvailable => Detect();

    private bool Detect()
    {
        RootPath ??= FileClassificationService.DetectOneDriveRoot();
        return RootPath is not null;
    }

    /// <summary>Cloud Files filter driver must be running for dehydration to work.</summary>
    public bool IsCloudFilesFilterEnabled()
    {
        try
        {
            using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\CldFlt");
            return key?.GetValue("Start") is int start && start != 4; // 4 = disabled
        }
        catch
        {
            return false;
        }
    }

    public async Task<OneDriveSummary> AnalyzeAsync(CancellationToken ct, IProgress<string>? progress, int maxUiItems, List<OneDriveItem>? collectedItems)
    {
        if (!Detect() || RootPath is null)
            throw new InvalidOperationException("OneDrive was not detected on this system.");

        long localBytes = 0, localFiles = 0;
        long onlineBytes = 0, onlineFiles = 0;
        long pinnedBytes = 0, pinnedFiles = 0;
        long reclaimBytes = 0, reclaimFiles = 0;
        bool truncated = false;
        int items = 0;

        var root = RootPath;
        var walk = Task.Run(() =>
        {
            var enumerable = new FileSystemEnumerable<(bool IsDir, string Name, long Len, uint Attrs, string Full)>(root,
                (ref FileSystemEntry entry) => (entry.IsDirectory, entry.FileName.ToString(), entry.IsDirectory ? 0 : entry.Length,
                    (uint)entry.Attributes, entry.ToFullPath()),
                new EnumerationOptions { IgnoreInaccessible = true, AttributesToSkip = 0, RecurseSubdirectories = true, ReturnSpecialDirectories = false });
            foreach (var (isDir, name, len, attrs, full) in enumerable)
            {
                ct.ThrowIfCancellationRequested();
                if ((attrs & 0x400) != 0) continue; // ReparsePoint — never traverse
                if (isDir) continue;
                items++;
                if (items % 500 == 0) progress?.Report(full);
                var state = ClassifyState(attrs);
                bool eligible = state == CloudState.LocallyAvailable;
                switch (state)
                {
                    case CloudState.OnlineOnly: onlineBytes += len; onlineFiles++; break;
                    case CloudState.Pinned: pinnedBytes += len; pinnedFiles++; break;
                    default:
                        localBytes += len; localFiles++;
                        if (eligible) { reclaimBytes += len; reclaimFiles++; }
                        break;
                }
                if (collectedItems is not null && collectedItems.Count < maxUiItems)
                {
                    collectedItems.Add(new OneDriveItem(full, name, len, state, eligible,
                        full[root.Length..]));
                }
                else if (collectedItems is not null && collectedItems.Count >= maxUiItems)
                {
                    truncated = true;
                }
            }
        }, ct);

        await walk;
        return new OneDriveSummary(root, localBytes, localFiles, onlineBytes, onlineFiles,
            pinnedBytes, pinnedFiles, reclaimBytes, reclaimFiles, truncated);
    }

    public static CloudState ClassifyState(uint attrs)
    {
        bool online = (attrs & (NativeMethods.FILE_ATTRIBUTE_RECALL_ON_DATA_ACCESS
                               | NativeMethods.FILE_ATTRIBUTE_RECALL_ON_OPEN
                               | NativeMethods.FILE_ATTRIBUTE_OFFLINE)) != 0;
        if (online) return CloudState.OnlineOnly;
        bool pinned = (attrs & NativeMethods.FILE_ATTRIBUTE_PINNED) != 0;
        return pinned ? CloudState.Pinned : CloudState.LocallyAvailable;
    }

    /// <summary>Dehydrates one placeholder. Returns false with a reason when not possible.</summary>
    public async Task<(bool Ok, bool Verified, string? Error)> DehydrateAsync(string path)
    {
        return await Task.Run(() =>
        {
            int hr = NativeMethods.CfOpenFileWithOplock(path, 0, out var handle);
            if (hr != 0)
            {
                // Some builds reject oplock type 0; retry with 1 (exclusive) before giving up.
                hr = NativeMethods.CfOpenFileWithOplock(path, 1, out handle);
                if (hr != 0)
                    return (false, false, $"Cannot open cloud file: {NativeMethods.HrMessage(hr)}");
            }
            try
            {
                hr = NativeMethods.CfDehydratePlaceholder(handle);
                if (hr != 0)
                    return (false, false, $"Dehydration failed: {NativeMethods.HrMessage(hr)} (the file may have unsynced changes or is in use)");
                uint attrs = NativeMethods.GetFileAttributesW(path);
                bool verified = attrs != 0xFFFFFFFF
                                && (attrs & (NativeMethods.FILE_ATTRIBUTE_RECALL_ON_DATA_ACCESS
                                             | NativeMethods.FILE_ATTRIBUTE_RECALL_ON_OPEN
                                             | NativeMethods.FILE_ATTRIBUTE_OFFLINE)) != 0;
                return (true, verified, verified ? null : "Dehydration was requested; the new state could not be verified.");
            }
            finally
            {
                NativeMethods.CloseHandle(handle);
            }
        });
    }

    /// <summary>Dehydrates all eligible files under the selected paths (files or directories).</summary>
    public async Task<DehydrateResult> DehydrateManyAsync(
        IReadOnlyList<string> paths, Func<string, Task>? reportFile, CancellationToken ct)
    {
        long attempted = 0, succeeded = 0, failed = 0, unverified = 0, bytes = 0;
        string? firstError = null;
        foreach (var path in paths)
        {
            ct.ThrowIfCancellationRequested();
            foreach (var file in EnumerateEligibleFiles(path, ct))
            {
                ct.ThrowIfCancellationRequested();
                attempted++;
                if (reportFile is not null) await reportFile(file.Full);
                var (ok, verified, error) = await DehydrateAsync(file.Full);
                if (ok)
                {
                    succeeded++;
                    bytes += file.Size;
                    if (!verified) unverified++;
                }
                else
                {
                    failed++;
                    firstError ??= error;
                }
            }
        }
        return new DehydrateResult(attempted, succeeded, failed, unverified, bytes, firstError);
    }

    private record EligibleFile(string Full, long Size);

    private IEnumerable<EligibleFile> EnumerateEligibleFiles(string path, CancellationToken ct)
    {
        if (File.Exists(path))
        {
            uint attrs = NativeMethods.GetFileAttributesW(path);
            if (attrs != 0xFFFFFFFF && ClassifyState(attrs) == CloudState.LocallyAvailable)
                yield return new EligibleFile(path, new FileInfo(path).Length);
            yield break;
        }
        if (!Directory.Exists(path)) yield break;
        var queue = new Queue<string>();
        queue.Enqueue(path);
        while (queue.Count > 0)
        {
            ct.ThrowIfCancellationRequested();
            var dir = queue.Dequeue();
            IEnumerable<FileSystemInfo> entries;
            try
            {
                entries = new DirectoryInfo(dir).EnumerateFileSystemInfos();
            }
            catch (Exception) { continue; }
            foreach (var entry in entries)
            {
                if ((entry.Attributes & FileAttributes.ReparsePoint) != 0) continue;
                if (entry is DirectoryInfo d) queue.Enqueue(d.FullName);
                else
                {
                    uint attrs = NativeMethods.GetFileAttributesW(entry.FullName);
                    if (attrs != 0xFFFFFFFF && ClassifyState(attrs) == CloudState.LocallyAvailable)
                        yield return new EligibleFile(entry.FullName, ((FileInfo)entry).Length);
                }
            }
        }
    }
}

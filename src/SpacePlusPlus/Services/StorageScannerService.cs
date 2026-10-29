using System.Diagnostics;
using System.IO;
using System.IO.Enumeration;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using SpacePlusPlus.Infrastructure;
using SpacePlusPlus.Models;

namespace SpacePlusPlus.Services;

/// <summary>
/// Parallel recursive filesystem scanner. Enumerates with bounded concurrency,
/// never follows reparse points, tracks access failures, and keeps memory bounded
/// by only retaining files above the large-file threshold plus per-directory aggregates.
///
/// Memory note: the directory tree is retained (required by the Storage Map), so directory
/// objects exist once regardless of queueing; the discovery channel is therefore unbounded
/// and adds no per-directory overhead beyond existing references. Small files are never
/// stored individually.
///
/// Completion model: a single "in flight" counter tracks directories that have been
/// discovered but not yet processed. When it reaches zero, no further directories can
/// appear; the channel writer is completed and workers drain and exit.
/// </summary>
public sealed class StorageScannerService(
    ILogger<StorageScannerService> logger,
    FileClassificationService classifier)
{
    private sealed class ScanContext
    {
        public required ScanOptions Options;
        public required DirectoryNode Root;
        public required ChannelWriter<DirectoryNode> Writer;
        public long Files;
        public long Directories;
        public long Bytes;
        public long Errors;
        public long Junctions;
        public long InFlight = 1;
        public volatile string? CurrentDirectory;
        public readonly object Gate = new();
        public readonly List<FileNode> LargeFiles = [];
        public bool LargeCapped;
        public readonly Dictionary<StorageCategory, (long Bytes, long Files)> Categories = [];
        public readonly List<ScanError> ErrorList = [];
    }

    public async Task<ScanResult> ScanAsync(
        ScanOptions options, PauseToken pause, IProgress<ScanProgress>? progress, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        logger.LogInformation("Scan started: {Root} (concurrency {C}, threshold {T})",
            options.RootPath, options.Concurrency, SizeFormat.Format(options.LargeFileThresholdBytes));

        string fullPath = Path.GetFullPath(options.RootPath);
        var root = new DirectoryNode
        {
            Name = options.DisplayName ?? fullPath,
            Path = fullPath
        };

        var channel = Channel.CreateUnbounded<DirectoryNode>(
            new UnboundedChannelOptions { SingleWriter = false, SingleReader = false });
        var ctx = new ScanContext { Options = options, Root = root, Writer = channel.Writer };
        channel.Writer.TryWrite(root);

        string? driveLetter = null;
        long? driveTotal = null, driveFree = null;
        try
        {
            string pathRoot = Path.GetPathRoot(fullPath) ?? string.Empty;
            if (!string.IsNullOrEmpty(pathRoot) && fullPath.TrimEnd('\\').Equals(pathRoot.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase))
            {
                var di = new DriveInfo(pathRoot);
                if (di.IsReady)
                {
                    driveLetter = di.Name[..1];
                    driveTotal = di.TotalSize;
                    driveFree = di.AvailableFreeSpace;
                }
            }
        }
        catch { /* Non-drive scan. */ }

        int concurrency = Math.Clamp(options.Concurrency, 1, 16);
        var workers = new List<Task>(concurrency);
        for (int i = 0; i < concurrency; i++)
            workers.Add(Task.Run(() => WorkerLoop(channel.Reader, ctx, pause, ct), CancellationToken.None));

        using var timerCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var reporter = Task.Run(async () =>
        {
            try
            {
                using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(250));
                while (await timer.WaitForNextTickAsync(timerCts.Token))
                    progress?.Report(Snapshot(ctx, sw.Elapsed, pause.IsPaused));
            }
            catch (OperationCanceledException) { }
        }, CancellationToken.None);

        await Task.WhenAll(workers);
        channel.Writer.TryComplete();
        timerCts.Cancel();
        try { await reporter; } catch { /* reporter cancelled with the scan */ }

        root.ComputeTotals();
        lock (ctx.Gate)
        {
            // Keep totals consistent with per-directory aggregation.
            ctx.Files = root.TotalFiles;
            ctx.Directories = root.TotalDirectories;
        }

        var large = new List<FileNode>(ctx.LargeFiles);
        large.Sort((a, b) => b.SizeBytes.CompareTo(a.SizeBytes));

        var result = new ScanResult
        {
            RootPath = fullPath,
            DisplayName = root.Name,
            StartedAtUtc = DateTime.UtcNow - sw.Elapsed,
            Duration = sw.Elapsed,
            Root = root,
            LargeFiles = large,
            Errors = ctx.ErrorList,
            CategorySizes = ctx.Categories.ToDictionary(k => k.Key, k => k.Value.Bytes),
            CategoryFileCounts = ctx.Categories.ToDictionary(k => k.Key, k => k.Value.Files),
            WasCancelled = ct.IsCancellationRequested,
            LargeFileTrackingCapped = ctx.LargeCapped,
            TotalSizeBytes = root.TotalSize,
            TotalAllocatedBytes = root.TotalAllocated,
            TotalFiles = root.TotalFiles,
            TotalDirectories = root.TotalDirectories,
            ErrorCount = (int)Interlocked.Read(ref ctx.Errors),
            JunctionsSkipped = (int)Interlocked.Read(ref ctx.Junctions),
            DriveTotalBytes = driveTotal,
            DriveFreeBytes = driveFree,
            DriveLetter = driveLetter
        };

        logger.LogInformation("Scan finished in {Elapsed}: {Files} files, {Dirs} dirs, {Size}, errors {E}, junctions skipped {J}, cancelled={C}",
            sw.Elapsed, result.TotalFiles, result.TotalDirectories, SizeFormat.Format(result.TotalSizeBytes),
            result.ErrorCount, result.JunctionsSkipped, result.WasCancelled);
        return result;
    }

    private static ScanProgress Snapshot(ScanContext ctx, TimeSpan elapsed, bool paused) => new(
        Interlocked.Read(ref ctx.Files),
        Interlocked.Read(ref ctx.Directories),
        Interlocked.Read(ref ctx.Bytes),
        Interlocked.Read(ref ctx.Errors),
        Interlocked.Read(ref ctx.Junctions),
        ctx.CurrentDirectory,
        elapsed,
        paused);

    private async Task WorkerLoop(ChannelReader<DirectoryNode> reader, ScanContext ctx, PauseToken pause, CancellationToken ct)
    {
        try
        {
            await foreach (var node in reader.ReadAllAsync(ct).ConfigureAwait(false))
            {
                await pause.WaitIfPausedAsync(ct).ConfigureAwait(false);
                ct.ThrowIfCancellationRequested();
                ProcessDirectory(node, ctx, ct);
                // The node has been fully enumerated and its children were already
                // registered as in-flight; this node's own work is now done.
                if (Interlocked.Decrement(ref ctx.InFlight) == 0)
                    ctx.Writer.TryComplete();
            }
        }
        catch (OperationCanceledException) { }
        catch (ChannelClosedException) { }
    }

    private void ProcessDirectory(DirectoryNode node, ScanContext ctx, CancellationToken ct)
    {
        ctx.CurrentDirectory = node.Path;
        var options = ctx.Options;
        bool hasExclusions = options.ExcludedDirectories.Count > 0;
        long threshold = options.LargeFileThresholdBytes;
        int maxTracked = options.MaxTrackedFiles;

        try
        {
            var enumerable = new FileSystemEnumerable<(bool IsDir, string Name, long Len, FileAttributes Attrs, DateTime LastWriteUtc)>(node.Path,
                static (ref FileSystemEntry entry) => (entry.IsDirectory, entry.FileName.ToString(),
                    entry.IsDirectory ? 0 : entry.Length, entry.Attributes, entry.LastWriteTimeUtc.UtcDateTime),
                new EnumerationOptions
                {
                    IgnoreInaccessible = true,
                    RecurseSubdirectories = false,
                    AttributesToSkip = 0,
                    ReturnSpecialDirectories = false
                });

            foreach (var (isDir, name, len, attrs, lastWriteUtc) in enumerable)
            {
                ct.ThrowIfCancellationRequested();

                if (isDir)
                {
                    if ((attrs & FileAttributes.ReparsePoint) != 0)
                    {
                        Interlocked.Increment(ref ctx.Junctions);
                        continue;
                    }
                    string childPath = Path.Combine(node.Path, name);
                    if (hasExclusions && ExclusionMatcher.IsExcluded(childPath, options.ExcludedDirectories))
                        continue;
                    var child = new DirectoryNode { Name = name, Path = childPath, Parent = node };
                    node.Children.Add(child);
                    Interlocked.Increment(ref ctx.Directories);
                    Interlocked.Increment(ref ctx.InFlight);
                    ctx.Writer.TryWrite(child); // unbounded: always succeeds
                }
                else
                {
                    long size = len;
                    var ext = Path.GetExtension(name);
                    var category = classifier.Classify(node.Path, ext);

                    if (size >= threshold)
                    {
                        bool capped;
                        lock (ctx.Gate)
                        {
                            capped = ctx.LargeFiles.Count >= maxTracked;
                            if (capped)
                            {
                                ctx.LargeCapped = true;
                            }
                            else
                            {
                                var file = new FileNode
                                {
                                    Name = name,
                                    Path = Path.Combine(node.Path, name),
                                    SizeBytes = size,
                                    AllocatedBytes = SizeFormat.AlignToCluster(size),
                                    ModifiedUtc = lastWriteUtc,
                                    Extension = ext,
                                    Category = category
                                };
                                ctx.LargeFiles.Add(file);
                                node.LargeFiles.Add(file); // tree totals include tracked files
                            }
                        }
                        if (capped)
                        {
                            // Tracking budget exhausted: fall back to aggregate accounting
                            // so totals and categories stay complete.
                            node.AddSmallFile(size);
                        }
                    }
                    else
                    {
                        node.AddSmallFile(size);
                    }

                    Interlocked.Increment(ref ctx.Files);
                    Interlocked.Add(ref ctx.Bytes, size);
                    lock (ctx.Gate)
                    {
                        var (b, c) = ctx.Categories.GetValueOrDefault(category, (0L, 0L));
                        ctx.Categories[category] = (b + size, c + 1);
                    }
                }
            }
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            Interlocked.Increment(ref ctx.Errors);
            node.Errors++;
            lock (ctx.Gate)
            {
                if (ctx.ErrorList.Count < 200) ctx.ErrorList.Add(new ScanError(node.Path, ex.Message));
            }
            logger.LogDebug(ex, "Enumeration failed for {Path}", node.Path);
        }
    }

}

using System.IO;
using System.IO.Enumeration;
using SpacePlusPlus.Infrastructure;

namespace SpacePlusPlus.Services;

/// <summary>
/// Bounded, non-recursive size measurement for known locations (cleanup detection,
/// application install folders). Never follows reparse points.
/// </summary>
public static class QuickScanner
{
    public sealed record MeasureResult(long Bytes, long Files, List<string> Samples, List<string> LockedPaths);

    public static MeasureResult Measure(IEnumerable<string> paths, CancellationToken ct, int sampleLimit = 6, bool collectSamples = true)
    {
        long bytes = 0, files = 0;
        var samples = new List<string>();
        var locked = new List<string>();
        foreach (var root in paths)
        {
            ct.ThrowIfCancellationRequested();
            MeasureOne(root, ct, ref bytes, ref files, samples, locked, sampleLimit, collectSamples);
        }
        return new MeasureResult(bytes, files, samples, locked);
    }

    private static void MeasureOne(string root, CancellationToken ct, ref long bytes, ref long files,
        List<string> samples, List<string> locked, int sampleLimit, bool collectSamples)
    {
        try
        {
            if (File.Exists(root))
            {
                try
                {
                    var fi = new FileInfo(root);
                    bytes += fi.Length;
                    files++;
                    if (collectSamples && samples.Count < sampleLimit) samples.Add(root);
                }
                catch (Exception) { locked.Add(root); }
                return;
            }
            if (!Directory.Exists(root)) return;

            var options = new EnumerationOptions
            {
                IgnoreInaccessible = true,
                RecurseSubdirectories = true,
                // Following junctions/symlinks can double-count another app or
                // enter a cycle. Their contents do not belong to this folder.
                AttributesToSkip = FileAttributes.ReparsePoint,
                ReturnSpecialDirectories = false
            };
            var enumerable = new FileSystemEnumerable<long>(root,
                static (ref FileSystemEntry entry) => entry.IsDirectory ? -1 : entry.Length,
                options);
            foreach (var value in enumerable)
            {
                ct.ThrowIfCancellationRequested();
                if (value < 0) continue; // directories contribute nothing directly
                bytes += value;
                files++;
                if (collectSamples && samples.Count < sampleLimit)
                {
                    // Reconstructing the path would cost another entry; sample only roots.
                    if (samples.Count == 0) samples.Add(root);
                }
            }
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception)
        {
            // Inaccessible location: counted as neither accessible nor deletable.
        }
    }

    /// <summary>
    /// Streams deletions of a directory's contents (keeps the root folder itself) or of a single file.
    /// Reports progress and collects skipped/failed counts. Never follows reparse points.
    /// </summary>
    public static void DeleteContents(string target, Action<long>? onFileDeleted, Action<long>? onBytesFreed,
        CancellationToken ct, List<string> errors, ref long deletedFiles, ref long freedBytes,
        ref long skipped, ref long failed)
    {
        try
        {
            if (File.Exists(target))
            {
                DeleteFileCore(target, onFileDeleted, onBytesFreed, ref deletedFiles, ref freedBytes, ref skipped, ref failed, errors);
                return;
            }
            if (!Directory.Exists(target)) return;
            DeleteTree(target, onFileDeleted, onBytesFreed, ct, errors, ref deletedFiles, ref freedBytes, ref skipped, ref failed);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            if (errors.Count < 5) errors.Add(ex.Message);
            failed++;
        }
    }

    private static void DeleteTree(string dir, Action<long>? onFileDeleted, Action<long>? onBytesFreed,
        CancellationToken ct, List<string> errors, ref long deletedFiles, ref long freedBytes,
        ref long skipped, ref long failed)
    {
        try
        {
            var enumerable = new FileSystemEnumerable<(bool isDir, string name, long len, FileAttributes attrs)>(dir,
                static (ref FileSystemEntry entry) => (entry.IsDirectory, entry.FileName.ToString(), entry.IsDirectory ? 0 : entry.Length, entry.Attributes),
                new EnumerationOptions { IgnoreInaccessible = true, AttributesToSkip = 0, ReturnSpecialDirectories = false });
            foreach (var (isDir, name, len, attrs) in enumerable)
            {
                ct.ThrowIfCancellationRequested();
                if ((attrs & FileAttributes.ReparsePoint) != 0) { skipped++; continue; }
                if (isDir)
                {
                    DeleteTree(Path.Combine(dir, name), onFileDeleted, onBytesFreed, ct, errors, ref deletedFiles, ref freedBytes, ref skipped, ref failed);
                }
                else
                {
                    DeleteFileCore(Path.Combine(dir, name), onFileDeleted, onBytesFreed, ref deletedFiles, ref freedBytes, ref skipped, ref failed, errors);
                }
            }
            try { Directory.Delete(dir, false); }
            catch (Exception) { skipped++; }
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            if (errors.Count < 5) errors.Add(ex.Message);
            failed++;
        }
    }

    private static void DeleteFileCore(string path, Action<long>? onFileDeleted, Action<long>? onBytesFreed,
        ref long deletedFiles, ref long freedBytes, ref long skipped, ref long failed, List<string> errors)
    {
        try
        {
            long size = new FileInfo(path).Length;
            var attrs = File.GetAttributes(path);
            if ((attrs & FileAttributes.ReadOnly) != 0)
                File.SetAttributes(path, attrs & ~FileAttributes.ReadOnly);
            File.Delete(path);
            deletedFiles++;
            freedBytes += size;
            onFileDeleted?.Invoke(deletedFiles);
            onBytesFreed?.Invoke(freedBytes);
        }
        catch (FileNotFoundException) { skipped++; }
        catch (DirectoryNotFoundException) { skipped++; }
        catch (IOException) { skipped++; } // File in use — typical for caches of running apps.
        catch (UnauthorizedAccessException)
        {
            failed++;
            if (errors.Count < 5) errors.Add($"Access denied: {path}");
        }
        catch (Exception ex)
        {
            failed++;
            if (errors.Count < 5) errors.Add(ex.Message);
        }
    }
}

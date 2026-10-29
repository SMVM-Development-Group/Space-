using System.Diagnostics;
using System.IO;
using Microsoft.Extensions.Logging;
using SpacePlusPlus.Infrastructure;
using SpacePlusPlus.Models;

namespace SpacePlusPlus.Services;

/// <summary>
/// Detects cleanup opportunities and executes user-confirmed cleanup.
/// Detection only reports sizes; nothing is deleted without an explicit,
/// confirmed action from the user.
/// </summary>
public sealed class CleanupService(
    ILogger<CleanupService> logger,
    WindowsUpdateService windowsUpdate,
    OneDriveService oneDrive,
    ScanHistoryService history)
{
    private static readonly string LocalAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
    private static readonly string RoamingAppData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
    private static readonly string WindowsDir = Environment.GetFolderPath(Environment.SpecialFolder.Windows);

    public IReadOnlyList<CleanupGroup>? CurrentGroups { get; private set; }
    public DateTime? DetectedAt { get; private set; }

    public event Action? DetectionChanged;
    public event Action<ComponentStoreAnalysis?>? ComponentStoreAnalyzed;

    public ComponentStoreAnalysis? LastComponentStoreAnalysis { get; private set; }
    public bool ComponentStoreCleanupRecommended => LastComponentStoreAnalysis?.CleanupRecommended == true;
    public long? ComponentStoreBytes => LastComponentStoreAnalysis?.SizeBytes;

    private static readonly string[] ChromiumCacheSubdirs =
    [
        "Cache\\Cache_Data", "Cache", "Code Cache", "GPUCache", "DawnGraphiteCache", "DawnWebGPUCache", "ShaderCache"
    ];

    private static readonly (string Name, string[] CacheRoots)[] ChromiumBrowsers =
    [
        ("Google Chrome", ["Google\\Chrome\\User Data"]),
        ("Microsoft Edge", ["Microsoft\\Edge\\User Data"]),
        ("Brave", ["BraveSoftware\\Brave-Browser\\User Data"]),
        ("Vivaldi", ["Vivaldi\\User Data"]),
        ("Opera", ["Opera Software\\Opera Stable"])
    ];

    public async Task DetectAsync(CancellationToken ct)
    {
        var detectors = new List<Task<CleanupGroup?>>
        {
            DetectStaleFileGroupAsync("user-temp", "Stale temporary files", "Temporary files not changed in at least seven days",
                "Temporary files", CleanupSafety.Safe, false, null,
                [Path.GetTempPath(), Path.Combine(LocalAppData, "Temp")], TimeSpan.FromDays(7), ct),
            DetectDirGroupAsync("windows-temp", "Windows temporary files", "Temporary files created by Windows components",
                "Temporary files", CleanupSafety.Caution, true,
                "Files in use by Windows are skipped; running elevated improves coverage.",
                [Path.Combine(WindowsDir, "Temp")], ct),
            DetectDirGroupAsync("wer", "Error reports & queues", "Windows Error Reporting archives and queues",
                "Diagnostics", CleanupSafety.Safe, false, null,
                [Path.Combine(LocalAppData, "Microsoft\\Windows\\WER\\ReportArchive"),
                 Path.Combine(LocalAppData, "Microsoft\\Windows\\WER\\ReportQueue"),
                 Path.Combine(LocalAppData, "Microsoft\\Windows\\WER\\Temp")], ct),
            DetectDirGroupAsync("crash-dumps", "Crash dumps (user)", "Minidumps written when applications crashed",
                "Diagnostics", CleanupSafety.Safe, false,
                "Deleting dumps removes the data a developer would need to debug those crashes.",
                [Path.Combine(LocalAppData, "CrashDumps")], ct),
            DetectDirGroupAsync("crash-dumps-system", "Crash dumps (system)", "Kernel minidumps and the full memory dump",
                "Diagnostics", CleanupSafety.Caution, true,
                "Deleting system dumps removes information useful for diagnosing blue screens.",
                [Path.Combine(WindowsDir, "Minidump"), Path.Combine(WindowsDir, "MEMORY.DMP")], ct),
            DetectDirGroupAsync("thumbnails", "Thumbnail cache", "Explorer thumbnail cache databases",
                "Caches", CleanupSafety.Safe, false, null,
                [Path.Combine(LocalAppData, "Microsoft\\Windows\\Explorer")], ct,
                fileFilter: p => Path.GetFileName(p).StartsWith("thumbcache_", StringComparison.OrdinalIgnoreCase)),
            DetectBrowserCachesAsync(ct),
            DetectShaderCachesAsync(ct),
            DetectDevCachesAsync(ct),
            DetectStaleInstallerGroupAsync(ct),
            DetectRecycleBinAsync(ct)
        };

        var results = await Task.WhenAll(detectors);
        var groups = new List<CleanupGroup>();
        foreach (var g in results)
        {
            if (g is null) continue;
            bool empty = (g.EstimatedBytes ?? 0) <= 0 && (g.EstimatedFiles ?? 0) <= 0;
            if (!empty || g.Id == "windows-update") groups.Add(g);
        }

        // This is deliberately included even before DISM has supplied an exact
        // estimate, so its analysis/action is discoverable in the cleanup page.
        var windowsUpdate = BuildWindowsUpdateGroup();
        if (windowsUpdate is not null) groups.Add(windowsUpdate);

        CurrentGroups = groups.OrderBy(g => g.CategoryLabel).ThenBy(g => g.Title).ToList();
        DetectedAt = DateTime.Now;
        DetectionChanged?.Invoke();
    }

    private async Task<CleanupGroup?> DetectDirGroupAsync(
        string id, string title, string description, string categoryLabel, CleanupSafety safety,
        bool requiresAdmin, string? warning, string[] roots, CancellationToken ct,
        Func<string, bool>? fileFilter = null)
    {
        if (fileFilter is not null)
            return await DetectFileGroupAsync(id, title, description, categoryLabel, safety, requiresAdmin, warning, roots, fileFilter, ct);
        return await Task.Run(() =>
        {
            try
            {
                var targets = roots.Where(r => File.Exists(r) || Directory.Exists(r)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                if (targets.Count == 0) return null;
                var result = QuickScanner.Measure(targets, ct, collectSamples: true);
                if (result.Files == 0) return null;
                return new CleanupGroup(id, title, description, categoryLabel, safety, requiresAdmin, warning,
                    result.Bytes, result.Files, targets, result.Samples);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Detection failed for group {Id}", id);
                return null;
            }
        }, ct);
    }

    /// <summary>Detects a group whose targets are individual matching files (e.g. thumbcache_*.db).</summary>
    private async Task<CleanupGroup?> DetectFileGroupAsync(
        string id, string title, string description, string categoryLabel, CleanupSafety safety,
        bool requiresAdmin, string? warning, string[] roots, Func<string, bool> fileFilter, CancellationToken ct)
    {
        return await Task.Run(() =>
        {
            try
            {
                long bytes = 0, files = 0;
                var targets = new List<string>();
                var samples = new List<string>();
                foreach (var root in roots)
                {
                    if (!Directory.Exists(root)) continue;
                    var options = new EnumerationOptions { IgnoreInaccessible = true, RecurseSubdirectories = false, AttributesToSkip = 0 };
                    foreach (var file in new DirectoryInfo(root).EnumerateFiles("*", options))
                    {
                        ct.ThrowIfCancellationRequested();
                        if (!fileFilter(file.FullName)) continue;
                        bytes += file.Length;
                        files++;
                        targets.Add(file.FullName);
                        if (samples.Count < 6) samples.Add(file.FullName);
                    }
                }
                if (files == 0) return null;
                return new CleanupGroup(id, title, description, categoryLabel, safety, requiresAdmin, warning,
                    bytes, files, targets, samples);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Detection failed for file group {Id}", id);
                return null;
            }
        }, ct);
    }

    /// <summary>
    /// Finds old, disposable files below well-known temporary roots without
    /// treating a whole active temp directory as junk. Individual files become
    /// the deletion targets, keeping recent installer/app work untouched.
    /// </summary>
    private async Task<CleanupGroup?> DetectStaleFileGroupAsync(
        string id, string title, string description, string categoryLabel, CleanupSafety safety,
        bool requiresAdmin, string? warning, string[] roots, TimeSpan minimumAge, CancellationToken ct)
    {
        return await Task.Run(() =>
        {
            try
            {
                var cutoff = DateTime.UtcNow - minimumAge;
                var targets = new List<string>();
                var samples = new List<string>();
                long bytes = 0;
                foreach (var root in roots.Where(Directory.Exists).Distinct(StringComparer.OrdinalIgnoreCase))
                {
                    var options = new EnumerationOptions
                    {
                        IgnoreInaccessible = true,
                        RecurseSubdirectories = true,
                        AttributesToSkip = 0,
                        ReturnSpecialDirectories = false
                    };
                    foreach (var file in new DirectoryInfo(root).EnumerateFiles("*", options))
                    {
                        ct.ThrowIfCancellationRequested();
                        if ((file.Attributes & FileAttributes.ReparsePoint) != 0 || file.LastWriteTimeUtc > cutoff)
                            continue;
                        targets.Add(file.FullName);
                        bytes += file.Length;
                        if (samples.Count < 6) samples.Add(file.FullName);
                    }
                }
                return targets.Count == 0 ? null : new CleanupGroup(id, title, description, categoryLabel,
                    safety, requiresAdmin, warning, bytes, targets.Count, targets, samples);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Detection failed for stale file group {Id}", id);
                return null;
            }
        }, ct);
    }

    private Task<CleanupGroup?> DetectStaleInstallerGroupAsync(CancellationToken ct)
    {
        string downloads = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
        var installers = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        { ".exe", ".msi", ".msix", ".msixbundle", ".appx", ".appxbundle" };

        return DetectFileGroupAsync("stale-installers", "Downloaded installers", "Installer files in Downloads older than 30 days",
            "User files", CleanupSafety.Caution, false,
            "Review these first: an installer can be useful for an offline reinstall.", [downloads],
            path =>
            {
                try
                {
                    var info = new FileInfo(path);
                    return installers.Contains(info.Extension) && info.LastWriteTimeUtc <= DateTime.UtcNow.AddDays(-30);
                }
                catch { return false; }
            }, ct);
    }

    private async Task<CleanupGroup?> DetectBrowserCachesAsync(CancellationToken ct)
    {
        return await Task.Run(() =>
        {
            try
            {
                var targets = new List<string>();
                var running = new List<string>();
                foreach (var (name, roots) in ChromiumBrowsers)
                {
                    string proc = name switch
                    {
                        "Google Chrome" => "chrome",
                        "Microsoft Edge" => "msedge",
                        "Brave" => "brave",
                        "Vivaldi" => "vivaldi",
                        _ => "opera"
                    };
                    if (IsProcessRunning(proc)) running.Add(name);
                    foreach (var userData in roots)
                    {
                        string userDataPath = Path.Combine(LocalAppData, userData);
                        if (!Directory.Exists(userDataPath)) continue;
                        foreach (var profile in Directory.EnumerateDirectories(userDataPath))
                        {
                            string profileName = Path.GetFileName(profile);
                            if (profileName is not ("Default" or "Profile 1" or "Profile 2" or "Profile 3" or "Profile 4" or "Profile 5" or "Guest Profile" or "System Profile")) continue;
                            foreach (var cache in ChromiumCacheSubdirs)
                            {
                                string p = Path.Combine(profile, cache);
                                if (Directory.Exists(p)) targets.Add(p);
                            }
                        }
                    }
                }
                // Firefox
                string firefoxProfiles = Path.Combine(LocalAppData, "Mozilla\\Firefox\\Profiles");
                if (Directory.Exists(firefoxProfiles))
                {
                    foreach (var profile in Directory.EnumerateDirectories(firefoxProfiles))
                    {
                        string p = Path.Combine(profile, "cache2");
                        if (Directory.Exists(p)) targets.Add(p);
                    }
                    if (IsProcessRunning("firefox")) running.Add("Firefox");
                }

                if (targets.Count == 0) return null;
                var measure = QuickScanner.Measure(targets, ct);
                if (measure.Files == 0) return null;
                string? locked = running.Count > 0 ? $"{string.Join(", ", running)} is running — open files will be skipped." : null;
                return new CleanupGroup("browser-caches", "Browser caches", "Cache data of Chromium and Firefox browsers",
                    "Caches", CleanupSafety.Safe, false, null, measure.Bytes, measure.Files, targets, measure.Samples, locked);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Browser cache detection failed");
                return null;
            }
        }, ct);
    }

    private async Task<CleanupGroup?> DetectShaderCachesAsync(CancellationToken ct)
    {
        return await Task.Run(() =>
        {
            try
            {
                var targets = new List<string>();
                void AddIfExists(string p) { if (Directory.Exists(p)) targets.Add(p); }
                AddIfExists(Path.Combine(LocalAppData, "D3DSCache"));
                AddIfExists(Path.Combine(LocalAppData, "NVIDIA\\GLCache"));
                AddIfExists(Path.Combine(LocalAppData, "NVIDIA Corporation\\NV_Cache"));
                AddIfExists(Path.Combine(LocalAppData, "AMD\\DxcCache"));
                AddIfExists(Path.Combine(LocalAppData, "AMD\\Dx9Cache"));
                AddIfExists(Path.Combine(LocalAppData, "AMD\\GLCache"));
                try
                {
                    using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam");
                    if (key?.GetValue("SteamPath") is string steam)
                        AddIfExists(Path.Combine(steam.Replace('/', '\\'), "steamapps", "shadercache"));
                }
                catch { }

                if (targets.Count == 0) return null;
                var measure = QuickScanner.Measure(targets, ct);
                if (measure.Files == 0) return null;
                return new CleanupGroup("shader-caches", "Shader caches", "GPU shader caches rebuilt automatically on next game/app start",
                    "Caches", CleanupSafety.Safe, false, null, measure.Bytes, measure.Files, targets, measure.Samples);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Shader cache detection failed");
                return null;
            }
        }, ct);
    }

    private async Task<CleanupGroup?> DetectDevCachesAsync(CancellationToken ct)
    {
        return await Task.Run(() =>
        {
            try
            {
                var targets = new List<string>();
                void AddIfExists(string p) { if (Directory.Exists(p)) targets.Add(p); }
                AddIfExists(Path.Combine(LocalAppData, "npm-cache"));
                AddIfExists(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".nuget\\packages"));
                AddIfExists(Path.Combine(LocalAppData, "pip\\cache"));

                if (targets.Count == 0) return null;
                var measure = QuickScanner.Measure(targets, ct);
                if (measure.Files == 0) return null;
                return new CleanupGroup("dev-caches", "Development caches", "Package manager caches (npm, NuGet, pip). Re-downloaded on the next build.",
                    "Development", CleanupSafety.Caution, false,
                    "Build tooling will re-download packages afterwards; first builds may be slower.",
                    measure.Bytes, measure.Files, targets, measure.Samples);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Dev cache detection failed");
                return null;
            }
        }, ct);
    }

    private async Task<CleanupGroup?> DetectRecycleBinAsync(CancellationToken ct)
    {
        return await Task.Run(() =>
        {
            try
            {
                long total = 0, items = 0;
                foreach (var drive in DriveInfo.GetDrives())
                {
                    if (drive.DriveType != DriveType.Fixed || !drive.IsReady) continue;
                    var info = new NativeMethods.SHQUERYRBINFO { CbSize = (uint)System.Runtime.InteropServices.Marshal.SizeOf<NativeMethods.SHQUERYRBINFO>() };
                    if (NativeMethods.SHQueryRecycleBin(drive.Name, ref info) == 0)
                    {
                        total += info.Size;
                        items += info.NumItems;
                    }
                }
                if (items <= 0) return null;
                return new CleanupGroup("recycle-bin", "Recycle Bin", "All files currently waiting in the Recycle Bin, on every drive",
                    "User content", CleanupSafety.Caution, false,
                    "Emptying the Recycle Bin permanently deletes these items. This cannot be undone.",
                    total, items, ["RECYCLEBIN"], []);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Recycle Bin detection failed");
                return null;
            }
        }, ct);
    }

    /// <summary>Builds the Windows Update group placeholder; the estimate arrives only after a DISM analysis.</summary>
    public CleanupGroup? BuildWindowsUpdateGroup()
    {
        if (LastComponentStoreAnalysis is { } a)
        {
            if (a.CleanupRecommended == false)
                return null; // Windows reports no cleanup is recommended
            return new CleanupGroup("windows-update", "Windows Update cleanup",
                "Component store cleanup via DISM (supported Windows maintenance). Removes superseded updates.",
                "Windows", CleanupSafety.Caution, true,
                a.CleanupRecommended == true
                    ? "Windows reports cleanup is recommended. Updating afterwards may re-download some components."
                    : "Windows does not state whether cleanup is recommended; the store can still be compacted.",
                a.SizeBytes, null, ["DISM_COMPONENT_STORE"], []);
        }
        return new CleanupGroup("windows-update", "Windows Update cleanup",
            "Component store cleanup via DISM (supported Windows maintenance). Run 'Analyze' first to obtain an estimate.",
            "Windows", CleanupSafety.Caution, true,
            "After cleanup with /ResetBase, installed updates can no longer be uninstalled.",
            null, null, ["DISM_COMPONENT_STORE"], []);
    }

    public async Task<ComponentStoreAnalysis?> AnalyzeComponentStoreAsync(CancellationToken ct)
    {
        LastComponentStoreAnalysis = await windowsUpdate.AnalyzeAsync(ct);
        ComponentStoreAnalyzed?.Invoke(LastComponentStoreAnalysis);
        // Refresh the stored group so the UI shows the measured estimate.
        var groups = CurrentGroups;
        if (groups is not null)
        {
            var list = groups.Where(g => g.Id != "windows-update").ToList();
            var wu = BuildWindowsUpdateGroup();
            if (wu is not null) list.Add(wu);
            CurrentGroups = list;
            DetectionChanged?.Invoke();
        }
        return LastComponentStoreAnalysis;
    }

    public async Task<CleanupRunResult> CleanAsync(
        IReadOnlyList<CleanupGroup> groups, IProgress<CleanupProgressReport>? progress, CancellationToken ct)
    {
        var run = await CleanCoreAsync(groups, progress, ct);
        var record = new CleanupRecord(Guid.NewGuid(), DateTime.UtcNow,
            run.Items.Select(i => new CleanupGroupStat(i.Title, i.FreedBytes, i.DeletedFiles, i.FailedFiles)).ToList(),
            run.TotalFreedBytes);
        await history.AddCleanupAsync(record);
        return run;
    }

    /// <summary>
    /// Cleanup entry point used by the elevated job runner: runs without a progress UI
    /// and still records the run in history so the UI process can see it afterwards.
    /// </summary>
    public Task<CleanupRunResult> CleanForElevationAsync(IReadOnlyList<CleanupGroup> groups, CancellationToken ct)
        => CleanAsync(groups, progress: null, ct);

    private async Task<CleanupRunResult> CleanCoreAsync(
        IReadOnlyList<CleanupGroup> groups, IProgress<CleanupProgressReport>? progress, CancellationToken ct)
    {
        var items = new List<CleanupResultItem>();
        long totalFreed = 0;

        foreach (var group in groups)
        {
            ct.ThrowIfCancellationRequested();
            long freed = 0, deleted = 0, skipped = 0, failed = 0;
            var errors = new List<string>();
            bool success = true;
            string? notes = null;

            try
            {
                progress?.Report(new CleanupProgressReport(group.Title, null, 0, 0));

                if (group.Id == "recycle-bin")
                {
                    long before = RecycleBinSize();
                    int hr = NativeMethods.SHEmptyRecycleBin(IntPtr.Zero, null,
                        NativeMethods.SHERB_NOCONFIRMATION | NativeMethods.SHERB_NOPROGRESSUI | NativeMethods.SHERB_NOSOUND);
                    if (hr == 0)
                    {
                        long after = RecycleBinSize();
                        freed = Math.Max(0, before - after);
                        deleted = group.EstimatedFiles ?? 0;
                    }
                    else
                    {
                        failed++;
                        notes = $"Windows reported an error emptying the Recycle Bin (0x{hr:X}).";
                        success = false;
                    }
                }
                else if (group.Id == "windows-update")
                {
                    var (ok, error) = await windowsUpdate.StartCleanupAsync(resetBase: false, ct);
                    if (!ok)
                    {
                        success = false;
                        failed++;
                        notes = error;
                    }
                    else
                    {
                        notes = "Component store cleanup finished. The amount freed depends on Windows servicing state and is reported by DISM in its log.";
                        freed = 0;
                    }
                }
                else if (group.Id == "onedrive-reclaim")
                {
                    if (!oneDrive.IsAvailable)
                    {
                        success = false;
                        notes = "OneDrive was not detected.";
                    }
                    else
                    {
                        var deh = await oneDrive.DehydrateManyAsync([oneDrive.RootPath!],
                            f => Task.Run(() => progress?.Report(new CleanupProgressReport(group.Title, f, deleted, freed))), ct);
                        deleted = deh.Succeeded;
                        freed = deh.BytesReclaimed;
                        failed = deh.Failed;
                        skipped = deh.Unverified;
                        notes = deh.Failed > 0
                            ? $"Some files could not be dehydrated: {deh.FirstError}"
                            : "Files remain in OneDrive; only local copies were removed.";
                    }
                }
                else
                {
                    foreach (var target in group.Targets)
                    {
                        ct.ThrowIfCancellationRequested();
                        progress?.Report(new CleanupProgressReport(group.Title, target, deleted, freed));
                        QuickScanner.DeleteContents(target, null, null, ct, errors,
                            ref deleted, ref freed, ref skipped, ref failed);
                    }
                }
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Cleanup failed for {Group}", group.Id);
                success = false;
                failed++;
                if (errors.Count < 5) errors.Add(ex.Message);
            }

            if (errors.Count > 0)
                notes = (notes is null ? "" : notes + " ") + $"e.g. {string.Join("; ", errors.Take(2))}";

            items.Add(new CleanupResultItem(group.Id, group.Title, freed, deleted, skipped, failed, notes, success));
            totalFreed += freed;
        }

        var result = new CleanupRunResult(items, totalFreed);
        return result;
    }

    /// <summary>Cleanup executed by the elevated helper instance; the parent process records the outcome.</summary>
    public Task RecordCleanupAsync(CleanupRecord record) => history.AddCleanupAsync(record);

    private static long RecycleBinSize()
    {
        long total = 0;
        foreach (var drive in DriveInfo.GetDrives())
        {
            if (drive.DriveType != DriveType.Fixed || !drive.IsReady) continue;
            var info = new NativeMethods.SHQUERYRBINFO { CbSize = (uint)System.Runtime.InteropServices.Marshal.SizeOf<NativeMethods.SHQUERYRBINFO>() };
            if (NativeMethods.SHQueryRecycleBin(drive.Name, ref info) == 0) total += info.Size;
        }
        return total;
    }

    private static bool IsProcessRunning(string name)
    {
        try { return Process.GetProcessesByName(name).Length > 0; }
        catch { return false; }
    }
}

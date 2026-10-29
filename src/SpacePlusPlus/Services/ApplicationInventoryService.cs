using System.IO;
using Microsoft.Extensions.Logging;
using Microsoft.Win32;
using SpacePlusPlus.Infrastructure;
using SpacePlusPlus.Models;

namespace SpacePlusPlus.Services;

/// <summary>
/// Enumerates installed programs from the standard Windows uninstall registry keys
/// (HKLM 64/32, HKCU). Only registered uninstall mechanisms are ever used to remove
/// software. UWP/Store apps are not inventoried because Windows does not expose their
/// disk usage through these keys; the page states this limitation.
/// </summary>
public sealed class ApplicationInventoryService(ILogger<ApplicationInventoryService> logger)
{
    private static readonly (RegistryKey Base, string Path)[] RegistryKeys =
    [
        (Registry.LocalMachine, @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall"),
        (Registry.LocalMachine, @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall"),
        (Registry.CurrentUser, @"Software\Microsoft\Windows\CurrentVersion\Uninstall")
    ];

    // The system-components switch changes the result set, so keep distinct
    // caches instead of returning a prior, filtered result for both modes.
    private readonly Dictionary<bool, IReadOnlyList<InstalledApp>> _cache = [];

    public event Action? InventoryChanged;

    public IReadOnlyList<InstalledApp> GetInstalledApps(bool includeSystemComponents = false)
    {
        if (_cache.TryGetValue(includeSystemComponents, out var cached)) return cached;

        var apps = new List<InstalledApp>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var (baseKey, path) in RegistryKeys)
        {
            try
            {
                using var root = baseKey.OpenSubKey(path);
                if (root is null) continue;
                foreach (var subName in root.GetSubKeyNames())
                {
                    try
                    {
                        using var sub = root.OpenSubKey(subName);
                        if (sub is null) continue;
                        string? name = sub.GetValue("DisplayName") as string;
                        if (string.IsNullOrWhiteSpace(name)) continue;

                        bool system = (sub.GetValue("SystemComponent") as int?) == 1;
                        if (system && !includeSystemComponents) continue;

                        if (!seen.Add(name)) continue;

                        long? reported = (sub.GetValue("EstimatedSize") as int?) is { } kb && kb > 0
                            ? (long)kb * 1024
                            : null;

                        string? uninstall = Trim(sub.GetValue("UninstallString") as string);
                        string? installLocation = NormalizeLocation(sub.GetValue("InstallLocation") as string)
                            ?? InferInstallLocation(
                                Trim(sub.GetValue("DisplayIcon") as string),
                                uninstall);

                        apps.Add(new InstalledApp(
                            Name: name.Trim(),
                            Publisher: Trim(sub.GetValue("Publisher") as string),
                            Version: Trim(sub.GetValue("DisplayVersion") as string),
                            InstallLocation: installLocation,
                            ReportedBytes: reported,
                            UninstallString: uninstall,
                            QuietUninstallString: Trim(sub.GetValue("QuietUninstallString") as string),
                            IsSystemComponent: system,
                            RegistryKeyPath: $@"{path}\{subName}"));
                    }
                    catch (Exception)
                    {
                        // Malformed registry entry; skip it.
                    }
                }
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Could not read uninstall key {Path}", path);
            }
        }

        apps.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));
        _cache[includeSystemComponents] = apps;
        return apps;
    }

    /// <summary>Measures install folders of apps that expose one. WindowsApps locations are skipped (ACL-protected).</summary>
    public async Task MeasureSizesAsync(List<InstalledApp> apps,
        IProgress<(int Done, int Total)>? progress, CancellationToken ct)
    {
        var targets = apps
            .Select((a, i) => (App: a, Index: i))
            .Where(t => t.App.InstallLocation is { } loc && Directory.Exists(loc)
                        && !loc.Contains("\\WindowsApps", StringComparison.OrdinalIgnoreCase))
            .ToList();

        // Be explicit about why a row has no actual size. This prevents a
        // silent dash from looking like a broken scan.
        for (int i = 0; i < apps.Count; i++)
        {
            if (apps[i].InstallLocation is null)
                apps[i] = apps[i] with { MeasureNote = "No install folder registered" };
            else if (!Directory.Exists(apps[i].InstallLocation))
                apps[i] = apps[i] with { MeasureNote = "Install folder is unavailable" };
            else if (apps[i].InstallLocation!.Contains("\\WindowsApps", StringComparison.OrdinalIgnoreCase))
                apps[i] = apps[i] with { MeasureNote = "Microsoft Store app — access restricted" };
        }

        int done = 0;
        var results = new (int Index, long Bytes)[targets.Count];
        int next = 0;

        var tasks = Enumerable.Range(0, Math.Min(4, Math.Max(1, targets.Count)))
            .Select(_ => Task.Run(async () =>
            {
                while (true)
                {
                    ct.ThrowIfCancellationRequested();
                    int idx = Interlocked.Increment(ref next) - 1;
                    if (idx >= targets.Count) return;
                    var (app, _) = targets[idx];
                    var result = QuickScanner.Measure([app.InstallLocation!], ct, collectSamples: false);
                    results[idx] = (targets[idx].Index, result.Bytes);
                    int d = Interlocked.Increment(ref done);
                    progress?.Report((d, targets.Count));
                }
            }))
            .ToList();

        await Task.WhenAll(tasks);
        foreach (var (index, bytes) in results)
        {
            if (index < 0 || index >= apps.Count) continue;
            var app = apps[index];
            apps[index] = app with { MeasuredBytes = bytes, MeasureNote = null };
        }
    }

    /// <summary>Launches the application's registered uninstaller. Returns false when none is registered.</summary>
    public bool Uninstall(InstalledApp app, out string? error)
    {
        error = null;
        string? command = app.UninstallString;
        if (string.IsNullOrWhiteSpace(command))
        {
            error = "This application has no registered uninstaller.";
            return false;
        }
        try
        {
            var (exe, args) = SplitCommand(command);
            var psi = new System.Diagnostics.ProcessStartInfo
            {
                FileName = exe,
                Arguments = args,
                UseShellExecute = true // lets the uninstaller trigger its own UAC prompt if required
            };
            return System.Diagnostics.Process.Start(psi) is not null;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Uninstall failed for {App}", app.Name);
            error = ex.Message;
            return false;
        }
    }

    public void InvalidateCache()
    {
        _cache.Clear();
        InventoryChanged?.Invoke();
    }

    private static string? Trim(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();

    private static string? NormalizeLocation(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return null;
        s = s.Trim().TrimEnd('\\', '"');
        return s.Length > 0 && Directory.Exists(s) ? s : (s.Length > 0 ? s : null);
    }

    /// <summary>
    /// Registry entries often omit InstallLocation. DisplayIcon and the
    /// uninstaller normally point at a file inside the installed application,
    /// allowing a real folder measurement without guessing from its name.
    /// </summary>
    private static string? InferInstallLocation(string? displayIcon, string? uninstallCommand)
    {
        foreach (var raw in new[] { displayIcon, uninstallCommand })
        {
            if (string.IsNullOrWhiteSpace(raw)) continue;
            string candidate = raw.Trim();
            int resourceComma = candidate.LastIndexOf(",", StringComparison.Ordinal);
            if (resourceComma > 0 && int.TryParse(candidate[(resourceComma + 1)..], out _))
                candidate = candidate[..resourceComma];

            try
            {
                var (exe, _) = SplitCommand(candidate);
                if (File.Exists(exe))
                {
                    var directory = Path.GetDirectoryName(exe);
                    if (!string.IsNullOrWhiteSpace(directory) && Directory.Exists(directory)) return directory;
                }
            }
            catch
            {
                // A malformed command simply is not a usable location hint.
            }
        }
        return null;
    }

    private static (string Exe, string Args) SplitCommand(string command)
    {
        command = command.Trim();
        if (command.StartsWith('"'))
        {
            int end = command.IndexOf('"', 1);
            if (end > 0) return (command[1..end], command[(end + 1)..].TrimStart());
        }
        int space = command.IndexOf(' ');
        if (space > 0 && File.Exists(command[..space]))
            return (command[..space], command[(space + 1)..].TrimStart());
        return (command, string.Empty);
    }
}

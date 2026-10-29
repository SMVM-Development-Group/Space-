using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using SpacePlusPlus.Infrastructure;
using SpacePlusPlus.Models;

namespace SpacePlusPlus.Services;

/// <summary>
/// Detects and removes preinstalled AppX bloat and manages user-level autostart
/// entries. Everything is reversible where Windows allows it and every result is
/// verified by re-querying the system — nothing is claimed without evidence.
/// AppX removal runs per user and therefore never requires elevation.
/// </summary>
public sealed class DebloatService(ILogger<DebloatService> logger)
{
    private sealed record TweakDefinition(
        string Id, string Title, string Description, string Category, bool Caution,
        IReadOnlyList<(string Key, string Value, int AppliedValue)> Values);

    // Deliberately limited to documented/current-user policy or preference keys.
    // No services, scheduled system tasks, or machine-wide settings are changed.
    private static readonly TweakDefinition[] TweakDefinitions =
    [
        new("consumer-content", "Windows recommendations", "Stops suggested apps, tips and promotional content in Windows.", "Recommendations", false,
            [( @"Software\Microsoft\Windows\CurrentVersion\ContentDeliveryManager", "SilentInstalledAppsEnabled", 0),
             ( @"Software\Microsoft\Windows\CurrentVersion\ContentDeliveryManager", "SoftLandingEnabled", 0),
             ( @"Software\Microsoft\Windows\CurrentVersion\ContentDeliveryManager", "SystemPaneSuggestionsEnabled", 0),
             ( @"Software\Microsoft\Windows\CurrentVersion\ContentDeliveryManager", "SubscribedContent-338388Enabled", 0),
             ( @"Software\Microsoft\Windows\CurrentVersion\ContentDeliveryManager", "SubscribedContent-353694Enabled", 0)]),
        new("tailored-experiences", "Tailored experiences", "Stops Windows from using diagnostic data for personalised recommendations.", "Privacy", false,
            [( @"Software\Policies\Microsoft\Windows\CloudContent", "DisableTailoredExperiencesWithDiagnosticData", 1)]),
        new("widgets", "Widgets button", "Hides the Widgets button from the taskbar.", "Taskbar", false,
            [( @"Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced", "TaskbarDa", 0)]),
        new("copilot", "Copilot", "Turns off Windows Copilot for this user where the installed Windows edition supports the policy.", "Windows features", true,
            [( @"Software\Policies\Microsoft\Windows\WindowsCopilot", "TurnOffWindowsCopilot", 1)]),
        new("game-dvr", "Game DVR", "Stops background game recording and capture features.", "Gaming", true,
            [( @"System\GameConfigStore", "GameDVR_Enabled", 0),
             ( @"Software\Microsoft\Windows\CurrentVersion\GameDVR", "AppCaptureEnabled", 0)]),
        new("edge-background", "Edge background start", "Stops Microsoft Edge from starting in the background and using Startup Boost.", "Background apps", false,
            [( @"Software\Policies\Microsoft\Edge", "StartupBoostEnabled", 0),
             ( @"Software\Policies\Microsoft\Edge", "BackgroundModeEnabled", 0)])
    ];
    // ===================== Bloat curation =====================

    /// <summary>Curated list of known preinstalled bloat (wildcards allowed).</summary>
    private static readonly (string Pattern, string Category, bool Caution)[] KnownBloat =
    [
        ("Microsoft.3DBuilder", "Microsoft", false),
        ("Microsoft.Microsoft3DViewer", "Microsoft", false),
        ("Microsoft.Print3D", "Microsoft", false),
        ("Microsoft.BingNews", "Microsoft", false),
        ("Microsoft.BingWeather", "Microsoft", false),
        ("Microsoft.BingFinance", "Microsoft", false),
        ("Microsoft.BingSports", "Microsoft", false),
        ("Microsoft.BingSearch", "Microsoft", false),
        ("Microsoft.GetHelp", "Microsoft", false),
        ("Microsoft.Getstarted", "Microsoft", false),
        ("Microsoft.WindowsFeedbackHub", "Microsoft", false),
        ("Microsoft.Messaging", "Microsoft", false),
        ("Microsoft.MicrosoftOfficeHub", "Microsoft", false),
        ("Microsoft.MicrosoftSolitaireCollection", "Microsoft", false),
        ("Microsoft.Office.OneNote", "Microsoft", false),
        ("Microsoft.OneConnect", "Microsoft", false),
        ("Microsoft.MixedReality.Portal", "Microsoft", false),
        ("Microsoft.News", "Microsoft", false),
        ("Microsoft.People", "Microsoft", false),
        ("Microsoft.SkypeApp", "Microsoft", false),
        ("Microsoft.Todos", "Microsoft", false),
        ("Microsoft.Whiteboard", "Microsoft", false),
        ("MicrosoftTeams", "Microsoft", false),
        ("Microsoft.WindowsMaps", "Microsoft", false),
        ("Microsoft.YourPhone", "Microsoft", false),
        ("Microsoft.ZuneMusic", "Microsoft", false),
        ("Microsoft.ZuneVideo", "Microsoft", false),
        ("Microsoft.Copilot", "Microsoft", false),
        ("Microsoft.OutlookForWindows", "Microsoft", false),
        ("Microsoft.549981C3F5F10", "Microsoft", false), // Cortana
        ("Clipchamp.Clipchamp", "Microsoft", false),
        ("Microsoft.PowerAutomateDesktop", "Microsoft", false),
        ("Microsoft.DevHome", "Microsoft", false),
        ("Microsoft.MicrosoftStickyNotes", "Microsoft", true),
        ("Microsoft.WindowsCommunicationsApps", "Microsoft", true), // Mail/Calendar
        ("Microsoft.GamingApp", "Gaming", true),
        ("Microsoft.XboxApp", "Gaming", true),
        ("Microsoft.Xbox.TCUI", "Gaming", true),
        ("Microsoft.XboxGameOverlay", "Gaming", true),
        ("Microsoft.XboxGamingOverlay", "Gaming", true),
        ("Microsoft.XboxIdentityProvider", "Gaming", true),
        ("Microsoft.XboxSpeechToTextOverlay", "Gaming", true),
        ("king.com.*", "Preinstalled (third-party)", false),
        ("*Facebook*", "Preinstalled (third-party)", false),
        ("*Instagram*", "Preinstalled (third-party)", false),
        ("*TikTok*", "Preinstalled (third-party)", false),
        ("*Twitter*", "Preinstalled (third-party)", false),
        ("*Netflix*", "Preinstalled (third-party)", false),
        ("SpotifyAB.*", "Preinstalled (third-party)", false),
        ("*Disney*", "Preinstalled (third-party)", false),
        ("*Duolingo*", "Preinstalled (third-party)", false),
        ("*Flipboard*", "Preinstalled (third-party)", false),
        ("*PandoraMediaInc*", "Preinstalled (third-party)", false),
        ("*Shazam*", "Preinstalled (third-party)", false),
        ("*iHeartMedia*", "Preinstalled (third-party)", false),
        ("*Fitbit*", "Preinstalled (third-party)", false),
        ("*DolbyLaboratories*", "Preinstalled (third-party)", true),
        ("CandyCrush*", "Preinstalled (third-party)", false)
    ];

    /// <summary>Packages that must never be offered for removal.</summary>
    private static readonly string[] NeverTouch =
    [
        "Microsoft.WindowsStore", "Microsoft.WindowsCalculator", "Microsoft.WindowsNotepad",
        "Microsoft.WindowsTerminal", "Microsoft.WindowsPhotos", "Microsoft.WindowsCamera",
        "Microsoft.WindowsSoundRecorder", "Microsoft.WindowsAlarms", "Microsoft.WindowsClock",
        "Microsoft.Windows.Photos", "Microsoft.SecHealthUI", "Microsoft.WindowsSecurityHealth",
        "Microsoft.DesktopAppInstaller", "Microsoft.Paint", "Microsoft.ScreenSketch",
        "Microsoft.Windows.ShellExperienceHost", "Microsoft.Windows.StartMenuExperienceHost",
        "Microsoft.VCLibs*", "Microsoft.UI.Xaml*", "Microsoft.NET.Native*", "Microsoft.Services.Store*",
        "Microsoft.WindowsStorePurchaseApp", "Microsoft.HEIFImageExtension", "Microsoft.VP9*",
        "Microsoft.WebMediaExtensions", "Microsoft.WebpImageExtension", "Microsoft.AVCEncoder",
        "Microsoft.AV1VideoExtension", "Microsoft.HEVCVideoExtension", "Microsoft.RawImageExtension",
        "Microsoft.MixedReality.Viewer", "Windows.PrintDialog", "Microsoft.AccountsControl",
        "Microsoft.AsyncTextService", "Microsoft.CredDialogHost", "Microsoft.UI.Xaml.*",
        "Microsoft.XboxGameCallableUI", "Microsoft.Windows.ParentalControls", "Microsoft.Widgets*",
        "Microsoft.StartExperiencesApp", "Microsoft.WebExperience", "Microsoft.AppInstaller"
    ];

    /// <summary>Pure matching logic so it can be unit tested.</summary>
    public static BloatEvaluation? Evaluate(string packageName, string? publisher)
    {
        if (string.IsNullOrWhiteSpace(packageName)) return null;

        foreach (var pattern in NeverTouch)
        {
            if (WildcardMatch(packageName, pattern)) return null;
        }

        foreach (var (pattern, category, caution) in KnownBloat)
        {
            if (WildcardMatch(packageName, pattern))
                return new BloatEvaluation(category, caution);
        }

        // Package metadata cannot tell whether a third-party Store app was
        // preinstalled by the OEM or deliberately installed by the user. Do not
        // turn that uncertainty into a destructive recommendation: only curated
        // patterns above are offered for removal.
        return null;
    }

    public sealed record BloatEvaluation(string Category, bool Caution);

    private static bool WildcardMatch(string text, string pattern)
    {
        if (!pattern.Contains('*'))
            return text.Equals(pattern, StringComparison.OrdinalIgnoreCase);
        var parts = pattern.Split('*');
        int pos = 0;
        for (int i = 0; i < parts.Length; i++)
        {
            if (parts[i].Length == 0) continue;
            int idx = text.IndexOf(parts[i], pos, StringComparison.OrdinalIgnoreCase);
            if (i == 0 && idx != 0) return false;
            if (idx < 0) return false;
            pos = idx + parts[i].Length;
        }
        if (parts[^1].Length > 0 && !text.EndsWith(parts[^1], StringComparison.OrdinalIgnoreCase))
            return false;
        return pos <= text.Length;
    }

    // ===================== Analysis =====================

    public async Task<DebloatAnalysisResult> AnalyzeAsync(IProgress<string>? progress, CancellationToken ct)
    {
        progress?.Report("Enumerating installed AppX packages…");
        var packages = await EnumeratePackagesAsync(ct).ConfigureAwait(false);

        var apps = new List<BloatApp>();
        foreach (var pkg in packages)
        {
            ct.ThrowIfCancellationRequested();
            var eval = Evaluate(pkg.Name, pkg.Publisher);
            if (eval is null) continue;

            progress?.Report($"Measuring {pkg.Name}…");
            long bytes = 0, files = 0;
            if (!string.IsNullOrEmpty(pkg.InstallLocation) && Directory.Exists(pkg.InstallLocation))
            {
                try
                {
                    var m = QuickScanner.Measure([pkg.InstallLocation], ct, collectSamples: false);
                    bytes = m.Bytes;
                    files = m.Files;
                }
                catch (Exception ex)
                {
                    logger.LogDebug(ex, "Size measurement failed for {Package}", pkg.Name);
                }
            }

            apps.Add(new BloatApp(pkg.Name, pkg.PackageFullName, pkg.InstallLocation,
                pkg.Publisher, bytes, files, eval.Category, eval.Caution));
        }

        progress?.Report("Reading autostart entries…");
        var startup = ReadStartupEntries();

        var tweaks = ReadTweaks();
        var before = new DebloatSnapshot(apps.Count, apps.Sum(a => a.SizeBytes), startup.Count(s => !s.CurrentlyDisabled));
        return new DebloatAnalysisResult(apps.OrderByDescending(a => a.SizeBytes).ToList(), startup, tweaks, before);
    }

    public sealed record DebloatAnalysisResult(
        IReadOnlyList<BloatApp> Apps,
        IReadOnlyList<StartupEntry> StartupEntries,
        IReadOnlyList<WindowsTweak> Tweaks,
        DebloatSnapshot Snapshot);

    private sealed record PkgInfo(string Name, string PackageFullName, string? InstallLocation, string? Publisher, bool IsFramework, bool NonRemovable);

    private static async Task<List<PkgInfo>> EnumeratePackagesAsync(CancellationToken ct)
    {
        const string script = """
            $out = Get-AppxPackage | Where-Object { -not $_.IsFramework } | ForEach-Object {
                $loc = if ($_.InstallLocation) { $_.InstallLocation.Replace('"', "'") } else { '' }
                $pub = if ($_.Publisher) { $_.Publisher.Replace('"', "'") } else { '' }
                '{' + '"n":"' + $_.Name.Replace('"', "'") + '","f":"' + $_.PackageFullName.Replace('"', "'") +
                      '","l":"' + $loc + '","p":"' + $pub + '","fw":' + ($_.IsFramework.ToString().ToLower()) +
                      ',"nr":' + ($_.NonRemovable.ToString().ToLower()) + '}'
            }
            $out -join "`n"
            """;
        string stdout = await RunPowerShellAsync(script, ct).ConfigureAwait(false);

        var result = new List<PkgInfo>();
        foreach (var line in stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            try
            {
                using var doc = JsonDocument.Parse(line);
                var r = doc.RootElement;
                result.Add(new PkgInfo(
                    r.GetProperty("n").GetString() ?? "",
                    r.GetProperty("f").GetString() ?? "",
                    NullIfEmpty(r.GetProperty("l").GetString()),
                    NullIfEmpty(r.GetProperty("p").GetString()),
                    r.GetProperty("fw").GetBoolean(),
                    r.GetProperty("nr").GetBoolean()));
            }
            catch (JsonException)
            {
                // Skip malformed lines; enumeration continues.
            }
        }
        return result.Where(p => p.PackageFullName.Length > 0 && !p.NonRemovable).ToList();
    }

    private static string? NullIfEmpty(string? s) => string.IsNullOrWhiteSpace(s) ? null : s;

    // ===================== App removal =====================

    /// <summary>
    /// Removes the given packages for the current user (no elevation needed).
    /// Every removal is verified by re-querying; running processes belonging to a
    /// removed package are counted (name + working set) for the report.
    /// </summary>
    public async Task<DebloatRunResult> RemoveAppsAsync(
        IReadOnlyList<BloatApp> apps, IProgress<string>? progress, CancellationToken ct)
    {
        var items = new List<DebloatItemResult>();
        var (procCount, procMemory) = CountRunningPackageProcesses(apps);

        var script = new StringBuilder();
        script.AppendLine("$ErrorActionPreference = 'Continue'");
        script.Append("$targets = @(");
        for (int i = 0; i < apps.Count; i++)
        {
            if (i > 0) script.Append(',');
            script.Append('\'').Append(apps[i].PackageFullName.Replace("'", "''")).Append('\'');
        }
        script.AppendLine(")");
        script.AppendLine("""
            foreach ($t in $targets) {
              $found = Get-AppxPackage -PackageTypeFilter All | Where-Object { $_.PackageFullName -eq $t }
              if (-not $found) {
                Write-Output ('{"n":"' + $t + '","ok":false,"e":"package not found"}')
                continue
              }
              try {
                $found | Remove-AppxPackage -ErrorAction Stop
                $still = Get-AppxPackage -PackageTypeFilter All | Where-Object { $_.PackageFullName -eq $t }
                if ($still) { Write-Output ('{"n":"' + $t + '","ok":false,"e":"still installed after removal"}') }
                else { Write-Output ('{"n":"' + $t + '","ok":true,"e":""}') }
              } catch {
                $msg = ($_.Exception.Message -replace '"', "'") -replace "[\r\n]", " "
                if ($msg.Length -gt 220) { $msg = $msg.Substring(0, 220) }
                Write-Output ('{"n":"' + $t + '","ok":false,"e":"' + $msg + '"}')
              }
            }
            """);

        progress?.Report($"Removing {apps.Count} package(s)…");
        string stdout = await RunPowerShellAsync(script.ToString(), ct).ConfigureAwait(false);

        var byName = apps.ToDictionary(a => a.PackageFullName, a => a);
        foreach (var line in stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            string? name = null; bool ok = false; string err = "";
            try
            {
                using var doc = JsonDocument.Parse(line);
                name = doc.RootElement.GetProperty("n").GetString();
                ok = doc.RootElement.GetProperty("ok").GetBoolean();
                err = doc.RootElement.GetProperty("e").GetString() ?? "";
            }
            catch (JsonException)
            {
                continue;
            }
            if (name is null || !byName.TryGetValue(name, out var app)) continue;

            if (ok)
            {
                progress?.Report($"Removed {app.Name}");
                items.Add(new DebloatItemResult(app.Name, true, app.SizeBytes, null));
            }
            else
            {
                logger.LogWarning("Removal failed for {Package}: {Error}", app.Name, err);
                items.Add(new DebloatItemResult(app.Name, false, 0, err.Length > 0 ? err : "Removal failed"));
            }
        }

        // Any app with no result line at all counts as failed (honest reporting).
        var reported = items.Select(i => i.Title).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var app in apps.Where(a => !reported.Contains(a.Name)))
            items.Add(new DebloatItemResult(app.Name, false, 0, "No result returned by the removal process."));

        long freed = items.Where(i => i.Success).Sum(i => i.FreedBytes);
        return new DebloatRunResult(items, freed, items.Count(i => i.Success), items.Count(i => !i.Success), procCount, procMemory);
    }

    public sealed record DebloatRunResult(
        IReadOnlyList<DebloatItemResult> Items,
        long FreedBytes,
        int Succeeded,
        int Failed,
        int RunningProcessesDetected,
        long ProcessMemoryBytes);

    private (int Count, long MemoryBytes) CountRunningPackageProcesses(IReadOnlyList<BloatApp> apps)
    {
        var roots = apps
            .Where(a => !string.IsNullOrEmpty(a.InstallLocation))
            .Select(a => a.InstallLocation!.TrimEnd('\\'))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (roots.Count == 0) return (0, 0);

        int count = 0;
        long memory = 0;
        try
        {
            foreach (var proc in Process.GetProcesses())
            {
                try
                {
                    string? path = proc.MainModule?.FileName;
                    if (path is null) continue;
                    foreach (var root in roots)
                    {
                        if (path.StartsWith(root, StringComparison.OrdinalIgnoreCase))
                        {
                            count++;
                            try { memory += proc.WorkingSet64; } catch { }
                            break;
                        }
                    }
                }
                catch { /* access denied — fine */ }
                finally
                {
                    try { proc.Dispose(); } catch { }
                }
            }
        }
        catch { }
        return (count, memory);
    }

    // ===================== Autostart =====================

    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ApprovedRunPath = @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run";
    private const string ApprovedFolderKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\StartupFolder";

    private List<StartupEntry> ReadStartupEntries()
    {
        var list = new List<StartupEntry>();

        try
        {
            using var run = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(RunKeyPath);
            using var approved = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(ApprovedRunPath);
            if (run is not null)
            {
                foreach (var name in run.GetValueNames())
                {
                    if (string.IsNullOrEmpty(name)) continue;
                    var command = run.GetValue(name) as string ?? "";
                    bool disabled = IsDisabled(approved?.GetValue(name) as byte[]);
                    list.Add(new StartupEntry("run:" + name, StartupSource.RunKey, name, command, 0, disabled));
                }
            }
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Failed reading HKCU Run key");
        }

        try
        {
            string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Startup));
            if (Directory.Exists(dir))
            {
                using var approvedFolder = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(ApprovedFolderKeyPath);
                foreach (var file in Directory.EnumerateFiles(dir))
                {
                    var name = Path.GetFileName(file);
                    var info = new FileInfo(file);
                    bool disabled = IsDisabled(approvedFolder?.GetValue(name) as byte[]);
                    list.Add(new StartupEntry("folder:" + name, StartupSource.StartupFolder, name,
                        file, info.Exists ? info.Length : 0, disabled));
                }
            }
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Failed reading the Startup folder");
        }

        return list.OrderBy(s => s.CurrentlyDisabled).ThenBy(s => s.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>Task Manager's StartupApproved format: byte 0 bit 0 set = disabled, then FILETIME.</summary>
    public static bool IsDisabled(byte[]? approvedValue)
        => approvedValue is { Length: >= 4 } v && (v[0] & 0x01) != 0;

    public static byte[] BuildDisabledValue()
    {
        var v = new byte[12];
        v[0] = 0x03; // disabled flag
        long fileTime = DateTime.UtcNow.ToFileTime();
        for (int i = 0; i < 8; i++) v[4 + i] = (byte)(fileTime >> (i * 8));
        return v;
    }

    public DebloatRunResult DisableStartup(IReadOnlyList<StartupEntry> entries, IProgress<string>? progress, CancellationToken ct)
    {
        var items = new List<DebloatItemResult>();
        foreach (var e in entries)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                if (e.Source == StartupSource.RunKey)
                {
                    using var key = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(ApprovedRunPath, writable: true);
                    key.SetValue(e.Name, BuildDisabledValue(), Microsoft.Win32.RegistryValueKind.Binary);
                    using var check = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(ApprovedRunPath);
                    if (!IsDisabled(check?.GetValue(e.Name) as byte[]))
                        throw new InvalidOperationException("Disable flag was not persisted.");
                }
                else
                {
                    using var key = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(ApprovedFolderKeyPath, writable: true);
                    key.SetValue(e.Name, BuildDisabledValue(), Microsoft.Win32.RegistryValueKind.Binary);
                    using var check = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(ApprovedFolderKeyPath);
                    if (!IsDisabled(check?.GetValue(e.Name) as byte[]))
                        throw new InvalidOperationException("Disable flag was not persisted.");
                }
                progress?.Report($"Disabled {e.Name}");
                items.Add(new DebloatItemResult(e.Name, true, 0, null));
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Failed disabling startup entry {Entry}", e.Name);
                items.Add(new DebloatItemResult(e.Name, false, 0, ex.Message));
            }
        }
        return new DebloatRunResult(items, 0, items.Count(i => i.Success), items.Count(i => !i.Success), 0, 0);
    }

    public DebloatRunResult EnableStartup(IReadOnlyList<StartupEntry> entries, IProgress<string>? progress, CancellationToken ct)
    {
        var items = new List<DebloatItemResult>();
        foreach (var e in entries)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                string path = e.Source == StartupSource.RunKey ? ApprovedRunPath : ApprovedFolderKeyPath;
                using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(path, writable: true);
                if (key?.GetValue(e.Name) is not null)
                    key.DeleteValue(e.Name, throwOnMissingValue: false);
                using var check = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(path);
                if (check?.GetValue(e.Name) is not null)
                    throw new InvalidOperationException("The disable flag could not be removed.");
                progress?.Report($"Re-enabled {e.Name}");
                items.Add(new DebloatItemResult(e.Name, true, 0, null));
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Failed enabling startup entry {Entry}", e.Name);
                items.Add(new DebloatItemResult(e.Name, false, 0, ex.Message));
            }
        }
        return new DebloatRunResult(items, 0, items.Count(i => i.Success), items.Count(i => !i.Success), 0, 0);
    }

    // ===================== Reversible Windows tweaks =====================

    private static IReadOnlyList<WindowsTweak> ReadTweaks()
    {
        var result = new List<WindowsTweak>(TweakDefinitions.Length);
        foreach (var definition in TweakDefinitions)
        {
            bool applied = true;
            try
            {
                foreach (var (keyPath, valueName, appliedValue) in definition.Values)
                {
                    using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(keyPath);
                    if (key?.GetValue(valueName) is not int value || value != appliedValue)
                    {
                        applied = false;
                        break;
                    }
                }
            }
            catch { applied = false; }
            result.Add(new WindowsTweak(definition.Id, definition.Title, definition.Description,
                definition.Category, definition.Caution, applied));
        }
        return result;
    }

    /// <summary>Applies or restores only the selected current-user preferences and verifies every value.</summary>
    public DebloatRunResult SetTweaks(IReadOnlyList<WindowsTweak> tweaks, bool apply,
        IProgress<string>? progress, CancellationToken ct)
    {
        var items = new List<DebloatItemResult>();
        foreach (var tweak in tweaks)
        {
            ct.ThrowIfCancellationRequested();
            var definition = TweakDefinitions.FirstOrDefault(d => d.Id == tweak.Id);
            if (definition is null) continue;
            try
            {
                foreach (var (keyPath, valueName, appliedValue) in definition.Values)
                {
                    using var key = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(keyPath, writable: true);
                    if (apply)
                        key.SetValue(valueName, appliedValue, Microsoft.Win32.RegistryValueKind.DWord);
                    else
                        key.DeleteValue(valueName, throwOnMissingValue: false);

                    using var check = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(keyPath);
                    object? actual = check?.GetValue(valueName);
                    if (apply && (actual is not int value || value != appliedValue))
                        throw new InvalidOperationException("The setting could not be verified.");
                    if (!apply && actual is not null)
                        throw new InvalidOperationException("The original setting could not be restored.");
                }
                progress?.Report((apply ? "Applied " : "Restored ") + definition.Title);
                items.Add(new DebloatItemResult(definition.Title, true, 0, null));
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Failed to {Action} debloat tweak {Tweak}", apply ? "apply" : "restore", tweak.Id);
                items.Add(new DebloatItemResult(definition.Title, false, 0, ex.Message));
            }
        }
        return new DebloatRunResult(items, 0, items.Count(i => i.Success), items.Count(i => !i.Success), 0, 0);
    }

    // ===================== PowerShell runner =====================

    private static async Task<string> RunPowerShellAsync(string script, CancellationToken ct)
    {
        string encoded = Convert.ToBase64String(Encoding.Unicode.GetBytes(script));
        var psi = new ProcessStartInfo
        {
            FileName = Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe"),
            Arguments = $"-NoProfile -NonInteractive -ExecutionPolicy Bypass -EncodedCommand {encoded}",
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };

        using var proc = new Process { StartInfo = psi };
        var stdout = new StringBuilder();
        proc.Start();
        var readOut = proc.StandardOutput.ReadToEndAsync(ct);
        var readErr = proc.StandardError.ReadToEndAsync(ct);

        try
        {
            await proc.WaitForExitAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            try { proc.Kill(entireProcessTree: true); } catch { }
            throw;
        }

        stdout.Append(await readOut.ConfigureAwait(false));
        string err = await readErr.ConfigureAwait(false);
        string text = stdout.ToString();
        if (proc.ExitCode != 0 && text.Length == 0 && err.Length > 0)
            throw new InvalidOperationException($"PowerShell failed (exit {proc.ExitCode}): {err.Trim()}");
        return text;
    }
}

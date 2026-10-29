namespace SpacePlusPlus.Models;

/// <summary>An installed AppX package identified as preinstalled bloat.</summary>
public sealed record BloatApp(
    string Name,
    string PackageFullName,
    string? InstallLocation,
    string? Publisher,
    long SizeBytes,
    long FileCount,
    string Category,
    bool Caution);

/// <summary>Origin of a startup entry — only user-writable sources are managed.</summary>
public enum StartupSource
{
    /// <summary>Value under HKCU\…\CurrentVersion\Run.</summary>
    RunKey,
    /// <summary>File in the user's Start Menu\Programs\Startup folder.</summary>
    StartupFolder
}

/// <summary>A user-level autostart entry that can be disabled and re-enabled.</summary>
public sealed record StartupEntry(
    string Id,
    StartupSource Source,
    string Name,
    string Command,
    long SizeBytes,
    bool CurrentlyDisabled);

/// <summary>A reversible, current-user Windows setting that reduces promotional or background features.</summary>
public sealed record WindowsTweak(
    string Id,
    string Title,
    string Description,
    string Category,
    bool Caution,
    bool IsApplied);

/// <summary>Point-in-time bloat metrics used for the before/after report.</summary>
public sealed record DebloatSnapshot(
    int BloatAppCount,
    long BloatAppBytes,
    int ActiveStartupCount);

/// <summary>What one removal/disable action actually changed (verified).</summary>
public sealed record DebloatItemResult(
    string Title,
    bool Success,
    long FreedBytes,
    string? Error);

/// <summary>Honest before/after report of a debloat run.</summary>
public sealed record DebloatOutcome(
    IReadOnlyList<DebloatItemResult> Items,
    long FreedBytes,
    int AppsRemoved,
    int AppsFailed,
    int StartupDisabled,
    int StartupEnabled,
    int RunningProcessesDetected,
    long ProcessMemoryBytes,
    DebloatSnapshot Before,
    DebloatSnapshot After)
{
    /// <summary>Factual summary — no unverified claims about boot time or RAM.</summary>
    public string SummaryText
    {
        get
        {
            var parts = new List<string>();
            if (AppsRemoved > 0) parts.Add($"{AppsRemoved} app(s) removed");
            if (StartupDisabled > 0) parts.Add($"{StartupDisabled} autostart entry(ies) disabled");
            if (RunningProcessesDetected > 0) parts.Add($"{RunningProcessesDetected} related process(es) were running");
            if (FreedBytes > 0) parts.Add($"{Infrastructure.SizeFormat.Format(FreedBytes)} of disk space freed");
            if (AppsFailed > 0) parts.Add($"{AppsFailed} item(s) could not be removed");
            return parts.Count > 0 ? string.Join(" — ", parts) : "Nothing was changed.";
        }
    }
}

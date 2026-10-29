namespace SpacePlusPlus.Models;

public enum CloudState
{
    OnlineOnly,
    LocallyAvailable,
    Pinned
}

public static class CloudStateInfo
{
    public static string Title(this CloudState s) => s switch
    {
        CloudState.OnlineOnly => "Online-only",
        CloudState.Pinned => "Always kept on device",
        _ => "Locally available"
    };
}

public sealed record OneDriveItem(
    string Path,
    string Name,
    long SizeBytes,
    CloudState State,
    bool IsEligible,
    string RelativePath);

public sealed record OneDriveSummary(
    string RootPath,
    long LocalBytes,
    long LocalFiles,
    long OnlineOnlyBytes,
    long OnlineOnlyFiles,
    long PinnedBytes,
    long PinnedFiles,
    long ReclaimableBytes,
    long ReclaimableFiles,
    bool Truncated);

public sealed record DehydrateResult(
    long Attempted,
    long Succeeded,
    long Failed,
    long Unverified,
    long BytesReclaimed,
    string? FirstError);

public sealed record InstalledApp(
    string Name,
    string? Publisher,
    string? Version,
    string? InstallLocation,
    long? ReportedBytes,
    string? UninstallString,
    string? QuietUninstallString,
    bool IsSystemComponent,
    string RegistryKeyPath,
    long? MeasuredBytes = null,
    string? MeasureNote = null)
{
    public string ReportedText => ReportedBytes is { } b && b > 0 ? Infrastructure.SizeFormat.Format(b) : "—";
    public string MeasuredText => MeasuredBytes is { } b ? Infrastructure.SizeFormat.Format(b) : MeasureNote ?? "Not measured";
}

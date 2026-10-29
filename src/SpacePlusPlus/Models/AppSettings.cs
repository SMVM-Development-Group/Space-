namespace SpacePlusPlus.Models;

public sealed class AppSettings
{
    public string DefaultScanLocation { get; set; } = string.Empty;
    public long LargeFileThresholdBytes { get; set; } = 100L << 20;
    public int ScanConcurrency { get; set; } = 4;
    public string Theme { get; set; } = Infrastructure.ThemeManager.Dark;
    public bool StartWithWindows { get; set; }
    public bool ConfirmDeletions { get; set; } = true;
    public bool ConfirmCleanup { get; set; } = true;
    public int HistoryRetention { get; set; } = 30;
    public List<string> ExcludedDirectories { get; set; } = [];
    public bool EnableFileLogging { get; set; } = true;
    public string LogLevel { get; set; } = "Information";
}

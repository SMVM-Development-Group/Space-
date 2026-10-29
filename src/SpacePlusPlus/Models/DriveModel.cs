namespace SpacePlusPlus.Models;

public sealed class DriveModel
{
    public required string RootPath { get; init; }
    public required string Letter { get; init; }
    public string Label { get; init; } = string.Empty;
    public string FileSystem { get; init; } = string.Empty;
    public long TotalBytes { get; init; }
    public long FreeBytes { get; init; }
    public long UsedBytes => TotalBytes - FreeBytes;
    public double UsedFraction => TotalBytes <= 0 ? 0 : (double)UsedBytes / TotalBytes;

    public string DisplayName => string.IsNullOrEmpty(Label) ? $"({Letter})" : $"{Label} ({Letter})";
}

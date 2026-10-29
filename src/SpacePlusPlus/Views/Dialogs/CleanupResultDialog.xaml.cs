using System.Windows;
using SpacePlusPlus.Infrastructure;
using SpacePlusPlus.Models;

namespace SpacePlusPlus.Views.Dialogs;

public sealed class CleanupResultRow
{
    public required string Title { get; init; }
    public required bool Success { get; init; }
    public required long FreedBytes { get; init; }
    public long DeletedFiles { get; init; }
    public long SkippedFiles { get; init; }
    public long FailedFiles { get; init; }
    public string? Notes { get; init; }

    public string FreedText => SizeFormat.Format(FreedBytes);
    public bool HasDetail => SkippedFiles > 0 || FailedFiles > 0 || !string.IsNullOrEmpty(Notes);
    public string DetailText
    {
        get
        {
            var parts = new List<string>();
            if (SkippedFiles > 0) parts.Add($"{SkippedFiles} file(s) skipped (in use)");
            if (FailedFiles > 0) parts.Add($"{FailedFiles} failed");
            if (!string.IsNullOrEmpty(Notes)) parts.Add(Notes);
            return string.Join(" — ", parts);
        }
    }
}

public partial class CleanupResultDialog : SpaceDialogWindow
{
    public string Headline { get; }
    public IReadOnlyList<CleanupResultRow> Items { get; }

    public CleanupResultDialog(IReadOnlyList<CleanupResultItem> items, long totalFreed, bool cancelled)
    {
        Headline = cancelled
            ? $"Cleanup was cancelled. {SizeFormat.Format(totalFreed)} was freed before cancellation."
            : totalFreed > 0
                ? $"{SizeFormat.Format(totalFreed)} of disk space was recovered."
                : "No space was recovered. See the per-category results below.";
        Items = items.Select(i => new CleanupResultRow
        {
            Title = i.Title,
            Success = i.Success,
            FreedBytes = i.FreedBytes,
            DeletedFiles = i.DeletedFiles,
            SkippedFiles = i.SkippedFiles,
            FailedFiles = i.FailedFiles,
            Notes = i.Notes
        }).ToList();
        DataContext = this;
        InitializeComponent();

        if (items.All(i => i.Success))
        {
            ResultIcon.Stroke = FindResource("Success") as System.Windows.Media.Brush;
        }
        else
        {
            ResultIcon.Stroke = FindResource("Warning") as System.Windows.Media.Brush;
            ResultIcon.Data = (System.Windows.Media.Geometry)FindResource("IconWarning");
        }
    }
}

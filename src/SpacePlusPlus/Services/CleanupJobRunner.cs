using System.IO;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using SpacePlusPlus.Infrastructure;
using SpacePlusPlus.Models;

namespace SpacePlusPlus.Services;

/// <summary>
/// Elevated cleanup mode: when Windows launches Space++ again with --cleanup-job,
/// the app runs the selected category cleanup without UI, writes a result file and exits.
/// Job files contain category IDs only — target paths are always recomputed from the
/// built-in category definitions, so nothing path-controlled can be injected.
/// </summary>
public static class CleanupJobRunner
{
    private sealed record CleanupJob(List<string> Groups);

    public static async Task<int> RunAsync(string jobPath, IServiceProvider services)
    {
        CleanupRunResult? result = null;
        string? fatal = null;
        try
        {
            var json = File.ReadAllText(jobPath);
            var job = JsonSerializer.Deserialize<CleanupJob>(json);
            if (job is null || job.Groups.Count == 0)
            {
                fatal = "Empty cleanup job.";
            }
            else
            {
                var cleanup = (CleanupService)services.GetService(typeof(CleanupService))!;
                var wus = (WindowsUpdateService)services.GetService(typeof(WindowsUpdateService))!;
                using var cts = new CancellationTokenSource(TimeSpan.FromHours(2));

                // Resolve groups from the definitions, not from the file.
                var all = new List<CleanupGroup>();
                foreach (var id in job.Groups)
                {
                    if (id == "windows-update")
                    {
                        var g = cleanup.BuildWindowsUpdateGroup();
                        if (g is not null) all.Add(g);
                    }
                    else if (id == "onedrive-reclaim")
                    {
                        var od = (OneDriveService)services.GetService(typeof(OneDriveService))!;
                        if (od.IsAvailable && od.RootPath is not null)
                            all.Add(new CleanupGroup("onedrive-reclaim", "OneDrive local copies", "Dehydrates eligible locally available files",
                                "Cloud", CleanupSafety.Caution, false, null, null, null, [od.RootPath], []));
                    }
                    else
                    {
                        // Detection runs scoped to this one group id.
                        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cts.Token);
                        var detectCt = linked.Token;
                        var groups = await DetectSingleAsync(cleanup, id, detectCt);
                        all.AddRange(groups);
                    }
                }

                result = await cleanup.CleanForElevationAsync(all, cts.Token);
            }
        }
        catch (Exception ex)
        {
            fatal = ex.Message;
        }

        try
        {
            var payload = new
            {
                Fatal = fatal,
                Items = result?.Items.Select(i => new
                {
                    i.GroupId, i.Title, i.FreedBytes, i.DeletedFiles, i.SkippedFiles, i.FailedFiles, i.Notes, i.Success
                }),
                TotalFreed = result?.TotalFreedBytes ?? 0
            };
            File.WriteAllText(jobPath + ".result", JsonSerializer.Serialize(payload, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch
        {
            return 2;
        }
        return fatal is null ? 0 : 1;
    }

    private static async Task<List<CleanupGroup>> DetectSingleAsync(CleanupService cleanup, string id, CancellationToken ct)
    {
        // Detection was built as one batch; for the elevated path we reuse the full
        // detection and filter by id. Detection is read-only (it never deletes).
        List<CleanupGroup> picked = [];
        var captured = new List<CleanupGroup>();
        void Handler()
        {
            if (cleanup.CurrentGroups is { } groups)
                captured.AddRange(groups.Where(g => g.Id == id));
        }
        cleanup.DetectionChanged += Handler;
        try
        {
            await cleanup.DetectAsync(ct);
        }
        finally
        {
            cleanup.DetectionChanged -= Handler;
        }
        picked.AddRange(captured);
        return picked;
    }
}

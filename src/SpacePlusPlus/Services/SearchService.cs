using Microsoft.Extensions.Logging;
using SpacePlusPlus.Models;

namespace SpacePlusPlus.Services;

public sealed record SearchResult(string Group, string Title, string Subtitle, string TargetKind, object Target);

/// <summary>
/// Global search across the last scan result, installed applications, cleanup
/// categories and scan history. Runs on the thread pool and returns capped result
/// sets to stay responsive on large scan data.
/// </summary>
public sealed class SearchService(
    ScanManager scanManager,
    ApplicationInventoryService inventory,
    CleanupService cleanup,
    ScanHistoryService history,
    ILogger<SearchService> logger)
{
    public async Task<IReadOnlyList<SearchResult>> SearchAsync(string query)
    {
        if (string.IsNullOrWhiteSpace(query)) return [];
        return await Task.Run(() =>
        {
            var results = new List<SearchResult>();
            var scan = scanManager.LastResult;
            if (scan is not null)
            {
                SearchTree(scan.Root, query, results, 200);
            }

            try
            {
                foreach (var app in inventory.GetInstalledApps())
                {
                    if (app.Name.Contains(query, StringComparison.OrdinalIgnoreCase))
                    {
                        results.Add(new SearchResult("Applications", app.Name,
                            app.Publisher ?? "Installed application", "app", app));
                        if (Count(results, "Applications") >= 10) break;
                    }
                }
            }
            catch (Exception ex) { logger.LogDebug(ex, "App search failed"); }

            try
            {
                var groups = cleanup.CurrentGroups;
                if (groups is not null)
                {
                    foreach (var g in groups)
                    {
                        if (g.Title.Contains(query, StringComparison.OrdinalIgnoreCase)
                            || g.CategoryLabel.Contains(query, StringComparison.OrdinalIgnoreCase))
                        {
                            results.Add(new SearchResult("Cleanup", g.Title,
                                $"{(g.EstimatedBytes is { } b ? Infrastructure.SizeFormat.Format(b) : "estimate pending")} — {g.CategoryLabel}",
                                "cleanup", g));
                        }
                    }
                }
            }
            catch (Exception ex) { logger.LogDebug(ex, "Cleanup search failed"); }

            try
            {
                foreach (var record in history.GetScans())
                {
                    if (record.DisplayName.Contains(query, StringComparison.OrdinalIgnoreCase)
                        || record.RootPath.Contains(query, StringComparison.OrdinalIgnoreCase)
                        || record.TimestampUtc.ToString("yyyy-MM-dd").Contains(query, StringComparison.OrdinalIgnoreCase))
                    {
                        results.Add(new SearchResult("Scan history", record.DisplayName,
                            $"{record.TimestampUtc.ToLocalTime():yyyy-MM-dd HH:mm} — {Infrastructure.SizeFormat.Format(record.TotalBytes)}", "history", record));
                        if (Count(results, "Scan history") >= 5) break;
                    }
                }
            }
            catch (Exception ex) { logger.LogDebug(ex, "History search failed"); }

            return results.Take(60).ToList();
        });
    }

    private static int Count(List<SearchResult> results, string group) => results.Count(r => r.Group == group);

    /// <summary>Breadth-first search through the directory tree, then largest-first ranking.</summary>
    private static void SearchTree(DirectoryNode root, string query, List<SearchResult> results, int collectLimit)
    {
        var files = new List<FileNode>(64);
        var dirs = new List<DirectoryNode>(64);
        var queue = new Queue<DirectoryNode>();
        queue.Enqueue(root);
        int visited = 0;
        while (queue.Count > 0 && visited < 150_000)
        {
            var node = queue.Dequeue();
            visited++;
            foreach (var child in node.Children) queue.Enqueue(child);
            foreach (var f in node.LargeFiles)
            {
                if (files.Count < collectLimit && f.Name.Contains(query, StringComparison.OrdinalIgnoreCase))
                    files.Add(f);
            }
            if (dirs.Count < collectLimit && node != root && node.Name.Contains(query, StringComparison.OrdinalIgnoreCase))
                dirs.Add(node);
            if (files.Count >= collectLimit && dirs.Count >= collectLimit) break;
        }

        foreach (var f in files.OrderByDescending(f => f.SizeBytes).Take(20))
            results.Add(new SearchResult("Files", f.Name, $"{Infrastructure.SizeFormat.Format(f.SizeBytes)} — {f.Path}", "file", f));
        foreach (var d in dirs.OrderByDescending(d => d.TotalSize).Take(12))
            results.Add(new SearchResult("Folders", d.Name, $"{Infrastructure.SizeFormat.Format(d.TotalSize)} — {d.Path}", "folder", d));
    }
}

using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Security.Principal;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using SpacePlusPlus.Infrastructure;

namespace SpacePlusPlus.Services;

public sealed record ComponentStoreAnalysis(
    long? SizeBytes,
    bool? CleanupRecommended,
    string RawSummary,
    string? Error)
{
    public bool HasData => SizeBytes is not null || CleanupRecommended is not null;
}

/// <summary>
/// Windows component store (Windows Update) maintenance through DISM — a supported
/// Windows mechanism. Never deletes files from servicing directories directly.
/// Analysis and cleanup both require elevation and are executed via a UAC prompt.
/// </summary>
public sealed class WindowsUpdateService(ILogger<WindowsUpdateService> logger)
{
    private static readonly Regex SizeRegex = new(
        @"Actual Size of Component Store\s*:?\s*([\d.,]+)\s*(KB|MB|GB|TB)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex RecommendedRegex = new(
        @"Cleanup Recommended\s*:\s*(Yes|No)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public static bool IsElevated
    {
        get
        {
            using var identity = WindowsIdentity.GetCurrent();
            return identity is not null && new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
        }
    }

    public async Task<ComponentStoreAnalysis?> AnalyzeAsync(CancellationToken ct)
    {
        var (log, ok, error) = await RunDismAsync("/Online /Cleanup-Image /AnalyzeComponentStore", TimeSpan.FromMinutes(10), ct);
        if (log is null) return new ComponentStoreAnalysis(null, null, string.Empty, error ?? "DISM could not be executed.");
        var analysis = Parse(log);
        if (!analysis.HasData && !ok)
            return new ComponentStoreAnalysis(null, null, log, "DISM reported an error. The component store could not be analyzed.");
        return analysis;
    }

    /// <param name="resetBase">Supersedes updates permanently: they can no longer be uninstalled afterwards.</param>
    public async Task<(bool Success, string? Error)> StartCleanupAsync(bool resetBase, CancellationToken ct)
    {
        string args = resetBase
            ? "/Online /Cleanup-Image /StartComponentCleanup /ResetBase"
            : "/Online /Cleanup-Image /StartComponentCleanup";
        var (log, ok, error) = await RunDismAsync(args, TimeSpan.FromHours(1), ct);
        if (!ok) return (false, error ?? "DISM cleanup did not complete successfully.");
        logger.LogInformation("Component store cleanup finished. Log tail: {Tail}", Truncate(log, 400));
        return (true, null);
    }

    private ComponentStoreAnalysis Parse(string log)
    {
        long? size = null;
        bool? recommended = null;
        var m = SizeRegex.Match(log);
        if (m.Success && double.TryParse(m.Groups[1].Value.Replace(",", "."), CultureInfo.InvariantCulture, out var value))
        {
            size = m.Groups[2].Value.ToUpperInvariant() switch
            {
                "KB" => (long)(value * 1024),
                "MB" => (long)(value * 1024 * 1024),
                "GB" => (long)(value * 1024 * 1024 * 1024),
                "TB" => (long)(value * 1024L * 1024 * 1024 * 1024),
                _ => null
            };
        }
        m = RecommendedRegex.Match(log);
        if (m.Success) recommended = string.Equals(m.Groups[1].Value, "Yes", StringComparison.OrdinalIgnoreCase);
        return new ComponentStoreAnalysis(size, recommended, log, null);
    }

    private async Task<(string? Log, bool Ok, string? Error)> RunDismAsync(string arguments, TimeSpan timeout, CancellationToken ct)
    {
        string logPath = Path.Combine(Path.GetTempPath(), $"SpacePlusPlus-dism-{Guid.NewGuid():N}.log");
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "cmd.exe",
                Arguments = $"/c dism {arguments} > \"{logPath}\" 2>&1",
                UseShellExecute = true,
                Verb = "runas",
                WindowStyle = ProcessWindowStyle.Hidden
            };
            using var process = Process.Start(psi);
            if (process is null) return (null, false, "Windows refused to start the elevated process.");
            await process.WaitForExitAsync(ct).WaitAsync(timeout, ct);
            string log = File.Exists(logPath) ? File.ReadAllText(logPath) : string.Empty;
            return (log, process.ExitCode == 0, process.ExitCode == 0 ? null : $"DISM exited with code {process.ExitCode}.");
        }
        catch (OperationCanceledException) { throw; }
        catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            return (null, false, "Elevation was declined. Component store analysis requires administrator rights.");
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "DISM execution failed");
            return (null, false, ex.Message);
        }
        finally
        {
            try { if (File.Exists(logPath)) File.Delete(logPath); } catch { }
        }
    }

    private static string Truncate(string? s, int max) => s is null ? string.Empty : (s.Length <= max ? s : s[..max]);
}

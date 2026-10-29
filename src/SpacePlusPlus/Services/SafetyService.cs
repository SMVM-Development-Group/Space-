using System.IO;
using SpacePlusPlus.Infrastructure;
using SpacePlusPlus.Models;

namespace SpacePlusPlus.Services;

public enum DeletionSafety
{
    Allowed,
    Warning,
    Blocked
}

public sealed record SafetyAssessment(DeletionSafety Level, string Message);

/// <summary>
/// Central safety gate for all destructive operations. Nothing is ever deleted
/// without passing through these checks.
/// </summary>
public sealed class SafetyService
{
    private readonly string _windowsDir = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
    private readonly string _programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
    private readonly string _programFilesX86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
    private readonly string _userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    public SafetyAssessment Assess(string path)
    {
        string full;
        try { full = Path.GetFullPath(path); }
        catch (Exception) { return new SafetyAssessment(DeletionSafety.Blocked, "Invalid path."); }

        if (!File.Exists(full) && !Directory.Exists(full))
            return new SafetyAssessment(DeletionSafety.Blocked, "The item no longer exists.");

        if (_userProfile.Length > 0 && full.TrimEnd('\\').Equals(_userProfile.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase))
            return new SafetyAssessment(DeletionSafety.Blocked, "This is your user profile root folder.");

        if (_windowsDir.Length > 0 && IsUnder(full, _windowsDir))
        {
            if (IsUnder(full, Path.Combine(_windowsDir, "WinSxS")))
                return new SafetyAssessment(DeletionSafety.Blocked, "Windows component store — managed exclusively by Windows servicing.");
            if (IsUnder(full, Path.Combine(_windowsDir, "Installer")))
                return new SafetyAssessment(DeletionSafety.Blocked, "Windows Installer cache — deleting these files can break software repair and uninstall.");
            if (IsUnder(full, Path.Combine(_windowsDir, "System32")))
                return new SafetyAssessment(DeletionSafety.Blocked, "Windows system directory.");
            return new SafetyAssessment(DeletionSafety.Blocked, "Inside the Windows directory — system-critical.");
        }

        string root = Path.GetPathRoot(full) ?? string.Empty;
        if (root.Length > 0 && full.TrimEnd('\\').Equals(root.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase))
            return new SafetyAssessment(DeletionSafety.Blocked, "This is a drive root.");

        if (full.Contains("\\$Recycle.Bin", StringComparison.OrdinalIgnoreCase))
            return new SafetyAssessment(DeletionSafety.Blocked, "Use the dedicated Recycle Bin cleanup instead of deleting bin internals.");

        if (_programFiles.Length > 0 && IsUnder(full, _programFiles) ||
            _programFilesX86.Length > 0 && IsUnder(full, _programFilesX86))
            return new SafetyAssessment(DeletionSafety.Warning, "Inside Program Files — deleting files may break an installed application. Uninstall programs from the Applications page instead.");

        if (IsUnder(full, Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData))))
            return new SafetyAssessment(DeletionSafety.Warning, "Inside ProgramData — shared application data may be removed.");

        return new SafetyAssessment(DeletionSafety.Allowed, string.Empty);
    }

    private static bool IsUnder(string full, string baseDir)
    {
        if (string.IsNullOrEmpty(baseDir)) return false;
        var b = baseDir.TrimEnd('\\');
        return full.StartsWith(b + "\\", StringComparison.OrdinalIgnoreCase)
            || full.TrimEnd('\\').Equals(b, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Deletes files to the Recycle Bin (undoable) or permanently when requested.</summary>
    public (long DeletedCount, long FreedBytes, List<(string Path, string Error)> Failures) Delete(
        IReadOnlyList<(string Path, long Size)> files, bool permanent, CancellationToken ct)
    {
        long deleted = 0, freed = 0;
        var failures = new List<(string, string)>();
        foreach (var (path, size) in files)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var attrs = File.GetAttributes(path);
                if ((attrs & FileAttributes.ReadOnly) != 0)
                    File.SetAttributes(path, attrs & ~FileAttributes.ReadOnly);

                var op = new NativeMethods.SHFILEOPSTRUCT
                {
                    Hwnd = IntPtr.Zero,
                    Func = NativeMethods.FO_DELETE,
                    From = path + "\0",
                    Flags = (ushort)(NativeMethods.FOF_SILENT | NativeMethods.FOF_NOCONFIRMATION | NativeMethods.FOF_NOERRORUI
                                     | (permanent ? 0 : NativeMethods.FOF_ALLOWUNDO))
                };
                int hr = NativeMethods.SHFileOperation(ref op);
                if (hr == 0 && !op.Aborted && !File.Exists(path))
                {
                    deleted++;
                    freed += size;
                }
                else
                {
                    failures.Add((path, hr == 0 ? "Operation was aborted." : NativeMethods.HrMessage(hr)));
                }
            }
            catch (Exception ex)
            {
                failures.Add((path, ex.Message));
            }
        }
        return (deleted, freed, failures);
    }
}

using System.IO;
using SpacePlusPlus.Models;

namespace SpacePlusPlus.Services;

/// <summary>
/// Heuristic path/extension based classification. Results are estimates: a file's
/// purpose cannot be determined reliably from metadata alone.
/// </summary>
public sealed class FileClassificationService
{
    private static readonly HashSet<string> TempExtensions = new(StringComparer.OrdinalIgnoreCase)
    { ".tmp", ".temp", ".dmp", ".bak", ".old", ".log" };

    private static readonly HashSet<string> DevExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".cs", ".cpp", ".cc", ".cxx", ".h", ".hpp", ".csproj", ".vcxproj", ".sln", ".suo", ".pdb", ".obj",
        ".ilk", ".idb", ".lib", ".a", ".o", ".pyc", ".pyd", ".class", ".jar", ".war", ".java", ".kt", ".go",
        ".rs", ".swift", ".rb", ".php", ".js", ".mjs", ".ts", ".tsx", ".jsx", ".vue", ".wasm", ".dll.config"
    };

    private static readonly HashSet<string> MediaExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mp4", ".mkv", ".avi", ".mov", ".wmv", ".flv", ".webm", ".m4v", ".ts", ".mpg", ".mpeg",
        ".mp3", ".flac", ".wav", ".ogg", ".m4a", ".aac", ".wma", ".opus", ".mid",
        ".jpg", ".jpeg", ".png", ".gif", ".bmp", ".webp", ".heic", ".heif", ".raw", ".cr2", ".nef", ".arw",
        ".tif", ".tiff", ".svg"
    };

    private static readonly HashSet<string> ArchiveExtensions = new(StringComparer.OrdinalIgnoreCase)
    { ".zip", ".7z", ".rar", ".tar", ".gz", ".bz2", ".xz", ".zst", ".iso", ".img", ".vhd", ".vhdx", ".cab", ".wim" };

    private static readonly HashSet<string> ExecutableExtensions = new(StringComparer.OrdinalIgnoreCase)
    { ".exe", ".msi", ".msix", ".appx", ".appxbundle" };

    private static readonly string[] DevPathMarkers =
    [
        "\\node_modules\\", "\\.git", "\\.nuget\\", "\\__pycache__\\", "\\.venv\\", "\\venv\\",
        "\\.gradle\\", "\\.m2\\", "\\packages\\", "\\.vs\\", "\\.vscode\\", "\\bin\\Debug", "\\bin\\Release",
        "\\cargo\\", "\\.cargo\\", "\\target\\debug", "\\target\\release", "\\.cache\\"
    ];

    private static readonly string[] GamePathMarkers =
    [
        "\\steamapps\\", "\\steamlibrary\\", "\\steam\\", "\\xboxgames\\", "\\epic games\\", "\\gog galaxy\\",
        "\\games\\", "\\origin games\\", "\\ubisoft\\", "\\battle.net\\", "\\riot games\\"
    ];

    private string? _oneDriveRoot;

    public StorageCategory Classify(string fullPath, string extension, bool isDirectory = false)
    {
        string path = fullPath;
        string ext = string.IsNullOrEmpty(extension) ? Path.GetExtension(path) : extension;

        if (IsCloudPath(path)) return StorageCategory.CloudSync;

        if (HasSegment(path, "temp") || HasSegment(path, "tmp") || TempExtensions.Contains(ext))
            return StorageCategory.TempFiles;

        if (HasSegment(path, "downloads")) return StorageCategory.Downloads;

        if (IsProgramFiles(path) || HasAnyMarker(path, GamePathMarkers) || ExecutableExtensions.Contains(ext))
            return StorageCategory.Applications;

        if (DevExtensions.Contains(ext) || HasAnyMarker(path, DevPathMarkers))
            return StorageCategory.Development;

        if (MediaExtensions.Contains(ext)) return StorageCategory.Media;
        if (ArchiveExtensions.Contains(ext)) return StorageCategory.Archives;

        return StorageCategory.Other;
    }

    public bool IsCloudPath(string fullPath)
    {
        _oneDriveRoot ??= DetectOneDriveRoot();
        if (_oneDriveRoot is { } root && fullPath.StartsWith(root, StringComparison.OrdinalIgnoreCase))
            return true;
        return HasSegment(fullPath, "onedrive");
    }

    public static string? DetectOneDriveRoot()
    {
        string?[] candidates =
        [
            Environment.GetEnvironmentVariable("OneDrive"),
            Environment.GetEnvironmentVariable("OneDriveConsumer"),
            Environment.GetEnvironmentVariable("OneDriveCommercial")
        ];
        foreach (var c in candidates)
            if (!string.IsNullOrWhiteSpace(c) && Directory.Exists(c))
                return EnsureTrailing(c);
        try
        {
            foreach (var sub in new[] { @"Accounts\Personal", @"Accounts\Business1", "" })
            {
                using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\OneDrive" + (sub.Length == 0 ? "" : "\\" + sub));
                if (key?.GetValue("UserFolder") is string folder && Directory.Exists(folder))
                    return EnsureTrailing(folder);
            }
        }
        catch
        {
            // Registry access failure means no OneDrive detection; not an error.
        }
        return null;
    }

    private static string EnsureTrailing(string path)
        => path.EndsWith(Path.DirectorySeparatorChar) ? path : path + Path.DirectorySeparatorChar;

    private static bool HasSegment(string path, string segment)
    {
        var span = path.AsSpan();
        int i = 0;
        while (i < span.Length)
        {
            int sep = span[i..].IndexOfAny(['\\', '/']);
            if (sep < 0) break;
            int end = i + sep;
            if (end - i == segment.Length && span.Slice(i, end - i).Equals(segment, StringComparison.OrdinalIgnoreCase))
                return true;
            i = end + 1;
        }
        return false;
    }

    private static bool IsProgramFiles(string path)
    {
        var pf = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        var pf86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
        return (pf.Length > 0 && path.StartsWith(pf, StringComparison.OrdinalIgnoreCase))
            || (pf86.Length > 0 && path.StartsWith(pf86, StringComparison.OrdinalIgnoreCase));
    }

    private static bool HasAnyMarker(string path, string[] markers)
    {
        foreach (var m in markers)
            if (path.Contains(m, StringComparison.OrdinalIgnoreCase))
                return true;
        return false;
    }
}

public static class ExclusionMatcher
{
    public static bool IsExcluded(string fullPath, IReadOnlyList<string> patterns)
    {
        if (patterns.Count == 0) return false;
        foreach (var raw in patterns)
        {
            var pattern = raw.Trim();
            if (pattern.Length == 0) continue;
            bool absolute = pattern.Contains(':') || pattern.StartsWith('\\') || pattern.StartsWith('/');
            pattern = pattern.TrimEnd('\\', '/');
            if (absolute)
            {
                if (fullPath.Equals(pattern, StringComparison.OrdinalIgnoreCase)) return true;
                if (fullPath.StartsWith(pattern + "\\", StringComparison.OrdinalIgnoreCase)) return true;
            }
            else
            {
                if (fullPath.Contains("\\" + pattern + "\\", StringComparison.OrdinalIgnoreCase)) return true;
                if (fullPath.EndsWith("\\" + pattern, StringComparison.OrdinalIgnoreCase)) return true;
            }
        }
        return false;
    }
}

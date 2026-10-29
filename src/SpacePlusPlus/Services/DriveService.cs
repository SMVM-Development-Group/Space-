using System.IO;
using SpacePlusPlus.Models;

namespace SpacePlusPlus.Services;

public interface IDriveService
{
    IReadOnlyList<DriveModel> GetDrives();
    DriveModel? FindDrive(string rootPath);
}

public sealed class DriveService : IDriveService
{
    public IReadOnlyList<DriveModel> GetDrives()
    {
        var list = new List<DriveModel>();
        foreach (var d in DriveInfo.GetDrives())
        {
            if (d.DriveType != DriveType.Fixed || !d.IsReady) continue;
            try
            {
                string letter = d.Name.Length >= 1 ? d.Name[..1] : d.Name;
                list.Add(new DriveModel
                {
                    RootPath = d.RootDirectory.FullName,
                    Letter = letter,
                    Label = SafeLabel(d),
                    FileSystem = SafeFormat(d),
                    TotalBytes = d.TotalSize,
                    FreeBytes = d.AvailableFreeSpace
                });
            }
            catch (Exception)
            {
                // Drive disappeared mid-enumeration; skip it.
            }
        }
        return list.OrderBy(d => d.Letter).ToList();
    }

    public DriveModel? FindDrive(string rootPath)
    {
        string root = Path.GetPathRoot(Path.GetFullPath(rootPath)) ?? string.Empty;
        return GetDrives().FirstOrDefault(d =>
            string.Equals(d.RootPath, root, StringComparison.OrdinalIgnoreCase));
    }

    private static string SafeLabel(DriveInfo d)
    {
        try { return d.VolumeLabel ?? string.Empty; }
        catch { return string.Empty; }
    }

    private static string SafeFormat(DriveInfo d)
    {
        try { return d.DriveFormat ?? string.Empty; }
        catch { return string.Empty; }
    }
}

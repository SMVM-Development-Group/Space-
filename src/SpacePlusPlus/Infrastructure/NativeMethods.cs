using System.Runtime.InteropServices;

namespace SpacePlusPlus.Infrastructure;

/// <summary>Win32 / Cloud Files API interop used by Space++ services.</summary>
internal static class NativeMethods
{
    // File attribute bits not fully represented by System.IO.FileAttributes.
    public const uint FILE_ATTRIBUTE_OFFLINE = 0x0000_1000;
    public const uint FILE_ATTRIBUTE_RECALL_ON_OPEN = 0x0004_0000;
    public const uint FILE_ATTRIBUTE_UNPINNED = 0x0010_0000;
    public const uint FILE_ATTRIBUTE_PINNED = 0x0020_0000;
    public const uint FILE_ATTRIBUTE_RECALL_ON_DATA_ACCESS = 0x0040_0000;

    public const int DWMWA_WINDOW_CORNER_PREFERENCE = 33;
    public const int DWMWCP_ROUND = 2;

    [DllImport("dwmapi.dll")]
    public static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    [DllImport("kernelbase.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern uint GetFileAttributesW(string fileName);

    [StructLayout(LayoutKind.Sequential, Pack = 8)]
    public struct SHQUERYRBINFO
    {
        public uint CbSize;
        public long Size;
        public long NumItems;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    public static extern int SHQueryRecycleBin(string rootPath, ref SHQUERYRBINFO info);

    public const uint SHERB_NOCONFIRMATION = 0x1;
    public const uint SHERB_NOPROGRESSUI = 0x2;
    public const uint SHERB_NOSOUND = 0x4;

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    public static extern int SHEmptyRecycleBin(IntPtr hwnd, string? rootPath, uint flags);

    public const uint FO_DELETE = 3;
    public const ushort FOF_SILENT = 0x4;
    public const ushort FOF_NOCONFIRMATION = 0x10;
    public const ushort FOF_ALLOWUNDO = 0x40;
    public const ushort FOF_NOERRORUI = 0x400;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct SHFILEOPSTRUCT
    {
        public IntPtr Hwnd;
        public uint Func;
        public string From;
        public string? To;
        public ushort Flags;
        public bool Aborted;
        public IntPtr NameMappings;
        public string? ProgressTitle;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    public static extern int SHFileOperation(ref SHFILEOPSTRUCT op);

    [DllImport("cldapi.dll", CharSet = CharSet.Unicode)]
    public static extern int CfOpenFileWithOplock(string path, uint oplockType, out IntPtr handle);

    [DllImport("cldapi.dll")]
    public static extern int CfDehydratePlaceholder(IntPtr fileHandle);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool CloseHandle(IntPtr handle);

    public static string HrMessage(int hr)
    {
        var ex = Marshal.GetExceptionForHR(hr);
        return ex is null ? $"HRESULT 0x{ unchecked((uint)hr):X8}" : ex.Message;
    }
}

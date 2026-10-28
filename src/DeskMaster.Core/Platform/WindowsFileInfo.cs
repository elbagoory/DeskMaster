using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Win32.SafeHandles;

namespace DeskMaster.Core.Platform;

/// <summary>On-disk facts .NET does not expose: allocated size of compressed files and hard-link identity.</summary>
[SupportedOSPlatform("windows")]
internal static partial class WindowsFileInfo
{
    [StructLayout(LayoutKind.Sequential)]
    private struct ByHandleFileInformation
    {
        public uint FileAttributes;
        public uint CreationLow, CreationHigh, AccessLow, AccessHigh, WriteLow, WriteHigh;
        public uint VolumeSerialNumber;
        public uint FileSizeHigh, FileSizeLow;
        public uint NumberOfLinks;
        public uint FileIndexHigh, FileIndexLow;
    }

    [LibraryImport("kernel32.dll", EntryPoint = "GetCompressedFileSizeW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    private static partial uint GetCompressedFileSize(string path, out uint high);

    [LibraryImport("kernel32.dll", EntryPoint = "CreateFileW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    private static partial SafeFileHandle CreateFile(string path, uint access, uint share, IntPtr security, uint disposition, uint flags, IntPtr template);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetFileInformationByHandle(SafeFileHandle handle, out ByHandleFileInformation info);

    /// <summary>Bytes actually used on disk by a compressed or sparse file, or null if unknown.</summary>
    public static long? AllocatedSize(string path)
    {
        var low = GetCompressedFileSize(path, out var high);
        if (low == uint.MaxValue && Marshal.GetLastPInvokeError() != 0)
            return null;
        return ((long)high << 32) | low;
    }

    /// <summary>A unique id for files with more than one hard link; null for ordinary files.</summary>
    public static (uint Volume, ulong Index)? HardLinkId(string path)
    {
        const uint readAttributes = 0x80, shareAll = 0x7, openExisting = 3, backupSemantics = 0x02000000;
        using var handle = CreateFile(path, readAttributes, shareAll, IntPtr.Zero, openExisting, backupSemantics, IntPtr.Zero);
        if (handle.IsInvalid || !GetFileInformationByHandle(handle, out var info) || info.NumberOfLinks <= 1)
            return null;
        return (info.VolumeSerialNumber, ((ulong)info.FileIndexHigh << 32) | info.FileIndexLow);
    }
}

using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace DeskMaster.Core.Platform;

[SupportedOSPlatform("windows")]
internal static partial class WindowsRecycleBin
{
    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    private struct ShQueryRbInfo
    {
        public int Size;
        public long SizeInBytes;
        public long NumItems;
    }

    private const uint NoConfirmation = 0x1;
    private const uint NoProgressUi = 0x2;
    private const uint NoSound = 0x4;

    [LibraryImport("shell32.dll", EntryPoint = "SHQueryRecycleBinW", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int SHQueryRecycleBin(string? rootPath, ref ShQueryRbInfo info);

    [LibraryImport("shell32.dll", EntryPoint = "SHEmptyRecycleBinW", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int SHEmptyRecycleBin(IntPtr hwnd, string? rootPath, uint flags);

    public static (long Bytes, long Items) Query()
    {
        var info = new ShQueryRbInfo { Size = Marshal.SizeOf<ShQueryRbInfo>() };
        return SHQueryRecycleBin(null, ref info) == 0 ? (info.SizeInBytes, info.NumItems) : (0, 0);
    }

    /// <summary>Empties the Recycle Bin on all drives. Returns true on success (or when already empty).</summary>
    public static bool Empty()
    {
        var hr = SHEmptyRecycleBin(IntPtr.Zero, null, NoConfirmation | NoProgressUi | NoSound);
        // 0x8000FFFF (E_UNEXPECTED) is returned when the bin is already empty.
        return hr == 0 || hr == unchecked((int)0x8000FFFF);
    }
}

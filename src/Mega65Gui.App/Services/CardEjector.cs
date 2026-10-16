using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Mega65Gui.App.Services;

/// <summary>
/// Safely ejects an SD card in a PC card reader: flushes, locks and dismounts the volume, then
/// asks the reader to eject the media (the same steps as Windows' "Eject" for removable drives).
/// </summary>
public static class CardEjector
{
    private const uint GenericRead = 0x80000000, GenericWrite = 0x40000000, ShareReadWrite = 3, OpenExisting = 3;
    private const uint FsctlLockVolume = 0x00090018, FsctlDismountVolume = 0x00090020;
    private const uint IoctlStorageMediaRemoval = 0x002D4804, IoctlStorageEjectMedia = 0x002D4808;

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern SafeFileHandle CreateFile(string name, uint access, uint share, IntPtr security, uint creation, uint flags, IntPtr template);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool DeviceIoControl(SafeFileHandle device, uint code, byte[]? inBuffer, int inSize, IntPtr outBuffer, int outSize, out int returned, IntPtr overlapped);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool FlushFileBuffers(SafeFileHandle handle);

    /// <summary>The drive letter ("E:") for a path on a removable drive, or null when the path is not on one.</summary>
    public static string? RemovableDriveOf(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        try
        {
            var root = Path.GetPathRoot(Path.GetFullPath(path));
            if (root is null || root.Length < 2 || root[1] != ':') return null;
            var drive = new DriveInfo(root);
            return drive.DriveType == DriveType.Removable ? root[..2].ToUpperInvariant() : null;
        }
        catch (Exception e) when (e is ArgumentException or IOException) { return null; }
    }

    /// <summary>Ejects the card. Throws with a plain explanation when a program still has files open on it.</summary>
    public static void Eject(string drive)
    {
        using var h = CreateFile(@"\\.\" + drive, GenericRead | GenericWrite, ShareReadWrite, IntPtr.Zero, OpenExisting, 0, IntPtr.Zero);
        if (h.IsInvalid) throw new Win32Exception(Marshal.GetLastWin32Error(), $"Could not open {drive}.");
        FlushFileBuffers(h);
        var locked = false;
        for (var i = 0; i < 10 && !locked; i++)
        {
            locked = DeviceIoControl(h, FsctlLockVolume, null, 0, IntPtr.Zero, 0, out _, IntPtr.Zero);
            if (!locked) Thread.Sleep(300);
        }
        if (!locked)
            throw new IOException($"{drive} is still in use. Close any windows or programs using the card (including Explorer windows) and try again.");
        if (!DeviceIoControl(h, FsctlDismountVolume, null, 0, IntPtr.Zero, 0, out _, IntPtr.Zero))
            throw new Win32Exception(Marshal.GetLastWin32Error(), $"Could not dismount {drive}.");
        DeviceIoControl(h, IoctlStorageMediaRemoval, [0], 1, IntPtr.Zero, 0, out _, IntPtr.Zero);
        if (!DeviceIoControl(h, IoctlStorageEjectMedia, null, 0, IntPtr.Zero, 0, out _, IntPtr.Zero))
            throw new Win32Exception(Marshal.GetLastWin32Error(), $"Could not eject {drive}.");
    }
}

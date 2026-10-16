using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Win32.SafeHandles;

namespace Mega65Gui.Core.Sd;

public interface IContiguityChecker
{
    /// <summary>Number of on-disk extents for the file, or null if it cannot be determined.</summary>
    int? CountFragments(string path);
}

/// <summary>Uses FSCTL_GET_RETRIEVAL_POINTERS to count a file's extents (Windows only).</summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsContiguityChecker : IContiguityChecker
{
    private const uint FsctlGetRetrievalPointers = 0x00090073;
    private const int ErrorMoreData = 234;

    public int? CountFragments(string path)
    {
        using var handle = File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        long startVcn = 0;
        var buffer = new byte[64 * 1024];
        int extents = 0;
        long lastNextVcnLcnEnd = -1;
        while (true)
        {
            var ok = DeviceIoControl(handle, FsctlGetRetrievalPointers, ref startVcn, sizeof(long),
                buffer, buffer.Length, out _, IntPtr.Zero);
            var err = ok ? 0 : Marshal.GetLastWin32Error();
            if (!ok && err != ErrorMoreData) return extents == 0 && err == 38 /* EOF: empty file */ ? 1 : (extents > 0 ? extents : null);

            int count = BitConverter.ToInt32(buffer, 0);
            long prevVcn = BitConverter.ToInt64(buffer, 8);
            for (int i = 0; i < count; i++)
            {
                long nextVcn = BitConverter.ToInt64(buffer, 16 + i * 16);
                long lcn = BitConverter.ToInt64(buffer, 24 + i * 16);
                // Adjacent extents that continue on disk are still physically contiguous.
                if (lcn != lastNextVcnLcnEnd) extents++;
                lastNextVcnLcnEnd = lcn + (nextVcn - prevVcn);
                prevVcn = nextVcn;
            }
            if (ok) return extents;
            startVcn = prevVcn;
        }
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool DeviceIoControl(SafeFileHandle hDevice, uint ioControlCode, ref long inBuffer, int inBufferSize,
        [Out] byte[] outBuffer, int outBufferSize, out int bytesReturned, IntPtr overlapped);
}

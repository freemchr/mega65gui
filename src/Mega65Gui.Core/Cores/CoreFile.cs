using System.Text;

namespace Mega65Gui.Core.Cores;

/// <summary>Header of a MEGA65 core file (.cor), as written by the official bit2core tool.</summary>
public sealed record CoreFileInfo(
    string CoreName, string CoreVersion, string Target, byte ModelId,
    int EmbeddedFiles, uint CoreSize, uint StoredCrc, uint ComputedCrc, long FileLength)
{
    public bool CrcMatches => StoredCrc == ComputedCrc;
    public bool SizeFits => CoreSize >= CoreFile.HeaderSize && CoreSize <= FileLength;
    public bool IsValid => CrcMatches && SizeFits;
}

/// <summary>
/// Reads and checks .cor files. Layout (4096-byte header): magic "MEGA65BITSTREAM0" at 0, core name
/// at 0x10, version at 0x30, target name at 0x50 (32 bytes each), model id at 0x70, embedded file
/// count at 0x72, core size at 0x80 and CRC32 at 0x84. The CRC covers the first core-size bytes with
/// the CRC field set to F0F0F0F0.
/// </summary>
public static class CoreFile
{
    public const int HeaderSize = 4096;
    public const string Magic = "MEGA65BITSTREAM0";

    /// <summary>Official bit2core target names, with the MEGA65 model each one is for.</summary>
    public static IReadOnlyList<(string Target, string Label)> Targets { get; } =
    [
        ("mega65r6", "MEGA65 R6"), ("mega65r5", "MEGA65 R5 (beta PCB)"), ("mega65r4", "MEGA65 R4"),
        ("mega65r3", "MEGA65 R3 / R3a / DevKit"), ("mega65r2", "MEGA65 R2"),
        ("nexys4ddrwidget", "Nexys4 DDR (widget)"), ("nexys4ddr", "Nexys4 DDR"), ("nexys4", "Nexys4"),
        ("megaphoner1", "MEGAphone R1"), ("wukonga100t", "Wukong A100T"),
    ];

    public static CoreFileInfo Read(string path) => Parse(File.ReadAllBytes(path));

    public static CoreFileInfo Parse(byte[] data)
    {
        if (data.Length < HeaderSize || Encoding.ASCII.GetString(data, 0, 16) != Magic)
            throw new InvalidDataException("This is not a MEGA65 core file (.cor): the MEGA65BITSTREAM0 header is missing.");
        var size = BitConverter.ToUInt32(data, 0x80);
        var stored = BitConverter.ToUInt32(data, 0x84);
        uint computed = 0;
        if (size >= HeaderSize && size <= data.Length)
        {
            var copy = data.AsSpan(0, (int)size).ToArray();
            BitConverter.TryWriteBytes(copy.AsSpan(0x84), 0xF0F0F0F0u);
            computed = Crc32(copy);
        }
        return new CoreFileInfo(Text(data, 0x10), Text(data, 0x30), Text(data, 0x50), data[0x70],
            data[0x72], size, stored, computed, data.Length);
    }

    private static string Text(byte[] data, int offset)
    {
        var end = Array.IndexOf(data, (byte)0, offset, 32);
        return Encoding.ASCII.GetString(data, offset, (end < 0 ? offset + 32 : end) - offset).Trim();
    }

    private static readonly uint[] Table = Enumerable.Range(0, 256).Select(n =>
    {
        var c = (uint)n;
        for (var k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
        return c;
    }).ToArray();

    /// <summary>Standard CRC-32 (as zlib and bit2core).</summary>
    public static uint Crc32(ReadOnlySpan<byte> data)
    {
        var crc = 0xFFFFFFFFu;
        foreach (var b in data) crc = Table[(crc ^ b) & 0xFF] ^ (crc >> 8);
        return ~crc;
    }

    /// <summary>bit2core arguments: target, bitstream, name, version, output.</summary>
    public static List<string> Bit2CoreArgs(string target, string bitPath, string name, string version, string outPath)
    {
        if (!Targets.Any(t => t.Target == target)) throw new ArgumentException($"Unknown target '{target}'.", nameof(target));
        if (string.IsNullOrWhiteSpace(name) || name.Length > 31) throw new ArgumentException("The core name must be 1-31 characters.", nameof(name));
        if (string.IsNullOrWhiteSpace(version) || version.Length > 31) throw new ArgumentException("The version must be 1-31 characters.", nameof(version));
        return [target, bitPath, name, version, outPath];
    }
}

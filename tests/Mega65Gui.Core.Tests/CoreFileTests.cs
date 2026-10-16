using System.Text;
using Mega65Gui.Core.Cores;

namespace Mega65Gui.Core.Tests;

public class CoreFileTests
{
    private static byte[] MakeCore(string name, string version, string target, int size)
    {
        var d = new byte[size + 100];
        Encoding.ASCII.GetBytes(CoreFile.Magic).CopyTo(d, 0);
        Encoding.ASCII.GetBytes(name).CopyTo(d, 0x10);
        Encoding.ASCII.GetBytes(version).CopyTo(d, 0x30);
        Encoding.ASCII.GetBytes(target).CopyTo(d, 0x50);
        d[0x70] = 0x06;
        for (var i = CoreFile.HeaderSize; i < size; i++) d[i] = (byte)(i * 7);
        BitConverter.TryWriteBytes(d.AsSpan(0x80), (uint)size);
        BitConverter.TryWriteBytes(d.AsSpan(0x84), 0xF0F0F0F0u);
        var crc = CoreFile.Crc32(d.AsSpan(0, size));
        BitConverter.TryWriteBytes(d.AsSpan(0x84), crc);
        return d;
    }

    [Fact]
    public void Reads_header_and_verifies_crc()
    {
        var info = CoreFile.Parse(MakeCore("MEGA65", "Release 0.97.2 03b24c6", "mega65r6", 9000));
        Assert.Equal("MEGA65", info.CoreName);
        Assert.Equal("Release 0.97.2 03b24c6", info.CoreVersion);
        Assert.Equal("mega65r6", info.Target);
        Assert.Equal(6, info.ModelId);
        Assert.True(info.IsValid);
    }

    [Fact]
    public void Detects_a_damaged_core()
    {
        var d = MakeCore("MEGA65", "1", "mega65r6", 9000);
        d[5000] ^= 0xFF;
        Assert.False(CoreFile.Parse(d).CrcMatches);
        Assert.Throws<InvalidDataException>(() => CoreFile.Parse(new byte[5000]));
    }

    [Fact]
    public void Crc32_matches_the_standard_check_value() =>
        Assert.Equal(0xCBF43926u, CoreFile.Crc32("123456789"u8));

    [Fact]
    public void Bit2core_arguments_are_checked()
    {
        Assert.Equal(["mega65r6", "a.bit", "MyCore", "1.0", "a.cor"], CoreFile.Bit2CoreArgs("mega65r6", "a.bit", "MyCore", "1.0", "a.cor"));
        Assert.Throws<ArgumentException>(() => CoreFile.Bit2CoreArgs("mega65r9", "a.bit", "x", "1", "a.cor"));
    }
}

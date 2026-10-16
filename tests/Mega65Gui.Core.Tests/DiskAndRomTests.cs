using Mega65Gui.Core.DiskImages;
using Mega65Gui.Core.Roms;

namespace Mega65Gui.Core.Tests;

public class DiskAndRomTests
{
    [Fact]
    public void NewD81HasStandardFreeSpace()
    {
        var d = D81Image.Create("MY DISK", "65");
        Assert.Equal("MY DISK", d.DiskName);
        Assert.Equal("65", d.DiskId);
        Assert.Equal(3160, d.FreeBlocks); // 79 tracks × 40 sectors, as the 1581 reports
        Assert.Empty(d.Entries);
    }

    [Fact]
    public void AddExtractRenameDeleteRoundTrip()
    {
        var d = D81Image.Create("TEST", "01");
        var data = Enumerable.Range(0, 1000).Select(i => (byte)i).ToArray();
        var e = d.Add("game", data);
        Assert.Equal("GAME", e.Name);
        Assert.Equal(4, e.Blocks); // 1000 bytes / 254 per block
        Assert.Equal(3160 - 4, d.FreeBlocks);
        Assert.Equal(data, d.Extract(e));

        d.Rename(e, "renamed");
        Assert.NotNull(d.Find("RENAMED"));
        Assert.Throws<InvalidOperationException>(() => d.Add("renamed", [1]));

        d.Delete(d.Find("RENAMED")!);
        Assert.Empty(d.Entries);
        Assert.Equal(3160, d.FreeBlocks);

        // Survives a save/load cycle.
        d.Add("A", [1, 2, 3]);
        var again = D81Image.Load(d.ToArray());
        Assert.Equal(new byte[] { 1, 2, 3 }, again.Extract(again.Find("A")!));
    }

    [Fact]
    public void DirectoryGrowsBeyondOneSector()
    {
        var d = D81Image.Create("MANY", "01");
        for (var i = 0; i < 20; i++) d.Add($"FILE{i}", [(byte)i]);
        Assert.Equal(20, d.Entries.Count);
        Assert.Equal(new byte[] { 19 }, d.Extract(d.Find("FILE19")!));
    }

    [Fact]
    public void RejectsWrongSize() => Assert.Throws<InvalidDataException>(() => D81Image.Load(new byte[1000]));

    private static byte[] Rom(string version)
    {
        var rom = new byte[RomFile.Size];
        System.Text.Encoding.ASCII.GetBytes(version).CopyTo(rom, 0x16);
        return rom;
    }

    [Fact]
    public void RomSettingsReadAndWriteTheSettingsBlock()
    {
        var rom = Rom("V920413");
        Assert.Equal("V920413", RomFile.ReadVersion(rom));
        var s = new RomSettings((byte)RomSpeed.Mhz40, (byte)'B', 1, 6, 6, 2, 13, 7);
        var patched = s.ApplyTo(rom);
        Assert.Equal(s, RomSettings.Read(patched));
        Assert.Equal(0, rom[RomFile.SettingsOffset + 1]); // original untouched
        Assert.Equal((byte)'B', patched[0x17FF9]);
    }

    [Fact]
    public void RomSettingsNeedASupportedRom()
    {
        Assert.Throws<InvalidDataException>(() => RomSettings.Read(Rom("V920100")));
        Assert.Throws<InvalidDataException>(() => RomSettings.Read(new byte[10]));
    }

    [Fact]
    public void ReadsRomPatchHeader()
    {
        var patch = new byte[300];
        System.Text.Encoding.ASCII.GetBytes("MEGA65ROMPATCH01.00").CopyTo(patch, 0);
        System.Text.Encoding.ASCII.GetBytes("920395.bin").CopyTo(patch, 32);
        System.Text.Encoding.ASCII.GetBytes("920413.bin").CopyTo(patch, 96);
        Assert.Equal("920395.bin", RomPatcher.ReferenceName(patch));
        Assert.Equal("920413.bin", RomPatcher.TargetName(patch));
        Assert.Null(RomPatcher.ReferenceName(new byte[300]));
    }
}

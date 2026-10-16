using Mega65Gui.Core.Device;
using Mega65Gui.Core.Filehost;
using Mega65Gui.Core.Packages;
using Mega65Gui.Core.Update;

namespace Mega65Gui.Core.Tests;

/// <summary>Layout of the real owner-only R6 0.97.2 package (file names only; no ROM data).</summary>
public sealed class RomPackageTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("m65rom").FullName;
    public void Dispose() => Directory.Delete(_root, recursive: true);

    private static readonly string[] RealLayout =
    [
        "Changelog.md", "README.md",
        "beta-pcb/r5-bit-is-the-same-as-r6", "beta-pcb/mega65r5-20250919.07-r-0.97.2-03b24c6.cor",
        "extra/HICKUP.M65", "flasher/mflash.prg",
        "log/mega65r6-20250919.07-r-0.97-03b24c6.log",
        "mega65r6-20250919.07-r-0.97.2-03b24c6.bit", "mega65r6-20250919.07-r-0.97.2-03b24c6.cor",
        "sdcard-files/AUDIOMIX.M65", "sdcard-files/BANNER.M65", "sdcard-files/ETHLOAD.M65", "sdcard-files/FREEZER.M65",
        "sdcard-files/MAKEDISK.M65", "sdcard-files/MEGA65.ROM", "sdcard-files/MEGAINFO.M65", "sdcard-files/MONITOR.M65",
    ];

    private string Build(bool withRom)
    {
        var dir = Path.Combine(_root, withRom ? "rom" : "norom", "mega65r6-release-0.97.2-03b24c6");
        foreach (var f in RealLayout.Where(f => withRom || !f.EndsWith("MEGA65.ROM")))
        {
            var p = Path.Combine(dir, f);
            Directory.CreateDirectory(Path.GetDirectoryName(p)!);
            File.WriteAllText(p, "x");
        }
        return Path.GetDirectoryName(dir)!;
    }

    [Fact]
    public void RecognisesRomPackageLayout()
    {
        var pkg = ReleasePackage.Inspect(Build(withRom: true));
        Assert.True(pkg.HasRom);
        Assert.EndsWith("sdcard-files", pkg.SdCardRoot);
        Assert.Equal("mega65r6-20250919.07-r-0.97.2-03b24c6.cor", Path.GetFileName(UpdateEngine.SelectCore(pkg, HardwareModel.R6)));
        Assert.Equal("mega65r5-20250919.07-r-0.97.2-03b24c6.cor", Path.GetFileName(UpdateEngine.SelectCore(pkg, HardwareModel.R5)));
        Assert.False(ReleasePackage.Inspect(Build(withRom: false)).HasRom);
    }

    [Theory]
    [InlineData("mega65r6-release-0.97.2-03b24c6.7z", true)]
    [InlineData("mega65r6-release-0.97.2-03b24c6-norom.7z", false)]
    public void PackageNameTellsWhetherRomIsIncluded(string name, bool rom)
    {
        var info = ReleaseInfo.TryParse(name)!;
        Assert.Equal(rom, info.IncludesRom);
        Assert.Equal(new Version(0, 97, 2), info.Release);
        Assert.Equal("03b24c6", info.CommitHash);
    }
}

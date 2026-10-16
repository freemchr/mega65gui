using Mega65Gui.Core.Device;
using Mega65Gui.Core.Tools;

namespace Mega65Gui.Core.Tests;

public class DeviceTests
{
    [Fact]
    public void ParsesMonitorOutputInM65CommonFormat()
    {
        var state = SerialMonitorProbe.Parse(
            "MEGA65 Serial Monitor\r\nbuild GIT: release-0.97.2,20250919.07,03b24c6\r\n",
            ":0FFD3629:06\r\n",
            ":00020016:56393230333735\r\n");
        Assert.Equal(HardwareModel.R6, state.Model);
        Assert.Equal(Provenance.ReadFromDevice, state.ModelSource);
        Assert.Equal("03b24c6", state.CoreGitHash);
        Assert.Equal("V920375", state.RomVersion);
    }

    [Fact]
    public void UnknownResponsesStayUnknown()
    {
        var state = SerialMonitorProbe.Parse("", "", "");
        Assert.Equal(HardwareModel.Unknown, state.Model);
        Assert.Equal(Provenance.NotKnown, state.ModelSource);
        Assert.Null(state.CoreBuild);
    }

    [Theory]
    [InlineData(HardwareModel.R3, "mega65r3", true)]
    [InlineData(HardwareModel.R5, "mega65r6", false)]
    [InlineData(HardwareModel.R6, "mega65r6", false)]
    [InlineData(HardwareModel.R4, null, false)]
    public void MapsModelsToReleaseTargets(HardwareModel m, string? target, bool pcFlash)
    {
        Assert.Equal(target, HardwareModels.ReleaseTarget(m));
        Assert.Equal(pcFlash, HardwareModels.SupportsPcFlashing(m));
    }

    [Fact]
    public void BuildsOfficialToolArguments()
    {
        var com = ConnectionTarget.Serial("COM5");
        var eth = ConnectionTarget.Ethernet("fe80::1%eth0");

        Assert.Equal(["-l", "COM5", "-s", "2000000", "-F", "-4", "-r", "game.prg"], Mega65Commands.SendPrgArgs(com, "game.prg", true, true).Args);
        var (tool, args) = Mega65Commands.SendPrgArgs(eth, "game.prg", true, false);
        Assert.Equal("etherload", tool);
        Assert.Equal(["-i", "fe80::1%eth0", "-5", "-r", "game.prg"], args);
        Assert.Equal(["-l", "COM5", "-s", "2000000", "-Sshot.png"], Mega65Commands.ScreenshotArgs(com, "shot.png").Args);
        Assert.Throws<NotSupportedException>(() => Mega65Commands.ScreenshotArgs(eth, "x.png"));
        Assert.Equal(["-F", "-y", "-e", "-i", "fe80::1%eth0", "-c", "dir", "-c", "quit"], Mega65Commands.FtpArgs(eth, "dir").Args);
    }

    [Fact]
    public void ParsesEtherloadDiscovery()
    {
        Assert.Equal(["fe80::4d:65ff:fe36:5%Ethernet 2"],
            Mega65Commands.ParseDiscovery("INFO: searching\nMEGA65 found at fe80::4d:65ff:fe36:5%Ethernet 2\n"));
    }

    [Fact]
    public void ParsesToolVersion()
    {
        Assert.Equal("20261005.14-develo-44c63d2", ToolManager.ParseVersion("m65 - usage\nVersion: 20261005.14-develo-44c63d2\n"));
        Assert.Null(ToolManager.ParseVersion("no version here"));
    }
}

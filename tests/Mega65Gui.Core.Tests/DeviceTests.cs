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

    /// <summary>Exact monitor responses captured from a MEGA65 R6 running core 0.96 (2026-10-16).</summary>
    [Fact]
    public void ParsesRealR6Responses()
    {
        var state = SerialMonitorProbe.Parse(
            "h\r\nMEGA65 Serial Monitor\r\nbuild GIT: release-0.96,20240224.00,3c10488\r\n\r\n.",
            "mffd3629 1\r\n:0FFD3629:06FF3FFFFFFFFFEF058348103C000000\r\n\r\n.",
            "m20016 7\r\n:00020016:56393230333935A98004E304E48D7101\r\n\r\n.");
        Assert.Equal(HardwareModel.R6, state.Model);
        Assert.Equal("release-0.96,20240224.00,3c10488", state.CoreBuild);
        Assert.Equal("3c10488", state.CoreGitHash);
        Assert.Equal("V920395", state.RomVersion);

        // Compared with the 0.97.2 release package, this core needs updating.
        var release = Filehost.ReleaseInfo.TryParse("mega65r6-release-0.97.2-03b24c6-norom.7z")!;
        Assert.Equal(Update.CoreComparison.UpdateAvailable, Update.UpdateEngine.CompareCore(state, release));
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
        Assert.Equal(["-F", "-y", "-e", "-i", "fe80::1%eth0", "-c", "dir", "-c", "exit"], Mega65Commands.FtpArgs(eth, true, "dir").Args);
        // The 1.00 release has no -y option.
        Assert.Equal(["-F", "-l", "COM5", "-s", "2000000", "-c", "dir", "-c", "exit"], Mega65Commands.FtpArgs(com, false, "dir").Args);
    }

    [Fact]
    public void BuildsTransferArguments()
    {
        var com = ConnectionTarget.Serial("COM3");
        var eth = ConnectionTarget.Ethernet("fe80::1%eth0");
        string[] serial = ["-l", "COM3", "-s", "2000000"];

        Assert.Equal([.. serial, "-F", "-4", "-r", "tune.sid"], Mega65Commands.SendSidArgs(com, "tune.sid").Args);
        Assert.Equal([.. serial, "-q", "core.bit"], Mega65Commands.LoadBitstreamArgs(com, "core.bit").Args);
        Assert.Equal([.. serial, "-F", "-k", "HICKUP.M65"], Mega65Commands.LoadHickupArgs(com, "HICKUP.M65").Args);
        Assert.Equal([.. serial, "-F", "-R", "MEGA65.ROM"], Mega65Commands.LoadRomArgs(com, "MEGA65.ROM").Args);
        var (romTool, romArgs) = Mega65Commands.LoadRomArgs(eth, "MEGA65.ROM");
        Assert.Equal("etherload", romTool);
        Assert.Equal(["-i", "fe80::1%eth0", "-R", "MEGA65.ROM"], romArgs);
        Assert.Equal([.. serial, "-n"], Mega65Commands.VideoModeArgs(com, pal: false).Args);
        Assert.Equal(["-i", "fe80::1%eth0", "-5", "--pal"], Mega65Commands.VideoModeArgs(eth, pal: true).Args);
        Assert.Equal([.. serial, "-Z", "1"], Mega65Commands.BootSlotArgs(com, 1).Args);
        Assert.Equal([.. serial, "-T", "-"], Mega65Commands.TypeTextArgs(com).Args);

        Assert.Throws<NotSupportedException>(() => Mega65Commands.SendSidArgs(eth, "x.sid"));
        Assert.Throws<ArgumentOutOfRangeException>(() => Mega65Commands.BootSlotArgs(com, 8));
    }

    [Fact]
    public void PreparesBasicListingForTyping()
    {
        Assert.Equal("10 print \"hello\"\n20 goto 10\n.\n",
            Mega65Commands.PrepareBasicListing("10 PRINT \"HELLO\"\r\n\r\n20 GOTO 10   \r\n.\r\n"));
    }

    [Fact]
    public void DetectsYesOptionFromHelpText()
    {
        Assert.True(Mega65Commands.HelpListsYesOption("  -c - execute command\n  -y - don't ask for confirmation before executing commands.\n"));
        Assert.False(Mega65Commands.HelpListsYesOption("  -c - execute mega65_ftp cli command\n       Note: if you don't specify 'exit'\n"));
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

    [Fact]
    public void Explains_missing_WinUSB_driver_for_bitstream_loading()
    {
        // Real m65 output on Windows without WinUSB on interface 0 (R6, 2026-10-16).
        const string output = "NOTE please check USB driver for Serial Converter A / Interface 0: needs to be WinUSB!\n" +
                              "CRIT you supplied a bitstream for a different MEGA65 model than is connected\n";
        Assert.Contains("WinUSB", ToolFailureHints.Explain(output));
        Assert.Null(ToolFailureHints.Explain("NOTE resetting MEGA65"));
    }

    [Fact]
    public void Ethernet_hint_names_the_adapter_without_IPv6()
    {
        Assert.Contains("\"Ethernet\"", NetworkCheck.NothingFoundMessage(["Ethernet"]));
        Assert.Contains("DIP switch 2", NetworkCheck.NothingFoundMessage([]));
        Assert.True(NetworkCheck.IsVirtual("VMware Virtual Ethernet Adapter for VMnet8"));
        Assert.False(NetworkCheck.IsVirtual("Realtek USB 2.5GbE Family Controller"));
    }
}

using Mega65Gui.Core.Device;
using Mega65Gui.Core.Update;

namespace Mega65Gui.Core.Tests;

public class PortAndPreflightTests
{
    private static SerialPortInfo Bluetooth(string port) =>
        SerialPorts.Classify(port, @"BTHENUM\{00001101-0000-1000-8000-00805F9B34FB}_LOCALMFG&0000\7&2A0B2C4&0&000000000000_00000000", $"Standard Serial over Bluetooth link ({port})");

    private static SerialPortInfo Te0790(string port, char iface) =>
        SerialPorts.Classify(port, $@"FTDIBUS\VID_0403+PID_6010+2516330596481{iface}\0000", $"USB Serial Port ({port})");

    [Fact]
    public void RecognisesTheMega65AdapterAndBluetooth()
    {
        var adapter = Te0790("COM5", 'B');
        Assert.Equal(PortKind.Mega65Adapter, adapter.Kind);
        Assert.True(adapter.SecondInterface);
        Assert.Equal("COM5 – MEGA65 USB/JTAG", adapter.Label);

        var bt = Bluetooth("COM3");
        Assert.Equal(PortKind.NotMega65, bt.Kind);
        Assert.False(bt.CouldBeMega65);
        Assert.Equal("COM3 – Bluetooth (not a MEGA65)", bt.Label);

        Assert.Equal(PortKind.Ftdi, SerialPorts.Classify("COM7", @"FTDIBUS\VID_0403+PID_6001+A50285BIA\0000", "USB Serial Port (COM7)").Kind);
        Assert.Equal(PortKind.NotMega65, SerialPorts.Classify("COM1", @"ACPI\PNP0501\0", "Communications Port (COM1)").Kind);
        // Windows' own driver names the FT2232H's interfaces MI_00 and MI_01.
        Assert.True(SerialPorts.Classify("COM9", @"USB\VID_0403&PID_6010&MI_01\7&1A2B3C&0&0001", "USB Serial Device (COM9)").SecondInterface);
    }

    [Fact]
    public void PrefersTheAdapterOverABluetoothPortWithAHigherNumber()
    {
        // The case behind the "why does it want a COM port" report: the last port used to be picked.
        Assert.Equal("COM4", SerialPorts.Preferred([Te0790("COM4", 'B'), Bluetooth("COM8")]));
        // Both FT2232H interfaces as COM ports: the second carries the MEGA65's serial monitor.
        Assert.Equal("COM6", SerialPorts.Preferred([Te0790("COM5", 'A'), Te0790("COM6", 'B')]));
    }

    [Fact]
    public void PicksNothingWhenNoPortCouldBeTheMega65()
    {
        Assert.Null(SerialPorts.Preferred([Bluetooth("COM3"), Bluetooth("COM4")]));
        Assert.Null(SerialPorts.Preferred([]));
    }

    [Fact]
    public void WithoutDeviceDetailsFallsBackToTheLastPort()
    {
        Assert.Equal("COM4", SerialPorts.Preferred([new("COM3", null, PortKind.Unknown), new("COM4", null, PortKind.Unknown)]));
        // One unknown USB adapter next to Bluetooth: that one.
        Assert.Equal("COM10", SerialPorts.Preferred([Bluetooth("COM3"), SerialPorts.Classify("COM10", @"USB\VID_10C4&PID_EA60\0001", "Silicon Labs CP210x (COM10)")]));
    }

    private static UpdatePreflight Card(bool inDevice, bool model, bool connected = false, bool usb = true, bool tools = true, bool folder = true) =>
        new(inDevice, model, connected, connected && usb, tools, folder);

    [Fact]
    public void CardInAPcReaderWithAutoAndNoMega65AsksForTheModelNotAPort()
    {
        var r = Card(inDevice: false, model: false).Check();
        Assert.Equal(PreflightAction.Stop, r.Action);
        Assert.Equal(UpdatePreflight.ChooseModel, r.Message);
        Assert.DoesNotContain("COM", r.Message);
    }

    [Theory]
    [InlineData(false, true, false, PreflightAction.Go)]      // card in reader, model picked, nothing connected
    [InlineData(false, false, true, PreflightAction.Go)]      // card in reader, Auto, MEGA65 on USB reads the model
    [InlineData(true, false, false, PreflightAction.Connect)] // card in the MEGA65: connect first
    [InlineData(true, true, false, PreflightAction.Connect)]
    [InlineData(true, false, true, PreflightAction.Go)]
    public void DecidesWhatTheUpdateNeeds(bool inDevice, bool model, bool connected, PreflightAction expected) =>
        Assert.Equal(expected, Card(inDevice, model, connected).Check().Action);

    [Fact]
    public void StopsWithAClearReason()
    {
        Assert.Equal("Choose the SD card folder first.", Card(false, true, folder: false).Check().Message);
        Assert.Contains("device tools", Card(true, true, connected: true, tools: false).Check().Message);
        // Ethernet cannot read the model.
        Assert.Contains("Ethernet", Card(true, false, connected: true, usb: false).Check().Message);
        Assert.Equal(UpdatePreflight.ChooseModel, Card(false, false, connected: true, usb: false).Check().Message);
    }
}

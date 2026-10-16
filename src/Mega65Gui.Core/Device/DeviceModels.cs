namespace Mega65Gui.Core.Device;

public enum ConnectionKind { None, Serial, Ethernet }

/// <summary>How a connection was established. Ethernet needs core 0.96+ and SHIFT+£ pressed on the MEGA65.</summary>
public sealed record ConnectionTarget(ConnectionKind Kind, string Address)
{
    public static ConnectionTarget Serial(string comPort) => new(ConnectionKind.Serial, comPort);
    public static ConnectionTarget Ethernet(string ipAddress) => new(ConnectionKind.Ethernet, ipAddress);
    public override string ToString() => Kind == ConnectionKind.Serial ? $"USB/JTAG ({Address})" : $"Ethernet ({Address})";
}

public enum HardwareModel { Unknown, R1, R2, R3, R4, R5, R6, Nexys4, Nexys4DdrWidget, Wukong, MegaPhone, Simulation }

public static class HardwareModels
{
    /// <summary>Model byte read from $FFD3629 (mega65-tools: mega65_ftp.c model table).</summary>
    public static HardwareModel FromModelByte(byte b) => b switch
    {
        0x01 => HardwareModel.R1,
        0x02 => HardwareModel.R2,
        0x03 => HardwareModel.R3,
        0x04 => HardwareModel.R4,
        0x05 => HardwareModel.R5,
        0x06 => HardwareModel.R6,
        0x21 or 0x22 => HardwareModel.MegaPhone,
        0x40 or 0x41 => HardwareModel.Nexys4,
        0x42 => HardwareModel.Nexys4DdrWidget,
        0xFD => HardwareModel.Wukong,
        0xFE => HardwareModel.Simulation,
        _ => HardwareModel.Unknown,
    };

    /// <summary>
    /// Filehost release-package target for a model. The mega65r6 package also contains the
    /// mega65r5 core (per its Filehost description); no public R4 package exists.
    /// </summary>
    public static string? ReleaseTarget(HardwareModel m) => m switch
    {
        HardwareModel.R2 => "mega65r2",
        HardwareModel.R3 => "mega65r3",
        HardwareModel.R5 or HardwareModel.R6 => "mega65r6",
        HardwareModel.Nexys4DdrWidget => "nexys4ddr-widget",
        _ => null,
    };

    /// <summary>Core file name prefix inside a release package for this model.</summary>
    public static string? CorePrefix(HardwareModel m) => m switch
    {
        HardwareModel.R2 => "mega65r2",
        HardwareModel.R3 => "mega65r3",
        HardwareModel.R5 => "mega65r5",
        HardwareModel.R6 => "mega65r6",
        HardwareModel.Nexys4DdrWidget => "nexys4ddr-widget",
        _ => null,
    };

    /// <summary>
    /// The official tools can only write flash slots on R2/R3 dev kits, and then only through
    /// an installed Vivado. R4–R6 must be flashed on the machine with MEGAFLASH.
    /// </summary>
    public static bool SupportsPcFlashing(HardwareModel m) => m is HardwareModel.R2 or HardwareModel.R3;
}

/// <summary>Where a value came from, so the UI never presents a guess as a fact.</summary>
public enum Provenance { NotKnown, UserSupplied, ReadFromDevice }

public sealed record DeviceState(
    ConnectionTarget? Connection,
    HardwareModel Model,
    Provenance ModelSource,
    string? CoreBuild,
    string? CoreGitHash,
    string? RomVersion,
    string? RawMonitorText)
{
    public static DeviceState Unknown { get; } = new(null, HardwareModel.Unknown, Provenance.NotKnown, null, null, null, null);
}

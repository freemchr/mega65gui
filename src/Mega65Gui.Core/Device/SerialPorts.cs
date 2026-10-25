using System.IO.Ports;
using System.Text.RegularExpressions;

namespace Mega65Gui.Core.Device;

/// <summary>What a COM port is, as far as Windows says.</summary>
public enum PortKind
{
    /// <summary>An FTDI FT2232H (USB 0403:6010), the chip on the MEGA65's TE0790 USB/JTAG adapter.</summary>
    Mega65Adapter,
    /// <summary>Another FTDI USB serial chip: could be a MEGA65 with a different adapter.</summary>
    Ftdi,
    /// <summary>Windows gave no details, or another USB serial adapter.</summary>
    Unknown,
    /// <summary>Bluetooth, a modem or a built-in port: never a MEGA65.</summary>
    NotMega65,
}

/// <param name="Name">"COM3".</param>
/// <param name="Description">Windows' name for the device, e.g. "USB Serial Port"; null when unknown.</param>
public sealed record SerialPortInfo(string Name, string? Description, PortKind Kind, bool SecondInterface = false)
{
    /// <summary>What the port lists show, e.g. "COM5 – MEGA65 USB/JTAG" or "COM3 – Bluetooth".</summary>
    public string Label => Kind switch
    {
        PortKind.Mega65Adapter => $"{Name} – MEGA65 USB/JTAG",
        PortKind.Ftdi => $"{Name} – FTDI USB serial",
        PortKind.NotMega65 => $"{Name} – {Short(Description)} (not a MEGA65)",
        _ => Description is null ? Name : $"{Name} – {Short(Description)}",
    };

    /// <summary>A port the MEGA65 could be on.</summary>
    public bool CouldBeMega65 => Kind != PortKind.NotMega65;

    private static string Short(string? description) =>
        description is null ? "other device"
        : description.Contains("Bluetooth", StringComparison.OrdinalIgnoreCase) ? "Bluetooth"
        : description.Length <= 32 ? description : description[..31] + "…";
}

/// <summary>
/// Lists COM ports with what Windows knows about them, so the MEGA65's USB/JTAG adapter can be
/// told apart from Bluetooth and modem ports, which many PCs have.
/// </summary>
public static partial class SerialPorts
{
    [GeneratedRegex(@"\((COM\d+)\)", RegexOptions.IgnoreCase)]
    private static partial Regex ComInName();

    /// <summary>Every COM port, ordered by name. Without device details (not Windows, or WMI failed) all are Unknown.</summary>
    public static IReadOnlyList<SerialPortInfo> List()
    {
        var names = SerialPort.GetPortNames().Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase).ToList();
        var devices = ReadDevices();
        return names.Select(n => devices.TryGetValue(n, out var d) ? Classify(n, d.Id, d.Name) : new SerialPortInfo(n, null, PortKind.Unknown)).ToList();
    }

    /// <summary>
    /// Classifies a port from its Plug and Play device ID and name, e.g.
    /// "FTDIBUS\VID_0403+PID_6010+210512345678B\0000" and "USB Serial Port (COM5)".
    /// </summary>
    public static SerialPortInfo Classify(string port, string? deviceId, string? deviceName)
    {
        var id = (deviceId ?? "").ToUpperInvariant();
        var description = deviceName is null ? null : ComInName().Replace(deviceName, "").Trim();
        if (string.IsNullOrEmpty(description)) description = null;

        var ftdi = id.Contains("VID_0403");
        if (ftdi && id.Contains("PID_6010"))
            // The FT2232H has two interfaces: FTDI's driver ends the serial number with A or B,
            // Windows' own USB driver names them MI_00 and MI_01. The MEGA65's serial is on the second.
            return new(port, description, PortKind.Mega65Adapter, id.Contains("B\\") || id.Contains("MI_01"));
        if (ftdi) return new(port, description, PortKind.Ftdi);

        var name = description ?? "";
        if (id.StartsWith("BTHENUM", StringComparison.Ordinal) || id.StartsWith("BTHMODEM", StringComparison.Ordinal)
            || id.StartsWith("ACPI", StringComparison.Ordinal) || id.StartsWith("PCI", StringComparison.Ordinal)
            || name.Contains("Bluetooth", StringComparison.OrdinalIgnoreCase) || name.Contains("Modem", StringComparison.OrdinalIgnoreCase))
            return new(port, description, PortKind.NotMega65);
        return new(port, description, PortKind.Unknown);
    }

    /// <summary>
    /// The port to pre-select: the MEGA65 adapter's serial interface, then any FTDI port. When
    /// Windows gave no details, the last port (what the official tools pick). Null when no port
    /// could be the MEGA65, or several unknown ones could.
    /// </summary>
    public static string? Preferred(IReadOnlyList<SerialPortInfo> ports)
    {
        var adapter = ports.Where(p => p.Kind == PortKind.Mega65Adapter).ToList();
        if (adapter.Count > 0) return (adapter.LastOrDefault(p => p.SecondInterface) ?? adapter[^1]).Name;
        if (ports.LastOrDefault(p => p.Kind == PortKind.Ftdi) is { } ftdi) return ftdi.Name;
        if (ports.Count > 0 && ports.All(p => p.Kind == PortKind.Unknown && p.Description is null)) return ports[^1].Name;
        var unknown = ports.Where(p => p.Kind == PortKind.Unknown).ToList();
        return unknown.Count == 1 ? unknown[0].Name : null;
    }

    private sealed record PnpDevice(string? Id, string? Name);

    /// <summary>COM port → its Plug and Play device, from WMI's Win32_PnPEntity.</summary>
    private static Dictionary<string, PnpDevice> ReadDevices()
    {
        var map = new Dictionary<string, PnpDevice>(StringComparer.OrdinalIgnoreCase);
        if (!OperatingSystem.IsWindows()) return map;
        try
        {
            using var search = new System.Management.ManagementObjectSearcher(
                "SELECT Name, PNPDeviceID FROM Win32_PnPEntity WHERE Name LIKE '%(COM%'");
            foreach (var o in search.Get())
            {
                using (o)
                {
                    var name = o["Name"] as string;
                    if (name is null || ComInName().Match(name) is not { Success: true } m) continue;
                    map[m.Groups[1].Value] = new PnpDevice(o["PNPDeviceID"] as string, name);
                }
            }
        }
        catch (Exception e) when (e is System.Management.ManagementException or System.Runtime.InteropServices.COMException or UnauthorizedAccessException or TypeInitializationException)
        {
            // No details: the ports are listed as Unknown, as before.
        }
        return map;
    }
}

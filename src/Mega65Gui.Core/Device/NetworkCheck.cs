using System.Net.NetworkInformation;

namespace Mega65Gui.Core.Device;

/// <summary>PC-side checks for Ethernet connections. etherload only talks to the MEGA65 over IPv6 link-local.</summary>
public static class NetworkCheck
{
    /// <summary>Names of physical wired adapters that are up but have IPv6 turned off.</summary>
    public static IReadOnlyList<string> WiredAdaptersWithoutIpv6() =>
        NetworkInterface.GetAllNetworkInterfaces()
            .Where(n => n.OperationalStatus == OperationalStatus.Up
                        && n.NetworkInterfaceType is NetworkInterfaceType.Ethernet or NetworkInterfaceType.GigabitEthernet or NetworkInterfaceType.FastEthernetT
                        && !IsVirtual(n.Description)
                        && !n.Supports(NetworkInterfaceComponent.IPv6))
            .Select(n => n.Name)
            .ToList();

    public static bool IsVirtual(string description) =>
        new[] { "virtual", "vmware", "hyper-v", "virtualbox", "vpn", "tap-", "wan miniport" }
            .Any(v => description.Contains(v, StringComparison.OrdinalIgnoreCase));

    /// <summary>Explains why Ethernet discovery found nothing, starting with causes on this PC.</summary>
    public static string NothingFoundMessage(IReadOnlyList<string> adaptersWithoutIpv6) =>
        adaptersWithoutIpv6.Count > 0
            ? $"No MEGA65 found. IPv6 is turned off on this PC's \"{adaptersWithoutIpv6[0]}\" network adapter, and the MEGA65 Ethernet tools only work over IPv6. " +
              "Turn it on in the adapter's properties (tick \"Internet Protocol Version 6\"), then press Find again."
            : "No MEGA65 found. On the MEGA65, DIP switch 2 must be ON and the power LED must blink green/yellow after SHIFT+£ (core 0.96 or later). " +
              "If it is blinking, allow etherload through Windows Firewall (UDP port 4510).";
}

namespace Mega65Gui.Core.Device;

public interface IDeviceProbe
{
    Task<DeviceState> ProbeAsync(ConnectionTarget target, CancellationToken ct = default);
}

/// <summary>Serial connections are probed through the monitor; Ethernet exposes no read-back with the official tools.</summary>
public sealed class DeviceProbe : IDeviceProbe
{
    public async Task<DeviceState> ProbeAsync(ConnectionTarget target, CancellationToken ct = default) => target.Kind switch
    {
        ConnectionKind.Serial => await new SerialMonitorProbe().ProbeAsync(target.Address, ct),
        _ => DeviceState.Unknown with { Connection = target },
    };
}

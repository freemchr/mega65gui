namespace Mega65Gui.Core.Device;

/// <summary>Thrown when a device operation is started while another one still owns the MEGA65.</summary>
public sealed class DeviceBusyException(string running, string requested)
    : InvalidOperationException($"The MEGA65 is busy ({running}). Wait for it to finish before you start {requested}.")
{
    public string Running { get; } = running;
}

/// <summary>
/// Lets only one operation use the MEGA65 at a time. Two tools on one serial port, or a
/// bitstream load during an SD-card upload, would interfere with each other, so the second
/// caller is refused at once rather than queued.
/// </summary>
public sealed class DeviceOperationGate
{
    private readonly Lock _lock = new();
    private string? _current;

    /// <summary>The operation in progress, or null.</summary>
    public string? Current { get { lock (_lock) return _current; } }

    /// <summary>Raised (on the caller's thread) when an operation starts or ends.</summary>
    public event Action? Changed;

    /// <summary>Claims the MEGA65 for <paramref name="operation"/>; dispose the result to release it.</summary>
    public IDisposable Enter(string operation)
    {
        lock (_lock)
        {
            if (_current is not null) throw new DeviceBusyException(_current, operation.ToLowerInvariant());
            _current = operation;
        }
        Changed?.Invoke();
        return new Claim(this);
    }

    private void Exit()
    {
        lock (_lock) _current = null;
        Changed?.Invoke();
    }

    private sealed class Claim(DeviceOperationGate gate) : IDisposable
    {
        private int _disposed;
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0) gate.Exit();
        }
    }
}

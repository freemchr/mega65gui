using System.IO.Ports;
using System.Text;

namespace Mega65Gui.Core.Device;

public enum SpecialKey { Return, InstDel, Left, Right, Up, Down, Home, Clr, RunStop, Space, F1, F2, F3, F4, F5, F6, F7, F8 }

/// <summary>
/// MEGA65 keyboard-matrix positions for the virtual keyboard register ($FFD3615). These are
/// hardware facts (the C65/MEGA65 matrix layout), as also used by mega65-tools' m65 -t.
/// </summary>
public static class KeyMatrix
{
    public const byte None = 0x7F, LeftShift = 0x0F;

    private static readonly Dictionary<char, byte> Plain = new()
    {
        ['3'] = 0x08, ['w'] = 0x09, ['a'] = 0x0A, ['4'] = 0x0B, ['z'] = 0x0C, ['s'] = 0x0D, ['e'] = 0x0E,
        ['5'] = 0x10, ['r'] = 0x11, ['d'] = 0x12, ['6'] = 0x13, ['c'] = 0x14, ['f'] = 0x15, ['t'] = 0x16, ['x'] = 0x17,
        ['7'] = 0x18, ['y'] = 0x19, ['g'] = 0x1A, ['8'] = 0x1B, ['b'] = 0x1C, ['h'] = 0x1D, ['u'] = 0x1E, ['v'] = 0x1F,
        ['9'] = 0x20, ['i'] = 0x21, ['j'] = 0x22, ['0'] = 0x23, ['m'] = 0x24, ['k'] = 0x25, ['o'] = 0x26, ['n'] = 0x27,
        ['+'] = 0x28, ['p'] = 0x29, ['l'] = 0x2A, ['-'] = 0x2B, ['.'] = 0x2C, [':'] = 0x2D, ['@'] = 0x2E, [','] = 0x2F,
        ['£'] = 0x30, ['*'] = 0x31, [';'] = 0x32, ['='] = 0x35, ['/'] = 0x37, ['1'] = 0x38, ['_'] = 0x39, ['2'] = 0x3B,
        [' '] = 0x3C, ['q'] = 0x3E,
    };

    /// <summary>Characters typed with left shift on another key.</summary>
    private static readonly Dictionary<char, char> Shifted = new()
    {
        ['!'] = '1', ['"'] = '2', ['#'] = '3', ['$'] = '4', ['%'] = '5', ['('] = '8', [')'] = '9', ['?'] = '/', ['<'] = ',', ['>'] = '.',
    };

    /// <summary>Matrix code and modifier for a typed character, or null if the MEGA65 has no such key.</summary>
    public static (byte Key, byte Modifier)? ForChar(char c)
    {
        if (c is >= 'A' and <= 'Z') return Plain.TryGetValue(char.ToLowerInvariant(c), out var k) ? (k, LeftShift) : null;
        if (Plain.TryGetValue(c, out var p)) return (p, None);
        if (Shifted.TryGetValue(c, out var basis) && Plain.TryGetValue(basis, out var b)) return (b, LeftShift);
        return null;
    }

    public static (byte Key, byte Modifier) For(SpecialKey key) => key switch
    {
        SpecialKey.Return => (0x01, None),
        SpecialKey.InstDel => (0x00, None),
        SpecialKey.Right => (0x02, None),
        SpecialKey.Left => (0x02, LeftShift),
        SpecialKey.Down => (0x07, None),
        SpecialKey.Up => (0x07, LeftShift),
        SpecialKey.Home => (0x33, None),
        SpecialKey.Clr => (0x33, LeftShift),
        SpecialKey.RunStop => (0x3F, None),
        SpecialKey.Space => (0x3C, None),
        SpecialKey.F1 => (0x04, None), SpecialKey.F2 => (0x04, LeftShift),
        SpecialKey.F3 => (0x05, None), SpecialKey.F4 => (0x05, LeftShift),
        SpecialKey.F5 => (0x06, None), SpecialKey.F6 => (0x06, LeftShift),
        SpecialKey.F7 => (0x03, None), SpecialKey.F8 => (0x03, LeftShift),
        _ => throw new ArgumentOutOfRangeException(nameof(key)),
    };

    /// <summary>
    /// Monitor commands to press and release a key. With a modifier, the modifier is pressed on its
    /// own first: the MEGA65's hardware keyboard scanner does not reliably see Shift otherwise
    /// (the same sequence mega65-tools uses).
    /// </summary>
    public static IReadOnlyList<string> Commands(byte key, byte modifier) => modifier == None
        ? [$"sffd3615 {key:x2} 7f\r", "sffd3615 7f 7f 7f\r"]
        : [$"sffd3615 {modifier:x2} 7f\r", $"sffd3615 {modifier:x2} {key:x2}\r", "sffd3615 7f 7f 7f\r"];
}

/// <summary>
/// An open connection to the MEGA65 serial monitor (2,000,000 baud) for the terminal and the
/// remote keyboard. It holds the port exclusively while open.
/// </summary>
public sealed class MonitorConnection : IDisposable
{
    private readonly SerialPort _port;
    private readonly SemaphoreSlim _write = new(1, 1);

    public event Action<string>? Received;

    private MonitorConnection(SerialPort port)
    {
        _port = port;
        _port.DataReceived += (_, _) =>
        {
            try { if (_port.IsOpen && _port.ReadExisting() is { Length: > 0 } s) Received?.Invoke(s); }
            catch (Exception e) when (e is IOException or InvalidOperationException) { }
        };
    }

    public static MonitorConnection Open(string comPort)
    {
        var port = new SerialPort(comPort, SerialMonitorProbe.BaudRate, Parity.None, 8, StopBits.One)
        {
            Handshake = Handshake.None, DtrEnable = true, RtsEnable = true, Encoding = Encoding.Latin1, WriteTimeout = 1000,
        };
        port.Open();
        return new MonitorConnection(port);
    }

    /// <summary>Sends a monitor command one character at a time, as mega65-tools does on Windows.</summary>
    public async Task SendAsync(string text, CancellationToken ct = default)
    {
        await _write.WaitAsync(ct);
        try
        {
            foreach (var c in text)
            {
                _port.Write(c.ToString());
                await Task.Delay(1, ct);
            }
        }
        finally { _write.Release(); }
    }

    public Task SendCommandAsync(string command, CancellationToken ct = default) => SendAsync(command.TrimEnd('\r', '\n') + "\r", ct);

    /// <summary>
    /// Sends a command and returns its reply, up to the monitor's "." prompt.
    /// Used for scripted reads such as memory dumps; the reply is also raised through Received.
    /// </summary>
    public async Task<string> QueryAsync(string command, TimeSpan timeout, CancellationToken ct = default)
    {
        var reply = new StringBuilder();
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void OnReceived(string s)
        {
            lock (reply)
            {
                reply.Append(s);
                if (EndsWithPrompt(reply)) done.TrySetResult();
            }
        }
        Received += OnReceived;
        try
        {
            await SendCommandAsync(command, ct);
            await done.Task.WaitAsync(timeout, ct);
            lock (reply) return reply.ToString();
        }
        finally { Received -= OnReceived; }
    }

    /// <summary>True when the text ends with the monitor prompt: a "." on a line of its own.</summary>
    public static bool EndsWithPrompt(StringBuilder text)
    {
        var s = text.ToString().TrimEnd(' ');
        return s.EndsWith("\n.", StringComparison.Ordinal) || s.EndsWith("\r.", StringComparison.Ordinal);
    }

    public async Task PressAsync(byte key, byte modifier, CancellationToken ct = default)
    {
        // Each step needs a keyboard scan to see it before the next.
        foreach (var command in KeyMatrix.Commands(key, modifier))
        {
            await SendAsync(command, ct);
            await Task.Delay(25, ct);
        }
    }

    public void Dispose()
    {
        try { if (_port.IsOpen) _port.Close(); } catch (IOException) { }
        _port.Dispose();
    }
}

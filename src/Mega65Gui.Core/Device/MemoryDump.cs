using System.Globalization;

namespace Mega65Gui.Core.Device;

/// <summary>
/// Reads MEGA65 memory through the serial monitor ("M" shows 256 bytes as 16 lines of
/// ":AAAAAAAA:" plus 32 hex digits). m65 --memsave is not used: on Windows it writes the file in
/// text mode, so every $0A byte gains a $0D in front of it (seen on an R6, 2026-10-16).
/// </summary>
public static class MemoryDump
{
    public const int BytesPerCommand = 256;
    /// <summary>Highest address the monitor accepts (28-bit).</summary>
    public const uint MaxAddress = 0x0FFFFFFF;

    /// <summary>Copies the bytes in monitor dump lines into <paramref name="buffer"/>, which starts at <paramref name="start"/>.</summary>
    /// <returns>The number of bytes placed.</returns>
    public static int Parse(string monitorText, uint start, byte[] buffer)
    {
        var placed = 0;
        foreach (var raw in monitorText.Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length < 11 || line[0] != ':' || line[9] != ':') continue;
            if (!uint.TryParse(line.AsSpan(1, 8), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var addr)) continue;
            var hex = line[10..];
            for (var i = 0; i + 1 < hex.Length; i += 2)
            {
                if (!byte.TryParse(hex.AsSpan(i, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var b)) break;
                var at = (long)addr + i / 2 - start;
                if (at < 0 || at >= buffer.Length) continue;
                buffer[at] = b;
                placed++;
            }
        }
        return placed;
    }

    /// <summary>Parses a hex address as typed by a user: "2001", "$2001" or "0x2001".</summary>
    public static bool TryParseAddress(string text, out uint address)
    {
        var t = text.Trim();
        if (t.StartsWith('$')) t = t[1..];
        else if (t.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) t = t[2..];
        return uint.TryParse(t, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out address) && address <= MaxAddress;
    }

    /// <summary>Reads <paramref name="length"/> bytes from <paramref name="start"/>, 256 bytes per monitor command.</summary>
    public static async Task<byte[]> ReadAsync(MonitorConnection monitor, uint start, int length, IProgress<double>? progress = null, CancellationToken ct = default)
    {
        if (length <= 0 || start + (ulong)length - 1 > MaxAddress) throw new ArgumentOutOfRangeException(nameof(length));
        var buffer = new byte[length];
        for (var offset = 0; offset < length; offset += BytesPerCommand)
        {
            var addr = start + (uint)offset;
            var reply = await monitor.QueryAsync($"M{addr:X}", TimeSpan.FromSeconds(5), ct);
            var want = Math.Min(BytesPerCommand, length - offset);
            var chunk = new byte[want];
            if (Parse(reply, addr, chunk) < want)
                throw new IOException($"The MEGA65 did not return memory at ${addr:X}. Make sure it is running normally.");
            chunk.CopyTo(buffer, offset);
            progress?.Report((double)(offset + want) / length);
        }
        return buffer;
    }
}

using System.Buffers.Binary;
using System.Text;
using System.Text.Json.Serialization;

namespace Mega65Gui.Core.Cores;

/// <summary>Header of a Xilinx .bit bitstream as written by Vivado, plus the FPGA IDCODE found in its configuration data.</summary>
public sealed record BitstreamInfo(
    string Design, string Part, string Date, string Time, int DataOffset, int DataLength, long FileLength, bool HasSyncWord, uint? IdCode)
{
    [JsonIgnore] public bool LengthFits => DataOffset + (long)DataLength <= FileLength;
    [JsonIgnore] public bool IsValid => HasSyncWord && LengthFits;

    /// <summary>The FPGA the bitstream configures, from its IDCODE (the part name in the header is only a label).</summary>
    [JsonIgnore] public string? Fpga => IdCode is { } id ? BitstreamFile.FpgaName(id) : null;
}

/// <summary>
/// Reads .bit files. Layout: a 2-byte length and 9-byte preamble, the field 0x0001, then tagged
/// fields 'a' (design name), 'b' (part), 'c' (date), 'd' (time) each with a 16-bit big-endian
/// length, and 'e' with a 32-bit big-endian length followed by the configuration data. The
/// configuration data starts with padding and the 7-series sync word AA995566; a type-1 write to
/// the IDCODE register (30018001) carries the device ID the FPGA checks before configuring.
/// </summary>
public static class BitstreamFile
{
    private static readonly byte[] Preamble = [0x00, 0x09, 0x0F, 0xF0, 0x0F, 0xF0, 0x0F, 0xF0, 0x0F, 0xF0, 0x00, 0x00, 0x01];
    private const uint SyncWord = 0xAA995566;
    private const uint WriteIdCode = 0x30018001;

    /// <summary>
    /// 7-series IDCODEs without the 4-bit silicon revision (UG470, table 1-1). MEGA65 R1 and
    /// R3–R6 use an XC7A200T (IDCODE x3636093, as m65 maps it); the Nexys4 DDR uses an XC7A100T.
    /// </summary>
    public static IReadOnlyDictionary<uint, string> KnownFpgas { get; } = new Dictionary<uint, string>
    {
        [0x0362D093] = "XC7A35T",
        [0x03632093] = "XC7A75T",
        [0x03631093] = "XC7A100T",
        [0x03636093] = "XC7A200T",
    };

    public static string? FpgaName(uint idCode) => KnownFpgas.TryGetValue(idCode & 0x0FFFFFFF, out var n) ? n : null;

    public static BitstreamInfo Read(string path) => Parse(File.ReadAllBytes(path));

    public static BitstreamInfo Parse(byte[] data)
    {
        if (data.Length < Preamble.Length + 3 || !data.AsSpan(0, Preamble.Length).SequenceEqual(Preamble))
            throw new InvalidDataException("This is not a Xilinx bitstream (.bit): the file header is missing.");

        var pos = Preamble.Length;
        string design = "", part = "", date = "", time = "";
        while (true)
        {
            if (pos >= data.Length) throw new InvalidDataException("The bitstream header ends before its configuration data.");
            var key = (char)data[pos++];
            if (key == 'e')
            {
                if (pos + 4 > data.Length) throw new InvalidDataException("The bitstream header is truncated.");
                var length = BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(pos));
                pos += 4;
                if (length > int.MaxValue) throw new InvalidDataException("The bitstream data length is not plausible.");
                var (sync, id) = ScanConfiguration(data, pos, (int)Math.Min(length, (uint)(data.Length - pos)));
                return new BitstreamInfo(design, part, date, time, pos, (int)length, data.Length, sync, id);
            }
            if (key is not ('a' or 'b' or 'c' or 'd')) throw new InvalidDataException($"Unexpected field '{key}' in the bitstream header.");
            if (pos + 2 > data.Length) throw new InvalidDataException("The bitstream header is truncated.");
            var len = BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(pos));
            pos += 2;
            if (pos + len > data.Length) throw new InvalidDataException("The bitstream header is truncated.");
            var text = Encoding.ASCII.GetString(data, pos, len).TrimEnd('\0');
            pos += len;
            switch (key)
            {
                case 'a': design = text; break;
                case 'b': part = text; break;
                case 'c': date = text; break;
                default: time = text; break;
            }
        }
    }

    /// <summary>Finds the sync word, then the IDCODE write that follows it, within the first part of the data.</summary>
    private static (bool Sync, uint? IdCode) ScanConfiguration(byte[] data, int start, int length)
    {
        var end = start + Math.Min(length, 4096);
        var i = start;
        for (; i + 4 <= end; i++)
            if (BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(i)) == SyncWord) break;
        if (i + 4 > end) return (false, null);
        // After sync the stream is word aligned.
        for (var j = i + 4; j + 8 <= end; j += 4)
            if (BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(j)) == WriteIdCode)
                return (true, BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(j + 4)));
        return (true, null);
    }
}

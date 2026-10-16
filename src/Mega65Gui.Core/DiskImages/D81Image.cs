using System.Text;

namespace Mega65Gui.Core.DiskImages;

public enum CbmFileType : byte { Del = 0, Seq = 1, Prg = 2, Usr = 3, Rel = 4, Cbm = 5 }

/// <summary>One directory entry of a disk image.</summary>
public sealed record DiskEntry(int DirTrack, int DirSector, int Slot, string Name, CbmFileType Type, bool Closed, bool Locked,
    int FirstTrack, int FirstSector, int Blocks)
{
    public string TypeName => (Closed ? "" : "*") + Type.ToString().ToUpperInvariant() + (Locked ? "<" : "");
}

/// <summary>
/// A 1581 / MEGA65 .D81 disk image (80 tracks × 40 sectors × 256 bytes). Written from the
/// published 1581 disk format: header at 40/0, BAM at 40/1–40/2, directory from 40/3.
/// </summary>
public sealed class D81Image
{
    public const int Tracks = 80, SectorsPerTrack = 40, SectorSize = 256;
    public const int ImageSize = Tracks * SectorsPerTrack * SectorSize; // 819200
    private const int DirTrack = 40;
    private const byte Pad = 0xA0;

    private readonly byte[] _data;

    private D81Image(byte[] data) => _data = data;

    public byte[] ToArray() => (byte[])_data.Clone();

    public static D81Image Load(byte[] data)
    {
        // 822400 = image plus one error byte per sector.
        if (data.Length != ImageSize && data.Length != ImageSize + Tracks * SectorsPerTrack)
            throw new InvalidDataException($"Not a D81 image: {data.Length:N0} bytes (expected {ImageSize:N0}).");
        return new D81Image(data[..ImageSize]);
    }

    public static D81Image Create(string name, string id)
    {
        var img = new D81Image(new byte[ImageSize]);
        var h = Offset(DirTrack, 0);
        img._data[h] = DirTrack; img._data[h + 1] = 3; img._data[h + 2] = (byte)'D';
        img.WriteName(h + 4, name, 16);
        img._data[h + 0x14] = Pad; img._data[h + 0x15] = Pad;
        img.WriteId(h + 0x16, id);
        img._data[h + 0x18] = Pad; img._data[h + 0x19] = (byte)'3'; img._data[h + 0x1A] = (byte)'D';
        img._data[h + 0x1B] = Pad; img._data[h + 0x1C] = Pad;

        for (var b = 1; b <= 2; b++)
        {
            var o = Offset(DirTrack, b);
            img._data[o] = (byte)(b == 1 ? DirTrack : 0); img._data[o + 1] = (byte)(b == 1 ? 2 : 0xFF);
            img._data[o + 2] = (byte)'D'; img._data[o + 3] = 0xBB;
            img._data[o + 4] = img._data[h + 0x16]; img._data[o + 5] = img._data[h + 0x17];
            img._data[o + 6] = 0xC0;
            for (var t = b == 1 ? 1 : 41; t <= (b == 1 ? 40 : 80); t++)
            {
                var e = o + 0x10 + (t - (b == 1 ? 1 : 41)) * 6;
                img._data[e] = SectorsPerTrack;
                for (var i = 1; i <= 5; i++) img._data[e + i] = 0xFF;
            }
        }
        // Header, both BAM sectors and the first directory sector are in use.
        for (var s = 0; s <= 3; s++) img.SetUsed(DirTrack, s, true);
        var d = Offset(DirTrack, 3);
        img._data[d] = 0; img._data[d + 1] = 0xFF;
        return img;
    }

    // ---------------- header ----------------

    public string DiskName
    {
        get => ReadName(Offset(DirTrack, 0) + 4, 16);
        set => WriteName(Offset(DirTrack, 0) + 4, value, 16);
    }

    public string DiskId
    {
        get => Petscii.ToAscii(_data.AsSpan(Offset(DirTrack, 0) + 0x16, 2));
        set
        {
            WriteId(Offset(DirTrack, 0) + 0x16, value);
            foreach (var b in new[] { 1, 2 })
            {
                _data[Offset(DirTrack, b) + 4] = _data[Offset(DirTrack, 0) + 0x16];
                _data[Offset(DirTrack, b) + 5] = _data[Offset(DirTrack, 0) + 0x17];
            }
        }
    }

    /// <summary>Free blocks, excluding the directory track (as the 1581 reports "BLOCKS FREE").</summary>
    public int FreeBlocks => Enumerable.Range(1, Tracks).Where(t => t != DirTrack).Sum(FreeOnTrack);

    // ---------------- directory ----------------

    public IReadOnlyList<DiskEntry> Entries => EnumerateSlots().Where(e => e.Type != CbmFileType.Del || e.Closed).ToList();

    private IEnumerable<DiskEntry> EnumerateSlots()
    {
        var (t, s) = (DirTrack, 3);
        var seen = new HashSet<(int, int)>();
        while (t != 0 && seen.Add((t, s)))
        {
            var o = Offset(t, s);
            for (var slot = 0; slot < 8; slot++)
            {
                var e = o + slot * 32;
                var typeByte = _data[e + 2];
                if (typeByte == 0) { yield return Empty(t, s, slot); continue; }
                yield return new DiskEntry(t, s, slot, ReadName(e + 5, 16), (CbmFileType)(typeByte & 0x07),
                    (typeByte & 0x80) != 0, (typeByte & 0x40) != 0, _data[e + 3], _data[e + 4], _data[e + 0x1E] | _data[e + 0x1F] << 8);
            }
            (t, s) = (_data[o], _data[o + 1]);
        }
    }

    private static DiskEntry Empty(int t, int s, int slot) => new(t, s, slot, "", CbmFileType.Del, false, false, 0, 0, 0);

    public DiskEntry? Find(string name) =>
        Entries.FirstOrDefault(e => string.Equals(e.Name, Petscii.Normalise(name), StringComparison.OrdinalIgnoreCase));

    public byte[] Extract(DiskEntry entry)
    {
        var result = new List<byte>();
        var (t, s) = (entry.FirstTrack, entry.FirstSector);
        var seen = new HashSet<(int, int)>();
        while (t != 0)
        {
            if (!seen.Add((t, s)) || t > Tracks || s >= SectorsPerTrack) throw new InvalidDataException($"Broken sector chain in {entry.Name}.");
            var o = Offset(t, s);
            int nt = _data[o], ns = _data[o + 1];
            var used = nt == 0 ? Math.Max(0, ns - 1) : SectorSize - 2;
            result.AddRange(_data.AsSpan(o + 2, used).ToArray());
            (t, s) = (nt, ns);
        }
        return [.. result];
    }

    public DiskEntry Add(string name, byte[] content, CbmFileType type = CbmFileType.Prg)
    {
        name = Petscii.Normalise(name);
        if (name.Length == 0) throw new ArgumentException("A file name is required.");
        if (Find(name) is not null) throw new InvalidOperationException($"\"{name}\" already exists on the disk.");
        var blocks = Math.Max(1, (content.Length + SectorSize - 3) / (SectorSize - 2));
        if (blocks > FreeBlocks) throw new InvalidOperationException($"Not enough space: {blocks} blocks needed, {FreeBlocks} free.");

        var slot = FreeSlot();
        var sectors = Allocate(blocks);
        for (var i = 0; i < sectors.Count; i++)
        {
            var (t, s) = sectors[i];
            var o = Offset(t, s);
            Array.Clear(_data, o, SectorSize);
            var chunk = content.AsSpan(Math.Min(content.Length, i * (SectorSize - 2)), Math.Min(SectorSize - 2, Math.Max(0, content.Length - i * (SectorSize - 2))));
            chunk.CopyTo(_data.AsSpan(o + 2));
            if (i + 1 < sectors.Count) { _data[o] = (byte)sectors[i + 1].Track; _data[o + 1] = (byte)sectors[i + 1].Sector; }
            else { _data[o] = 0; _data[o + 1] = (byte)(chunk.Length + 1); }
        }

        var e = Offset(slot.DirTrack, slot.DirSector) + slot.Slot * 32;
        Array.Clear(_data, e + 2, 30);
        _data[e + 2] = (byte)(0x80 | (byte)type);
        _data[e + 3] = (byte)sectors[0].Track; _data[e + 4] = (byte)sectors[0].Sector;
        WriteName(e + 5, name, 16);
        _data[e + 0x1E] = (byte)blocks; _data[e + 0x1F] = (byte)(blocks >> 8);
        return Find(name)!;
    }

    public void Delete(DiskEntry entry)
    {
        var (t, s) = (entry.FirstTrack, entry.FirstSector);
        var seen = new HashSet<(int, int)>();
        while (t != 0 && t <= Tracks && s < SectorsPerTrack && seen.Add((t, s)))
        {
            var o = Offset(t, s);
            SetUsed(t, s, false);
            (t, s) = (_data[o], _data[o + 1]);
        }
        _data[Offset(entry.DirTrack, entry.DirSector) + entry.Slot * 32 + 2] = 0;
    }

    public void Rename(DiskEntry entry, string newName)
    {
        newName = Petscii.Normalise(newName);
        if (Find(newName) is { } other && other != entry) throw new InvalidOperationException($"\"{newName}\" already exists.");
        WriteName(Offset(entry.DirTrack, entry.DirSector) + entry.Slot * 32 + 5, newName, 16);
    }

    // ---------------- allocation ----------------

    private (int DirTrack, int DirSector, int Slot) FreeSlot()
    {
        var last = (Track: DirTrack, Sector: 3);
        foreach (var e in EnumerateSlots())
        {
            if (_data[Offset(e.DirTrack, e.DirSector) + e.Slot * 32 + 2] == 0) return (e.DirTrack, e.DirSector, e.Slot);
            last = (e.DirTrack, e.DirSector);
        }
        // Extend the directory with the next free sector on the directory track.
        var next = Enumerable.Range(4, SectorsPerTrack - 4).FirstOrDefault(s => !IsUsed(DirTrack, s));
        if (next == 0) throw new InvalidOperationException("The directory is full (296 entries).");
        SetUsed(DirTrack, next, true);
        var lo = Offset(last.Track, last.Sector);
        _data[lo] = DirTrack; _data[lo + 1] = (byte)next;
        var no = Offset(DirTrack, next);
        Array.Clear(_data, no, SectorSize);
        _data[no] = 0; _data[no + 1] = 0xFF;
        return (DirTrack, next, 0);
    }

    /// <summary>Allocates sectors on the tracks nearest the directory track first, as the 1581 does.</summary>
    private List<(int Track, int Sector)> Allocate(int count)
    {
        var order = Enumerable.Range(1, DirTrack - 1).SelectMany(d => new[] { DirTrack - d, DirTrack + d }).Where(t => t is >= 1 and <= Tracks);
        var result = new List<(int, int)>();
        foreach (var t in order)
            for (var s = 0; s < SectorsPerTrack && result.Count < count; s++)
                if (!IsUsed(t, s)) { SetUsed(t, s, true); result.Add((t, s)); }
        return result;
    }

    private int BamEntry(int track) => Offset(DirTrack, track <= 40 ? 1 : 2) + 0x10 + (track - (track <= 40 ? 1 : 41)) * 6;

    private int FreeOnTrack(int track) => _data[BamEntry(track)];

    private bool IsUsed(int track, int sector) => (_data[BamEntry(track) + 1 + sector / 8] & (1 << (sector % 8))) == 0;

    private void SetUsed(int track, int sector, bool used)
    {
        var e = BamEntry(track);
        var mask = (byte)(1 << (sector % 8));
        var was = IsUsed(track, sector);
        if (used == was) return;
        if (used) { _data[e + 1 + sector / 8] &= (byte)~mask; _data[e]--; }
        else { _data[e + 1 + sector / 8] |= mask; _data[e]++; }
    }

    private static int Offset(int track, int sector) => ((track - 1) * SectorsPerTrack + sector) * SectorSize;

    private string ReadName(int offset, int max) => Petscii.ToAscii(_data.AsSpan(offset, max)).TrimEnd();

    private void WriteName(int offset, string name, int max)
    {
        var bytes = Petscii.FromAscii(Petscii.Normalise(name));
        for (var i = 0; i < max; i++) _data[offset + i] = i < bytes.Length ? bytes[i] : Pad;
    }

    private void WriteId(int offset, string id)
    {
        var b = Petscii.FromAscii((id + "  ")[..2]);
        _data[offset] = b[0]; _data[offset + 1] = b[1];
    }
}

/// <summary>Minimal PETSCII ↔ ASCII mapping for file names (uppercase character set).</summary>
public static class Petscii
{
    public static string Normalise(string name) => new string(name.ToUpperInvariant().Where(c => c is >= ' ' and <= ']' && c != '"').ToArray()).Trim()[..Math.Min(16, name.Trim().Length)];

    public static byte[] FromAscii(string s) => s.Select(c => (byte)c).ToArray();

    public static string ToAscii(ReadOnlySpan<byte> bytes)
    {
        var sb = new StringBuilder();
        foreach (var b in bytes)
        {
            if (b == 0xA0) break;
            sb.Append(b switch
            {
                >= 0x20 and <= 0x5D => (char)b,
                >= 0xC1 and <= 0xDA => (char)(b - 0x80),
                _ => '?',
            });
        }
        return sb.ToString();
    }
}

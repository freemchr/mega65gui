using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;
using Mega65Gui.Core.Common;
using Mega65Gui.Core.Cores;
using Mega65Gui.Core.Device;
using Mega65Gui.Core.Filehost;
using Mega65Gui.Core.Sd;
using Mega65Gui.Core.Tools;
using Mega65Gui.Core.Update;

namespace Mega65Gui.Core.Tests;

/// <summary>Synthetic .bit and .cor files laid out as Vivado and bit2core write them.</summary>
public static class CoreFixtures
{
    public static byte[] MakeBit(uint idCode = 0x13636093, string part = "7a200tfbg484", int padding = 64, bool sync = true, int extraLength = 0)
    {
        var data = new List<byte>();
        data.AddRange(Enumerable.Repeat((byte)0xFF, 32));
        foreach (var w in new uint[] { 0x000000BB, 0x11220044, 0xFFFFFFFF, 0xFFFFFFFF })
            data.AddRange(Be(w));
        if (sync) data.AddRange(Be(0xAA995566));
        foreach (var w in new uint[] { 0x20000000, 0x30018001, idCode, 0x20000000 })
            data.AddRange(Be(w));
        data.AddRange(Enumerable.Range(0, padding).Select(i => (byte)(i * 13)));

        var bit = new List<byte> { 0x00, 0x09, 0x0F, 0xF0, 0x0F, 0xF0, 0x0F, 0xF0, 0x0F, 0xF0, 0x00, 0x00, 0x01 };
        void Field(char key, string text)
        {
            var b = Encoding.ASCII.GetBytes(text + "\0");
            bit.Add((byte)key);
            bit.Add((byte)(b.Length >> 8));
            bit.Add((byte)b.Length);
            bit.AddRange(b);
        }
        Field('a', "container;COMPRESS=TRUE;UserID=0XFFFFFFFF;Version=2022.2");
        Field('b', part);
        Field('c', "2026/10/01");
        Field('d', "12:00:00");
        bit.Add((byte)'e');
        bit.AddRange(Be((uint)(data.Count + extraLength)));
        bit.AddRange(data);
        return [.. bit];
    }

    public static byte[] MakeCor(byte[] bit, string name = "Galaga", string version = "0.5.2", string target = "mega65r6", byte modelId = 0x06, bool damage = false)
    {
        var size = CoreFile.HeaderSize + bit.Length + 4;
        var d = new byte[size];
        Encoding.ASCII.GetBytes(CoreFile.Magic).CopyTo(d, 0);
        Encoding.ASCII.GetBytes(name).CopyTo(d, 0x10);
        Encoding.ASCII.GetBytes(version).CopyTo(d, 0x30);
        Encoding.ASCII.GetBytes(target).CopyTo(d, 0x50);
        d[0x70] = modelId;
        bit.CopyTo(d, CoreFile.HeaderSize);
        BitConverter.TryWriteBytes(d.AsSpan(0x80), (uint)size);
        BitConverter.TryWriteBytes(d.AsSpan(0x84), 0xF0F0F0F0u);
        BitConverter.TryWriteBytes(d.AsSpan(0x84), CoreFile.Crc32(d));
        if (damage) d[CoreFile.HeaderSize + 20] ^= 0xFF;
        return d;
    }

    private static byte[] Be(uint v)
    {
        var b = new byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(b, v);
        return b;
    }
}

public sealed class TempDir : IDisposable
{
    public string Path { get; } = Directory.CreateTempSubdirectory("m65cores").FullName;
    public string File(string name, byte[] data) { var p = System.IO.Path.Combine(Path, name); System.IO.File.WriteAllBytes(p, data); return p; }
    public void Dispose() { try { Directory.Delete(Path, true); } catch (IOException) { } }
}

public class BitstreamFileTests
{
    [Fact]
    public void Reads_header_fields_and_the_fpga_idcode()
    {
        var info = BitstreamFile.Parse(CoreFixtures.MakeBit());
        Assert.StartsWith("container", info.Design);
        Assert.Equal("7a200tfbg484", info.Part);
        Assert.Equal("2026/10/01", info.Date);
        Assert.True(info.IsValid);
        Assert.Equal(0x13636093u, info.IdCode);
        Assert.Equal("XC7A200T", info.Fpga);
        Assert.Equal("XC7A100T", BitstreamFile.Parse(CoreFixtures.MakeBit(0x03631093)).Fpga);
    }

    [Fact]
    public void Rejects_malformed_bitstreams()
    {
        Assert.Throws<InvalidDataException>(() => BitstreamFile.Parse(new byte[100]));
        Assert.Throws<InvalidDataException>(() => BitstreamFile.Parse(CoreFixtures.MakeBit()[..40]));
        Assert.False(BitstreamFile.Parse(CoreFixtures.MakeBit(sync: false)).IsValid);
        var truncated = BitstreamFile.Parse(CoreFixtures.MakeBit(extraLength: 1000));
        Assert.False(truncated.LengthFits);
        Assert.False(truncated.IsValid);
    }
}

public class CoreExtractionTests
{
    [Fact]
    public void Extracts_the_bit_stored_in_a_core_file()
    {
        var bit = CoreFixtures.MakeBit();
        Assert.Equal(bit, CoreFile.ExtractBitstream(CoreFixtures.MakeCor(bit)));
    }

    [Fact]
    public void Refuses_damaged_cores_and_cores_without_a_bit()
    {
        Assert.Throws<InvalidDataException>(() => CoreFile.ExtractBitstream(CoreFixtures.MakeCor(CoreFixtures.MakeBit(), damage: true)));
        // A body that is not a Vivado .bit (for example raw configuration data) is not guessed at.
        Assert.Throws<InvalidDataException>(() => CoreFile.ExtractBitstream(CoreFixtures.MakeCor(Enumerable.Repeat((byte)0xFF, 600).ToArray())));
        Assert.Throws<InvalidDataException>(() => CoreFile.ExtractBitstream(CoreFixtures.MakeCor(CoreFixtures.MakeBit(extraLength: 5000))));
    }
}

public class CoreCompatibilityTests
{
    private static CoreIdentity Cor(string target, byte modelId, bool damage = false) =>
        CoreIdentity.FromCore(CoreFile.Parse(CoreFixtures.MakeCor(CoreFixtures.MakeBit(), target: target, modelId: modelId, damage: damage)));

    [Theory]
    [InlineData("mega65r6", 0x06, HardwareModel.R6, Provenance.ReadFromDevice, CompatibilityResult.Compatible)]
    [InlineData("mega65r3", 0x03, HardwareModel.R6, Provenance.ReadFromDevice, CompatibilityResult.Incompatible)]
    [InlineData("mega65r6", 0x06, HardwareModel.R3, Provenance.ReadFromDevice, CompatibilityResult.Incompatible)]
    [InlineData("mega65r6", 0x06, HardwareModel.R5, Provenance.ReadFromDevice, CompatibilityResult.Unverified)]
    [InlineData("mega65r6", 0x06, HardwareModel.Unknown, Provenance.NotKnown, CompatibilityResult.Unverified)]
    [InlineData("mega65r6", 0x06, HardwareModel.R6, Provenance.UserSupplied, CompatibilityResult.Unverified)]
    [InlineData("mega65r6", 0x03, HardwareModel.R6, Provenance.ReadFromDevice, CompatibilityResult.Unverified)] // header disagrees with itself
    [InlineData("nexys4ddr", 0x41, HardwareModel.Nexys4, Provenance.ReadFromDevice, CompatibilityResult.Unverified)]
    [InlineData("mega65r9", 0x09, HardwareModel.R6, Provenance.ReadFromDevice, CompatibilityResult.Unverified)]
    public void Only_an_exact_confirmed_match_is_compatible(string target, byte modelId, HardwareModel device, Provenance source, CompatibilityResult expected) =>
        Assert.Equal(expected, CoreTargets.Check(Cor(target, modelId), device, source).Result);

    [Fact]
    public void Damaged_cores_are_never_compatible() =>
        Assert.Equal(CompatibilityResult.Incompatible, CoreTargets.Check(Cor("mega65r6", 6, damage: true), HardwareModel.R6, Provenance.ReadFromDevice).Result);

    [Fact]
    public void Bitstreams_are_never_confirmed()
    {
        var bit = BitstreamFile.Parse(CoreFixtures.MakeBit());
        Assert.Equal(CompatibilityResult.Unverified, CoreTargets.Check(CoreIdentity.FromBitstream(bit), HardwareModel.R6, Provenance.ReadFromDevice).Result);
        // The user's own label does not confirm it either; a label for another board blocks it.
        Assert.Equal(CompatibilityResult.Unverified, CoreTargets.Check(CoreIdentity.FromBitstream(bit, HardwareModel.R6), HardwareModel.R6, Provenance.ReadFromDevice).Result);
        Assert.Equal(CompatibilityResult.Incompatible, CoreTargets.Check(CoreIdentity.FromBitstream(bit, HardwareModel.R3), HardwareModel.R6, Provenance.ReadFromDevice).Result);
        // A bitstream for a different FPGA is refused outright.
        var small = BitstreamFile.Parse(CoreFixtures.MakeBit(0x03631093));
        Assert.Equal(CompatibilityResult.Incompatible, CoreTargets.Check(CoreIdentity.FromBitstream(small, HardwareModel.R6), HardwareModel.R6, Provenance.ReadFromDevice).Result);
    }

    [Theory]
    [InlineData("GalagaMega65_0.5.2_r3_r6.zip", "R3,R6")]
    [InlineData("megazxnext_v1.0_r4_r5_r6.zip", "R4,R5,R6")]
    [InlineData("Mega][e_R3_R6_V2.zip", "R3,R6")]
    [InlineData("mega65r6-release-0.97.2-03b24c6-norom.7z", "R6")]
    [InlineData("IMSAI8080-MEGA65-R3A-Experimental-v0.2.1.zip", "R3")]
    [InlineData("C64MEGA65-V5.2.zip", "")]
    [InlineData("Vixen-32_Beta2_v0.76.zip", "")]
    public void Finds_boards_named_in_file_names(string name, string expected) =>
        Assert.Equal(expected, string.Join(",", CoreTargets.ModelsNamedIn(name)));
}

public class CoreLibraryTests : IDisposable
{
    private readonly TempDir _tmp = new();
    private readonly CoreLibrary _lib;

    public CoreLibraryTests()
    {
        _lib = new CoreLibrary(Path.Combine(_tmp.Path, "lib"), Path.Combine(_tmp.Path, "extract"));
        _lib.Load();
    }

    public void Dispose() => _tmp.Dispose();

    private string CorFile(string name = "galaga_r6.cor", string core = "Galaga", string target = "mega65r6", byte id = 6, bool damage = false) =>
        _tmp.File(name, CoreFixtures.MakeCor(CoreFixtures.MakeBit(padding: name.Length * 7), core, target: target, modelId: id, damage: damage));

    [Fact]
    public async Task Imports_a_core_with_its_header_and_detects_duplicates_by_sha256()
    {
        var path = CorFile();
        var first = await _lib.ImportAsync(path);
        Assert.False(first.WasDuplicate);
        Assert.Equal("Galaga", first.Entry.DisplayName);
        Assert.Equal(CoreFileKind.Cor, first.Entry.Kind);
        Assert.Equal(HardwareModel.R6, first.Entry.Identity.Model);
        Assert.Equal(Provenance.ReadFromFile, first.Entry.Identity.ModelSource);
        Assert.Equal(await Hashing.Sha256FileAsync(path), first.Entry.Id);
        Assert.True(await _lib.VerifyAsync(first.Entry));

        // The same bytes under another name are not stored again.
        var copy = _tmp.File("renamed.cor", File.ReadAllBytes(path));
        var second = await _lib.ImportAsync(copy);
        Assert.True(second.WasDuplicate);
        Assert.Single(_lib.Entries);
    }

    [Fact]
    public async Task Refuses_malformed_damaged_and_unsupported_files_without_leaving_anything_behind()
    {
        var bad = _tmp.File("bad.cor", new byte[5000]);
        await Assert.ThrowsAsync<InvalidDataException>(() => _lib.ImportAsync(bad));
        await Assert.ThrowsAsync<InvalidDataException>(() => _lib.ImportAsync(CorFile("damaged.cor", damage: true)));
        await Assert.ThrowsAsync<InvalidDataException>(() => _lib.ImportAsync(_tmp.File("nosync.bit", CoreFixtures.MakeBit(sync: false))));
        await Assert.ThrowsAsync<InvalidDataException>(() => _lib.ImportAsync(_tmp.File("short.bit", CoreFixtures.MakeBit(extraLength: 900))));
        await Assert.ThrowsAsync<InvalidDataException>(() => _lib.ImportAsync(_tmp.File("readme.txt", [1, 2, 3])));
        Assert.Empty(_lib.Entries);
        Assert.False(Directory.Exists(Path.Combine(_lib.Root, "files")) && Directory.EnumerateFiles(Path.Combine(_lib.Root, "files"), "*", SearchOption.AllDirectories).Any());
    }

    [Fact]
    public async Task A_cancelled_import_adds_nothing()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => _lib.ImportAsync(CorFile(), null, cts.Token));
        Assert.Empty(_lib.Entries);
    }

    [Fact]
    public async Task Metadata_persists_and_is_validated()
    {
        var e = (await _lib.ImportAsync(_tmp.File("dev.bit", CoreFixtures.MakeBit()))).Entry;
        Assert.Equal(CoreFileKind.Bit, e.Kind);
        Assert.Null(e.Identity.Model);
        _lib.Update(e.Id, x => x with { DisplayName = "  My build ", Favorite = true, Category = "Dev", Tags = CoreLibrary.ParseTags("test, r6;test"), Notes = "works", DeclaredModel = HardwareModel.R6 });
        _lib.SetSlotNote(3, "Galaga");

        var reloaded = new CoreLibrary(_lib.Root, Path.Combine(_tmp.Path, "extract"));
        reloaded.Load();
        var r = Assert.Single(reloaded.Entries);
        Assert.Equal("My build", r.DisplayName);
        Assert.True(r.Favorite);
        Assert.Equal(["test", "r6"], r.Tags);
        Assert.Equal(HardwareModel.R6, r.Identity.Model);
        Assert.Equal(Provenance.UserSupplied, r.Identity.ModelSource);
        Assert.Equal("XC7A200T", r.Bitstream!.Fpga);
        Assert.Equal("Galaga", reloaded.SlotNotes[3]);
        Assert.Single(reloaded.Search(new CoreLibraryQuery("build", FavoritesOnly: true)));
        Assert.Empty(reloaded.Search(new CoreLibraryQuery(Category: "Other")));

        Assert.Throws<ArgumentException>(() => _lib.Update(e.Id, x => x with { DisplayName = " " }));
        Assert.Throws<InvalidOperationException>(() => _lib.Update(e.Id, x => x with { Size = 1 }));
    }

    [Fact]
    public async Task Export_remove_and_artwork()
    {
        var e = (await _lib.ImportAsync(CorFile())).Entry;
        var dest = Path.Combine(_tmp.Path, "out.cor");
        await _lib.ExportAsync(e.Id, dest);
        Assert.Equal(e.Id, await Hashing.Sha256FileAsync(dest));

        await Assert.ThrowsAsync<InvalidDataException>(() => _lib.SetArtworkAsync(e.Id, _tmp.File("art.exe", [1])));
        var withArt = await _lib.SetArtworkAsync(e.Id, _tmp.File("art.png", [0x89, 0x50, 0x4E, 0x47]));
        Assert.True(File.Exists(_lib.ArtworkPathOf(withArt)));

        // A library copy that changed on disk is caught before it is used.
        File.AppendAllText(_lib.PathOf(e), "x");
        Assert.False(await _lib.VerifyAsync(e));
        await Assert.ThrowsAsync<IOException>(() => _lib.ExportAsync(e.Id, dest + "2"));

        _lib.Remove(e.Id);
        Assert.Empty(_lib.Entries);
        Assert.False(File.Exists(_lib.PathOf(e)));
        Assert.False(File.Exists(_lib.ArtworkPathOf(withArt)));
    }

    [Fact]
    public async Task Imports_every_core_in_a_package_and_lists_the_ones_it_refuses()
    {
        var zip = Path.Combine(_tmp.Path, "GalagaMega65_0.5.2_r3_r6.zip");
        using (var archive = ZipFile.Open(zip, ZipArchiveMode.Create))
        {
            void Add(string name, byte[] data) { using var s = archive.CreateEntry(name).Open(); s.Write(data); }
            Add("R3/galaga_r3.cor", CoreFixtures.MakeCor(CoreFixtures.MakeBit(padding: 10), target: "mega65r3", modelId: 3));
            Add("R6/galaga_r6.cor", CoreFixtures.MakeCor(CoreFixtures.MakeBit(padding: 20)));
            Add("R6/broken.cor", CoreFixtures.MakeCor(CoreFixtures.MakeBit(padding: 30), damage: true));
            Add("README.md", "hello"u8.ToArray());
        }
        var origin = new CoreOrigin { FilehostFileId = "f1", FilehostVersionId = "v1", FilehostTitle = "Galaga" };
        var r = await _lib.ImportPackageAsync(zip, origin);
        Assert.Equal(2, r.Imported.Count);
        Assert.Contains(r.Rejected, x => x.File.EndsWith("broken.cor"));
        Assert.All(r.Imported, i => Assert.Equal("v1", i.Entry.Origin.FilehostVersionId));
        Assert.All(r.Imported, i => Assert.Equal("GalagaMega65_0.5.2_r3_r6.zip", i.Entry.Origin.PackageFileName));
        Assert.Equal([HardwareModel.R3, HardwareModel.R6], r.Imported[0].Entry.NamedModels);
        Assert.Empty(Directory.EnumerateDirectories(Path.Combine(_tmp.Path, "extract")));
    }

    [Fact]
    public async Task Finds_cores_in_an_archive_inside_the_package()
    {
        // Laid out like Vixen-32_Beta2_v0.76.zip: docs at the top, the core in vixen32-r6-v076.zip.
        var inner = new MemoryStream();
        using (var a = new ZipArchive(inner, ZipArchiveMode.Create, leaveOpen: true))
        {
            using (var s = a.CreateEntry("vixen32-r6-v076.cor").Open()) s.Write(CoreFixtures.MakeCor(CoreFixtures.MakeBit(padding: 40), "Vixen-32"));
            using (var s = a.CreateEntry("vixen32-r6-v076.bit").Open()) s.Write(CoreFixtures.MakeBit(padding: 41));
        }
        var zip = Path.Combine(_tmp.Path, "Vixen-32_Beta2_v0.76.zip");
        using (var outer = ZipFile.Open(zip, ZipArchiveMode.Create))
        {
            using (var s = outer.CreateEntry("Vixen-32_Beta2_v0.76/README.txt").Open()) s.Write("readme"u8);
            using (var s = outer.CreateEntry("Vixen-32_Beta2_v0.76/vixen32-r6-v076.zip").Open()) s.Write(inner.ToArray());
            using (var s = outer.CreateEntry("Vixen-32_Beta2_v0.76/broken.zip").Open()) s.Write("not a zip"u8);
        }
        var r = await _lib.ImportPackageAsync(zip);
        Assert.Equal(["vixen32-r6-v076.bit", "vixen32-r6-v076.cor"], r.Imported.Select(i => i.Entry.FileName).Order());
        Assert.Contains(r.Rejected, x => x.File.EndsWith("broken.zip"));
        Assert.All(r.Imported, i => Assert.Equal("Vixen-32_Beta2_v0.76.zip", i.Entry.Origin.PackageFileName));
    }

    [Fact]
    public void An_unreadable_index_is_kept_and_reported()
    {
        File.WriteAllText(Path.Combine(_lib.Root, "index.json"), "{ not json");
        _lib.Load();
        Assert.NotNull(_lib.LoadWarning);
        Assert.Empty(_lib.Entries);
        Assert.Single(Directory.EnumerateFiles(_lib.Root, "index.json.unreadable-*"));
    }
}

public class FilehostCoresTests
{
    private static FilehostFile F(string id, string title, string type, string? file = null, string? version = "v2", string author = "someone") =>
        new() { FileId = id, Title = title, Category = "Firmware", Type = type, FileName = file, VersionId = version, Author = author, SortDate = "2026/01/01/00/00/00" };

    private static readonly FilehostFile[] Catalogue =
    [
        F("1", "GalagaMega65_0.5.2_r3_r6", "Arcade Core", "GalagaMega65_0.5.2_r3_r6.zip"),
        F("2", "C64 for MEGA65", "Alternate Core", "C64MEGA65-V5.2.zip"),
        F("3", "MEGA65 Core Release Package (mega65r6) no ROM", "Release Package", "mega65r6-release-0.97.2-03b24c6-norom.7z", author: "MEGA65"),
        F("4", "Some ROM", "ROM"),
        new() { FileId = "5", Title = "A game", Category = "Game", Type = "Arcade" },
    ];

    [Fact]
    public void Lists_only_fpga_cores_with_named_boards_and_versions()
    {
        var all = FilehostCores.Search(Catalogue, new FilehostCoreQuery()).ToList();
        Assert.Equal(["1", "2", "3"], all.Select(c => c.File.FileId).Order());
        var galaga = all.Single(c => c.File.FileId == "1");
        Assert.Equal([HardwareModel.R3, HardwareModel.R6], galaga.NamedModels);
        Assert.Equal("0.5.2", galaga.VersionHint);
        Assert.Equal("5.2", all.Single(c => c.File.FileId == "2").VersionHint);

        Assert.Equal(["1", "3"], FilehostCores.Search(Catalogue, new FilehostCoreQuery(Model: HardwareModel.R6)).Select(c => c.File.FileId).Order());
        Assert.Equal(["3"], FilehostCores.Search(Catalogue, new FilehostCoreQuery(OfficialOnly: true)).Select(c => c.File.FileId));
        Assert.Equal(["2"], FilehostCores.Search(Catalogue, new FilehostCoreQuery(Text: "c64", Kind: "Alternate Core")).Select(c => c.File.FileId));
    }

    [Fact]
    public void Flags_a_newer_filehost_version_of_a_library_core()
    {
        CoreLibraryEntry Entry(string fileId, string versionId) => new()
        {
            Id = new string('a', 64), FileName = "x.cor", Kind = CoreFileKind.Cor, Size = 1, StoredPath = "x", DisplayName = "x",
            Origin = new CoreOrigin { FilehostFileId = fileId, FilehostVersionId = versionId },
        };
        Assert.True(FilehostCores.HasNewerVersion(Catalogue[0], [Entry("1", "v1")]));
        Assert.False(FilehostCores.HasNewerVersion(Catalogue[0], [Entry("1", "v1"), Entry("1", "v2")]));
        Assert.False(FilehostCores.HasNewerVersion(Catalogue[0], [Entry("9", "v1")]));
        Assert.True(FilehostCores.IsInLibrary(Catalogue[0], [Entry("1", "v2")]));
    }
}

public class CoreOperationTests
{
    private static readonly ConnectionTarget Usb = ConnectionTarget.Serial("COM3");
    private static readonly ToolInstallation Tools = new("tools", new Dictionary<string, string> { ["m65"] = "m65.exe" }, "1", "test", DateTimeOffset.Now);

    private static CoreLibraryEntry Entry(byte[] data, CoreFileKind kind, HardwareModel? declared = null) => new()
    {
        Id = new string('b', 64), FileName = kind == CoreFileKind.Cor ? "c.cor" : "c.bit", Kind = kind, Size = data.Length, StoredPath = "x", DisplayName = "Core",
        Core = kind == CoreFileKind.Cor ? CoreFile.Parse(data) : null,
        Bitstream = kind == CoreFileKind.Bit ? BitstreamFile.Parse(data) : null,
        DeclaredModel = declared,
    };

    private static readonly CoreLibraryEntry R6Core = Entry(CoreFixtures.MakeCor(CoreFixtures.MakeBit()), CoreFileKind.Cor);
    private static readonly CoreLibraryEntry R3Core = Entry(CoreFixtures.MakeCor(CoreFixtures.MakeBit(), target: "mega65r3", modelId: 3), CoreFileKind.Cor);
    private static ICoreLoader Loader(bool tools = true) => new JtagCoreLoader(tools ? new Mega65Commands(Tools, new FakeRunner()) : null);

    [Fact]
    public void Run_needs_tools_usb_jtag_an_intact_file_and_a_matching_board()
    {
        Assert.True(CoreOperations.CheckRun(R6Core, true, Loader(), Usb, HardwareModel.R6, Provenance.ReadFromDevice) is { Allowed: true, NeedsExplicitConfirmation: false });
        Assert.Contains("device tools", CoreOperations.CheckRun(R6Core, true, Loader(false), Usb, HardwareModel.R6, Provenance.ReadFromDevice).Blocker);
        Assert.Contains("Connect", CoreOperations.CheckRun(R6Core, true, Loader(), null, HardwareModel.R6, Provenance.ReadFromDevice).Blocker);
        Assert.Contains("Ethernet", CoreOperations.CheckRun(R6Core, true, Loader(), ConnectionTarget.Ethernet("fe80::1"), HardwareModel.R6, Provenance.ReadFromDevice).Blocker);
        Assert.False(CoreOperations.CheckRun(R6Core, false, Loader(), Usb, HardwareModel.R6, Provenance.ReadFromDevice).Allowed);
        Assert.False(CoreOperations.CheckRun(R3Core, true, Loader(), Usb, HardwareModel.R6, Provenance.ReadFromDevice).Allowed);
        // Model unknown: allowed only after an explicit confirmation.
        Assert.True(CoreOperations.CheckRun(R6Core, true, Loader(), Usb, HardwareModel.Unknown, Provenance.NotKnown).NeedsExplicitConfirmation);
    }

    [Fact]
    public void A_bitstream_runs_only_once_labelled_with_this_board_and_always_with_a_warning()
    {
        var bit = CoreFixtures.MakeBit();
        Assert.False(CoreOperations.CheckRun(Entry(bit, CoreFileKind.Bit), true, Loader(), Usb, HardwareModel.R6, Provenance.ReadFromDevice).Allowed);
        Assert.False(CoreOperations.CheckRun(Entry(bit, CoreFileKind.Bit, HardwareModel.R3), true, Loader(), Usb, HardwareModel.R6, Provenance.ReadFromDevice).Allowed);
        var labelled = CoreOperations.CheckRun(Entry(bit, CoreFileKind.Bit, HardwareModel.R6), true, Loader(), Usb, HardwareModel.R6, Provenance.ReadFromDevice);
        Assert.True(labelled.Allowed);
        Assert.True(labelled.NeedsExplicitConfirmation);
        Assert.Contains(labelled.Warnings, w => w.Contains("temporary"));
    }

    [Fact]
    public void Boot_slot_checks()
    {
        Assert.True(CoreOperations.CheckBootSlot(2, true, Usb, HardwareModel.R6).Allowed);
        Assert.False(CoreOperations.CheckBootSlot(8, true, Usb, HardwareModel.R6).Allowed);
        Assert.False(CoreOperations.CheckBootSlot(1, false, Usb, HardwareModel.R6).Allowed);
        Assert.False(CoreOperations.CheckBootSlot(1, true, ConnectionTarget.Ethernet("x"), HardwareModel.R6).Allowed);
        Assert.Contains("serial monitor", CoreOperations.CheckBootSlot(1, true, Usb, HardwareModel.R6, monitorAnswers: false).Blocker);
        Assert.Contains(CoreOperations.CheckBootSlot(0, true, Usb, HardwareModel.R6).Warnings, w => w.Contains("factory"));
        Assert.Contains(CoreOperations.CheckBootSlot(1, true, Usb, HardwareModel.R6).Warnings, w => w.Contains("cannot read"));
    }

    [Fact]
    public void Staging_takes_cor_files_for_this_board_and_says_it_is_not_an_install()
    {
        var ok = CoreOperations.CheckStage(R6Core, true, HardwareModel.R6, Provenance.ReadFromDevice);
        Assert.True(ok.Allowed);
        Assert.Contains(ok.Warnings, w => w.Contains("cannot start a core from the SD card"));
        Assert.False(CoreOperations.CheckStage(R3Core, true, HardwareModel.R6, Provenance.ReadFromDevice).Allowed);
        Assert.Contains("MEGAFLASH installs .cor", CoreOperations.CheckStage(Entry(CoreFixtures.MakeBit(), CoreFileKind.Bit, HardwareModel.R6), true, HardwareModel.R6, Provenance.ReadFromDevice).Blocker);
        Assert.True(CoreOperations.CheckStage(R6Core, true, HardwareModel.R6, Provenance.UserSupplied).NeedsExplicitConfirmation);
    }

    [Fact]
    public void Flash_instructions_never_suggest_the_factory_slot()
    {
        var steps = CoreStaging.FlashInstructions(HardwareModel.R6, "galaga_r6.cor");
        Assert.Contains("from 1 to 7", steps);
        Assert.Contains("Never choose slot 0", steps);
        Assert.Contains("1 or higher", CoreStaging.FlashInstructions(HardwareModel.R3, "x.cor"));
    }
}

/// <summary>Records tool runs and answers them like m65 would.</summary>
internal sealed class FakeRunner(Func<IReadOnlyList<string>, ProcessResult>? answer = null) : IProcessRunner
{
    public List<(string File, IReadOnlyList<string> Args)> Calls { get; } = [];
    public Action<IReadOnlyList<string>>? OnRun { get; set; }

    public Task<ProcessResult> RunAsync(string fileName, IReadOnlyList<string> args, TimeSpan timeout, IProgress<string>? output = null,
        CancellationToken ct = default, string? workingDirectory = null, string? standardInput = null)
    {
        ct.ThrowIfCancellationRequested();
        Calls.Add((fileName, args.ToList()));
        OnRun?.Invoke(args);
        return Task.FromResult(answer?.Invoke(args) ?? new ProcessResult(0, "", "", TimeSpan.Zero));
    }
}

public class JtagCoreLoaderTests : IDisposable
{
    private readonly TempDir _tmp = new();
    public void Dispose() => _tmp.Dispose();
    private static readonly ToolInstallation Tools = new("tools", new Dictionary<string, string> { ["m65"] = "m65.exe" }, "1", "test", DateTimeOffset.Now);
    private static readonly ConnectionTarget Usb = ConnectionTarget.Serial("COM3");

    [Fact]
    public async Task Loads_a_bit_with_m65_q()
    {
        var runner = new FakeRunner();
        var bit = _tmp.File("core.bit", CoreFixtures.MakeBit());
        var r = await new JtagCoreLoader(new Mega65Commands(Tools, runner)).LoadAsync(bit, Usb, null, default);
        Assert.True(r.Succeeded);
        Assert.Equal(["-l", "COM3", "-s", "2000000", "-q", bit], runner.Calls.Single().Args);
    }

    [Fact]
    public async Task Loads_a_cor_through_the_bit_it_contains_and_deletes_the_temporary_file()
    {
        var bit = CoreFixtures.MakeBit();
        string? sent = null;
        byte[]? content = null;
        var runner = new FakeRunner { OnRun = a => { sent = a[^1]; content = File.ReadAllBytes(sent); } };
        var r = await new JtagCoreLoader(new Mega65Commands(Tools, runner)).LoadAsync(_tmp.File("core.cor", CoreFixtures.MakeCor(bit)), Usb, null, default);
        Assert.True(r.Succeeded);
        Assert.EndsWith(".bit", sent);
        Assert.Equal(bit, content);
        Assert.False(File.Exists(sent));
    }

    [Fact]
    public async Task Explains_device_failures_and_timeouts()
    {
        var bit = _tmp.File("core.bit", CoreFixtures.MakeBit());
        var winusb = new FakeRunner(_ => new ProcessResult(1, "", "ERROR: Interface 0 needs to be WinUSB", TimeSpan.Zero));
        var r = await new JtagCoreLoader(new Mega65Commands(Tools, winusb)).LoadAsync(bit, Usb, null, default);
        Assert.False(r.Succeeded);
        Assert.Contains("WinUSB", r.Message);

        var timeout = new FakeRunner(_ => new ProcessResult(-1, "", "\nTimed out after 00:10:00.", TimeSpan.Zero));
        Assert.Contains("did not finish in time", (await new JtagCoreLoader(new Mega65Commands(Tools, timeout)).LoadAsync(bit, Usb, null, default)).Message);

        var other = new FakeRunner(_ => new ProcessResult(3, "no FPGA found", "", TimeSpan.Zero));
        Assert.Contains("code 3", (await new JtagCoreLoader(new Mega65Commands(Tools, other)).LoadAsync(bit, Usb, null, default)).Message);
    }

    [Fact]
    public async Task Cancellation_and_unusable_connections()
    {
        var bit = _tmp.File("core.bit", CoreFixtures.MakeBit());
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var loader = new JtagCoreLoader(new Mega65Commands(Tools, new FakeRunner()));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => loader.LoadAsync(bit, Usb, null, cts.Token));
        await Assert.ThrowsAsync<InvalidOperationException>(() => loader.LoadAsync(bit, ConnectionTarget.Ethernet("fe80::1"), null, default));
        await Assert.ThrowsAsync<InvalidDataException>(() => loader.LoadAsync(_tmp.File("bad.cor", CoreFixtures.MakeCor(CoreFixtures.MakeBit(), damage: true)), Usb, null, default));
    }
}

public class DeviceOperationGateTests
{
    [Fact]
    public void Only_one_operation_at_a_time()
    {
        var gate = new DeviceOperationGate();
        var changes = 0;
        gate.Changed += () => changes++;
        var claim = gate.Enter("Loading a core");
        Assert.Equal("Loading a core", gate.Current);
        var e = Assert.Throws<DeviceBusyException>(() => gate.Enter("Starting a slot"));
        Assert.Equal("Loading a core", e.Running);
        claim.Dispose();
        claim.Dispose();
        Assert.Null(gate.Current);
        gate.Enter("Starting a slot").Dispose();
        Assert.Equal(4, changes);
    }
}

public class CoreStagingTests : IDisposable
{
    private readonly TempDir _tmp = new();
    private readonly CoreLibrary _lib;
    private readonly AppPaths _paths;

    public CoreStagingTests()
    {
        _paths = new AppPaths(Path.Combine(_tmp.Path, "home"));
        _lib = new CoreLibrary(_paths.CoreLibrary, _paths.Extracted);
    }

    public void Dispose() => _tmp.Dispose();

    private sealed class Gate(bool approve) : IApprovalGate
    {
        public int Asked;
        public Task<bool> ConfirmAsync(string title, string message, CancellationToken ct) => Task.FromResult(approve);
        public Task<IReadOnlySet<string>?> ApproveChangesAsync(string title, SyncPlan plan, CancellationToken ct)
        {
            Asked++;
            return Task.FromResult<IReadOnlySet<string>?>(approve ? plan.Changes.Select(c => c.RelativePath).ToHashSet() : null);
        }
    }

    /// <summary>The card inside the MEGA65, kept in memory; optionally corrupts what is read back.</summary>
    private sealed class FakeCard(bool corruptReadBack = false) : IRemoteCard
    {
        public Dictionary<string, byte[]> Files { get; } = new(StringComparer.OrdinalIgnoreCase);
        public string Description => "fake card";
        public Task<IReadOnlyList<RemoteEntry>> ListRootAsync(CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<RemoteEntry>>(Files.Select(f => new RemoteEntry(f.Key, f.Key, f.Value.Length, false)).ToList());
        public Task DownloadAsync(IReadOnlyList<string> names, string localDir, IProgress<string>? log, CancellationToken ct)
        {
            Directory.CreateDirectory(localDir);
            foreach (var n in names)
            {
                var d = (byte[])Files[n].Clone();
                if (corruptReadBack) d[^1] ^= 1;
                File.WriteAllBytes(Path.Combine(localDir, n), d);
            }
            return Task.CompletedTask;
        }
        public Task UploadAsync(string localDir, IReadOnlyList<string> names, IProgress<string>? log, CancellationToken ct)
        {
            foreach (var n in names) Files[n] = File.ReadAllBytes(Path.Combine(localDir, n));
            return Task.CompletedTask;
        }
    }

    private async Task<CoreLibraryEntry> Core(string name = "galaga r6.cor") =>
        (await _lib.ImportAsync(_tmp.File(name, CoreFixtures.MakeCor(CoreFixtures.MakeBit())))).Entry;

    [Fact]
    public async Task Copies_to_a_card_folder_only_after_approval_and_backs_up_what_it_replaces()
    {
        var e = await Core();
        var card = Directory.CreateDirectory(Path.Combine(_tmp.Path, "card")).FullName;

        var declined = await new CoreStaging(_lib, new Gate(false), _paths).StageToFolderAsync(e, card);
        Assert.False(declined.Staged);
        Assert.False(File.Exists(Path.Combine(card, "galaga_r6.cor")));

        File.WriteAllText(Path.Combine(card, "galaga_r6.cor"), "old core");
        var gate = new Gate(true);
        var staged = await new CoreStaging(_lib, gate, _paths).StageToFolderAsync(e, card);
        Assert.True(staged.Staged);
        Assert.Equal("galaga_r6.cor", staged.CardFileName);
        Assert.Equal(e.Id, await Hashing.Sha256FileAsync(Path.Combine(card, "galaga_r6.cor")));
        Assert.Contains(staged.Sync!.Results, r => r.BackupPath is { } b && File.ReadAllText(b) == "old core");

        // Already identical: nothing to approve.
        var again = await new CoreStaging(_lib, gate, _paths).StageToFolderAsync(e, card);
        Assert.True(again.Staged);
        Assert.Equal(1, gate.Asked);
        Assert.Empty(Directory.EnumerateDirectories(_paths.Extracted, "stage-*"));
    }

    [Fact]
    public async Task Uploads_to_the_card_in_the_mega65_and_verifies_by_reading_back()
    {
        var e = await Core();
        var card = new FakeCard();
        var r = await new CoreStaging(_lib, new Gate(true), _paths).StageToDeviceAsync(e, card);
        Assert.True(r.Staged);
        Assert.Equal(File.ReadAllBytes(_lib.PathOf(e)), card.Files["galaga_r6.cor"]);

        var bad = await new CoreStaging(_lib, new Gate(true), _paths).StageToDeviceAsync((await Core("other.cor")) , new FakeCard(corruptReadBack: true));
        Assert.False(bad.Staged);
    }

    [Fact]
    public async Task Refuses_a_library_copy_that_has_changed()
    {
        var e = await Core();
        File.AppendAllText(_lib.PathOf(e), "tampered");
        await Assert.ThrowsAsync<IOException>(() => new CoreStaging(_lib, new Gate(true), _paths).StageToDeviceAsync(e, new FakeCard()));
    }
}

public class CoreSdFilesTests : IDisposable
{
    private readonly TempDir _tmp = new();
    public void Dispose() => _tmp.Dispose();

    /// <summary>Laid out like Vixen-32_Beta2_v0.76.zip once unpacked.</summary>
    private string VixenLayout()
    {
        var root = Path.Combine(_tmp.Path, "pkg", "Vixen-32_Beta2_v0.76");
        void Put(string rel, string text) { var p = Path.Combine(root, rel); Directory.CreateDirectory(Path.GetDirectoryName(p)!); File.WriteAllText(p, text); }
        Put("README.txt", "read me");
        Put("Vixen-32_User_Guide.pdf", "pdf");
        Put("vixen32-r6-v076.zip", "core archive");
        Put("vixen-32/BOOT.BIN", "boot");
        Put("vixen-32/img/Basic-A.img", "disk");
        Put("vixen-32/network/TERMINAL_B33.RVK", "terminal");
        Put("source/mega65/top.vhd", "vhdl");
        Put("cores/r6/core.cor", "core");
        return Path.Combine(_tmp.Path, "pkg");
    }

    [Fact]
    public void Finds_the_folder_meant_for_the_card_and_nothing_else()
    {
        var folders = CorePackageFiles.FindSdFolders(VixenLayout());
        var f = Assert.Single(folders);
        Assert.Equal("vixen-32", f.Name);
        Assert.Equal(3, f.FileCount);
    }

    /// <summary>The card inside the MEGA65 as an in-memory folder tree.</summary>
    private sealed class FakeTree(bool corrupt = false) : ICardTree
    {
        public Dictionary<string, byte[]> Files { get; } = new(StringComparer.OrdinalIgnoreCase);
        public HashSet<string> Dirs { get; } = new(StringComparer.OrdinalIgnoreCase) { "" };
        public List<string> Made { get; } = [];
        public string Description => "fake";
        public Task<IReadOnlyList<RemoteEntry>?> ListAsync(string dir, CancellationToken ct)
        {
            if (!Dirs.Contains(dir)) return Task.FromResult<IReadOnlyList<RemoteEntry>?>(null);
            string Child(string p) => p[(dir.Length == 0 ? 0 : dir.Length + 1)..];
            bool In(string p) => (dir.Length == 0 ? !p.Contains('/') : p.StartsWith(dir + "/", StringComparison.OrdinalIgnoreCase) && !Child(p).Contains('/')) && p.Length > 0;
            var list = Dirs.Where(In).Select(d => new RemoteEntry(Child(d).ToUpperInvariant(), Child(d), 0, true))
                .Concat(Files.Where(f => In(f.Key)).Select(f => new RemoteEntry(Child(f.Key).ToUpperInvariant(), Child(f.Key), f.Value.Length, false))).ToList();
            return Task.FromResult<IReadOnlyList<RemoteEntry>?>(list);
        }
        public Task MakeDirectoryAsync(string parent, string name, CancellationToken ct)
        {
            var d = parent.Length == 0 ? name : parent + "/" + name;
            Dirs.Add(d); Made.Add(d);
            return Task.CompletedTask;
        }
        public Task UploadAsync(string localPath, string dir, string name, CancellationToken ct)
        {
            Files[(dir.Length == 0 ? "" : dir + "/") + name] = File.ReadAllBytes(localPath);
            return Task.CompletedTask;
        }
        public Task DownloadAsync(string dir, RemoteEntry entry, string localPath, CancellationToken ct)
        {
            var d = (byte[])Files[(dir.Length == 0 ? "" : dir + "/") + entry.Name].Clone();
            if (corrupt) d[0] ^= 1;
            File.WriteAllBytes(localPath, d);
            return Task.CompletedTask;
        }
    }

    private string Stage()
    {
        var stage = Path.Combine(_tmp.Path, "stage");
        foreach (var (rel, text) in new[] { ("vixen-32/BOOT.BIN", "boot v2"), ("vixen-32/img/Basic-A.img", "disk"), ("vixen-32/README.TXT", "hi") })
        {
            var p = Path.Combine(stage, rel);
            Directory.CreateDirectory(Path.GetDirectoryName(p)!);
            File.WriteAllText(p, text);
        }
        return stage;
    }

    [Fact]
    public async Task Copies_a_folder_tree_to_the_card_keeping_its_layout_and_backing_up_replacements()
    {
        var card = new FakeTree();
        card.Dirs.Add("vixen-32");
        card.Files["vixen-32/BOOT.BIN"] = "boot v1"u8.ToArray();
        card.Files["vixen-32/README.TXT"] = "hi"u8.ToArray();
        card.Files["MEGA65.ROM"] = "rom"u8.ToArray();
        var sync = new CardTreeSync(card);
        var (plan, backup) = await sync.PlanAsync(Stage(), Path.Combine(_tmp.Path, "backups"));

        Assert.Equal(SyncAction.Replace, plan.Items.Single(i => i.RelativePath == "vixen-32/BOOT.BIN").Action);
        Assert.Equal(SyncAction.Unchanged, plan.Items.Single(i => i.RelativePath == "vixen-32/README.TXT").Action);
        Assert.Equal(SyncAction.Add, plan.Items.Single(i => i.RelativePath == "vixen-32/img/Basic-A.img").Action);
        Assert.DoesNotContain(plan.Items, i => i.RelativePath == "MEGA65.ROM"); // the rest of the card is not touched

        var result = await sync.ApplyAsync(plan, plan.Changes.Select(c => c.RelativePath).ToHashSet(), backup);
        Assert.True(result.AllSucceeded);
        Assert.Equal(["vixen-32/img"], card.Made);
        Assert.Equal("boot v2", Encoding.ASCII.GetString(card.Files["vixen-32/BOOT.BIN"]));
        Assert.Equal("disk", Encoding.ASCII.GetString(card.Files["vixen-32/img/Basic-A.img"]));
        Assert.Equal("boot v1", File.ReadAllText(Path.Combine(backup, "vixen-32", "BOOT.BIN")));
        Assert.True(card.Files.ContainsKey("MEGA65.ROM"));
    }

    [Fact]
    public async Task Creates_missing_folders_and_reports_a_bad_read_back()
    {
        var card = new FakeTree(corrupt: true);
        var sync = new CardTreeSync(card);
        var (plan, backup) = await sync.PlanAsync(Stage(), Path.Combine(_tmp.Path, "backups"));
        Assert.All(plan.Items, i => Assert.Equal(SyncAction.Add, i.Action));
        var result = await sync.ApplyAsync(plan, plan.Changes.Select(c => c.RelativePath).ToHashSet(), backup);
        Assert.Equal(["vixen-32", "vixen-32/img"], card.Made);
        Assert.All(result.Results, r => Assert.Equal(ItemOutcome.Failed, r.Outcome));
    }

    [Fact]
    public async Task Stages_sd_files_from_the_cores_package_to_a_card_folder()
    {
        var paths = new AppPaths(Path.Combine(_tmp.Path, "home"));
        var lib = new CoreLibrary(paths.CoreLibrary, paths.Extracted);
        var zip = Path.Combine(_tmp.Path, "Vixen-32_Beta2_v0.76.zip");
        ZipFile.CreateFromDirectory(VixenLayout(), zip);
        File.Delete(Path.Combine(_tmp.Path, "pkg", "Vixen-32_Beta2_v0.76", "cores", "r6", "core.cor"));
        var entry = (await lib.ImportAsync(_tmp.File("vixen.cor", CoreFixtures.MakeCor(CoreFixtures.MakeBit())), new CoreOrigin { SourcePath = zip })).Entry;
        var card = Directory.CreateDirectory(Path.Combine(_tmp.Path, "card")).FullName;

        var r = await new CoreStaging(lib, new ApproveAll(), paths).CopySdFilesToFolderAsync(entry, card);
        Assert.True(r.Staged, r.Message);
        Assert.Equal("boot", File.ReadAllText(Path.Combine(card, "vixen-32", "BOOT.BIN")));
        Assert.True(File.Exists(Path.Combine(card, "vixen-32", "network", "TERMINAL_B33.RVK")));
        Assert.False(File.Exists(Path.Combine(card, "README.txt")));
        Assert.Empty(Directory.EnumerateDirectories(paths.Extracted, "sdfiles-*"));

        // A core added on its own has no package to take files from.
        var alone = entry with { Origin = new CoreOrigin { SourcePath = Path.Combine(_tmp.Path, "vixen.cor") } };
        await Assert.ThrowsAsync<InvalidOperationException>(() => new CoreStaging(lib, new ApproveAll(), paths).CopySdFilesToFolderAsync(alone, card));
    }

    private sealed class ApproveAll : IApprovalGate
    {
        public Task<bool> ConfirmAsync(string title, string message, CancellationToken ct) => Task.FromResult(true);
        public Task<IReadOnlySet<string>?> ApproveChangesAsync(string title, SyncPlan plan, CancellationToken ct) =>
            Task.FromResult<IReadOnlySet<string>?>(plan.Changes.Select(c => c.RelativePath).ToHashSet());
    }
}

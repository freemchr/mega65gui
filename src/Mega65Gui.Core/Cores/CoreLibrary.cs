using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using Mega65Gui.Core.Device;
using Mega65Gui.Core.Packages;

namespace Mega65Gui.Core.Cores;

public enum CoreFileKind { Cor, Bit }

/// <summary>Where a library core came from.</summary>
public sealed record CoreOrigin
{
    public string? SourcePath { get; init; }
    public string? PackageFileName { get; init; }
    public string? FilehostFileId { get; init; }
    public string? FilehostVersionId { get; init; }
    public string? FilehostTitle { get; init; }
    public string? FilehostVersion { get; init; }
    public string? FilehostCategory { get; init; }
    public string? Author { get; init; }
    public string? Published { get; init; }
}

/// <summary>One core file in the library. The ID is the file's SHA-256, so the same file is only stored once.</summary>
public sealed record CoreLibraryEntry
{
    public required string Id { get; init; }
    public required string FileName { get; init; }
    public required CoreFileKind Kind { get; init; }
    public required long Size { get; init; }
    /// <summary>Relative to the library folder.</summary>
    public required string StoredPath { get; init; }
    public required string DisplayName { get; init; }
    public string? Description { get; init; }
    public string? Version { get; init; }
    public CoreFileInfo? Core { get; init; }
    public BitstreamInfo? Bitstream { get; init; }
    /// <summary>The board the user says a .bit file is for. Recorded as their claim, never as confirmed.</summary>
    public HardwareModel? DeclaredModel { get; init; }
    public CoreOrigin Origin { get; init; } = new();
    public bool Favorite { get; init; }
    public string? Category { get; init; }
    public IReadOnlyList<string> Tags { get; init; } = [];
    public string? Notes { get; init; }
    /// <summary>Relative to the library folder.</summary>
    public string? Artwork { get; init; }
    public DateTimeOffset ImportedAt { get; init; }
    public DateTimeOffset? LastRunAt { get; init; }

    [JsonIgnore] public string Sha256 => Id;

    [JsonIgnore]
    public CoreIdentity Identity => Core is { } c ? CoreIdentity.FromCore(c)
        : Bitstream is { } b ? CoreIdentity.FromBitstream(b, DeclaredModel)
        : new CoreIdentity(null, Provenance.NotKnown, null, null, false);

    /// <summary>Boards named in the file, package or Filehost title. Hints for searching only.</summary>
    [JsonIgnore] public IReadOnlyList<HardwareModel> NamedModels => CoreTargets.ModelsNamedIn(FileName, Origin.PackageFileName, Origin.FilehostTitle);
}

public sealed record CoreImportResult(CoreLibraryEntry Entry, bool WasDuplicate);

public sealed record PackageImportResult(IReadOnlyList<CoreImportResult> Imported, IReadOnlyList<(string File, string Reason)> Rejected);

public sealed record CoreLibraryQuery(string? Text = null, string? Category = null, bool FavoritesOnly = false, HardwareModel? Model = null, CoreFileKind? Kind = null);

/// <summary>
/// The Core Manager's local library. Files are copied into the library folder (checked by
/// SHA-256 after copying), metadata is kept in index.json, and every change is written
/// atomically so a crash cannot leave a half-written index.
/// </summary>
public sealed class CoreLibrary
{
    public const long MaxCoreFileSize = 64L * 1024 * 1024;
    public const long MaxArtworkSize = 20L * 1024 * 1024;
    public static readonly string[] ArtworkExtensions = [".png", ".jpg", ".jpeg", ".bmp", ".gif"];

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true, Converters = { new JsonStringEnumConverter() } };

    private readonly Lock _lock = new();
    private readonly string _root;
    private readonly string _extractRoot;
    private List<CoreLibraryEntry> _entries = [];
    private Dictionary<int, string> _slotNotes = [];

    public CoreLibrary(string root, string extractRoot)
    {
        _root = root;
        _extractRoot = extractRoot;
        Directory.CreateDirectory(root);
    }

    private sealed record IndexFile(int Version, List<CoreLibraryEntry> Entries, Dictionary<int, string>? SlotNotes);

    public string Root => _root;
    private string IndexPath => Path.Combine(_root, "index.json");

    /// <summary>Set when the index could not be read; the unreadable copy is kept beside it.</summary>
    public string? LoadWarning { get; private set; }

    public event Action? Changed;

    public IReadOnlyList<CoreLibraryEntry> Entries { get { lock (_lock) return [.. _entries]; } }

    /// <summary>Your own labels for the flash slots. They are notes, not a reading of the flash.</summary>
    public IReadOnlyDictionary<int, string> SlotNotes { get { lock (_lock) return new Dictionary<int, string>(_slotNotes); } }

    public void Load()
    {
        LoadWarning = null;
        if (!File.Exists(IndexPath)) { lock (_lock) { _entries = []; _slotNotes = []; } return; }
        try
        {
            var index = JsonSerializer.Deserialize<IndexFile>(File.ReadAllText(IndexPath), Json) ?? throw new JsonException("empty index");
            lock (_lock)
            {
                _entries = index.Entries.Where(e => e.Id.Length == 64).ToList();
                _slotNotes = index.SlotNotes ?? [];
            }
        }
        catch (JsonException e)
        {
            var bad = IndexPath + $".unreadable-{DateTime.Now:yyyyMMdd-HHmmss}";
            File.Copy(IndexPath, bad, overwrite: true);
            lock (_lock) { _entries = []; _slotNotes = []; }
            LoadWarning = $"The core library index could not be read ({e.Message}). A copy was kept as {Path.GetFileName(bad)}; your core files are still in the library folder.";
        }
    }

    public CoreLibraryEntry? Find(string id) { lock (_lock) return _entries.FirstOrDefault(e => e.Id == id); }

    public string PathOf(CoreLibraryEntry e) => Path.Combine(_root, e.StoredPath);
    public string? ArtworkPathOf(CoreLibraryEntry e) => e.Artwork is null ? null : Path.Combine(_root, e.Artwork);
    public bool FileExists(CoreLibraryEntry e) => File.Exists(PathOf(e));

    public IReadOnlyList<string> Categories
    {
        get { lock (_lock) return _entries.Select(e => e.Category).OfType<string>().Distinct(StringComparer.OrdinalIgnoreCase).Order().ToList(); }
    }

    public IEnumerable<CoreLibraryEntry> Search(CoreLibraryQuery q)
    {
        IEnumerable<CoreLibraryEntry> items = Entries;
        if (q.FavoritesOnly) items = items.Where(e => e.Favorite);
        if (q.Kind is { } kind) items = items.Where(e => e.Kind == kind);
        if (!string.IsNullOrWhiteSpace(q.Category)) items = items.Where(e => string.Equals(e.Category, q.Category, StringComparison.OrdinalIgnoreCase));
        if (q.Model is { } model) items = items.Where(e => e.Identity.Model == model || e.NamedModels.Contains(model));
        if (!string.IsNullOrWhiteSpace(q.Text))
        {
            var terms = q.Text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            items = items.Where(e => terms.All(t => SearchText(e).Contains(t, StringComparison.OrdinalIgnoreCase)));
        }
        return items.OrderByDescending(e => e.Favorite).ThenBy(e => e.DisplayName, StringComparer.OrdinalIgnoreCase);
    }

    private static string SearchText(CoreLibraryEntry e) => string.Join('\n',
        new[] { e.DisplayName, e.FileName, e.Description, e.Version, e.Category, e.Notes, e.Core?.CoreName, e.Core?.CoreVersion, e.Core?.Target,
                e.Origin.FilehostTitle, e.Origin.Author }.OfType<string>().Concat(e.Tags));

    /// <summary>
    /// Imports one .cor or .bit file. The header is parsed and checked first: a malformed file or
    /// one that fails its CRC check is refused. A file already in the library is not stored twice.
    /// </summary>
    public async Task<CoreImportResult> ImportAsync(string sourcePath, CoreOrigin? origin = null, CancellationToken ct = default)
    {
        var kind = Path.GetExtension(sourcePath).ToLowerInvariant() switch
        {
            ".cor" => CoreFileKind.Cor,
            ".bit" => CoreFileKind.Bit,
            _ => throw new InvalidDataException($"{Path.GetFileName(sourcePath)}: only .cor and .bit core files can be added to the library."),
        };
        var length = new FileInfo(sourcePath).Length;
        if (length > MaxCoreFileSize) throw new InvalidDataException($"{Path.GetFileName(sourcePath)} is {length / 1048576} MB, which is too large to be a MEGA65 core.");

        var data = await File.ReadAllBytesAsync(sourcePath, ct);
        CoreFileInfo? core = null;
        BitstreamInfo? bit = null;
        if (kind == CoreFileKind.Cor)
        {
            core = CoreFile.Parse(data);
            if (!core.SizeFits) throw new InvalidDataException($"{Path.GetFileName(sourcePath)}: the size in the core header does not match the file, so it was not added.");
            if (!core.CrcMatches) throw new InvalidDataException($"{Path.GetFileName(sourcePath)} is damaged: its CRC32 does not match (stored {core.StoredCrc:X8}, calculated {core.ComputedCrc:X8}), so it was not added.");
        }
        else
        {
            bit = BitstreamFile.Parse(data);
            if (!bit.HasSyncWord) throw new InvalidDataException($"{Path.GetFileName(sourcePath)}: no FPGA configuration data was found after the header, so it was not added.");
            if (!bit.LengthFits) throw new InvalidDataException($"{Path.GetFileName(sourcePath)} is truncated: the header lists more data than the file holds, so it was not added.");
        }

        var id = Convert.ToHexStringLower(SHA256.HashData(data));
        origin ??= new CoreOrigin { SourcePath = Path.GetFullPath(sourcePath) };
        if (Find(id) is { } existing)
        {
            // Keep the Filehost link if the same file is downloaded after a manual import.
            if (existing.Origin.FilehostFileId is null && origin.FilehostFileId is not null)
                existing = Replace(existing with { Origin = origin });
            return new CoreImportResult(existing, WasDuplicate: true);
        }

        var fileName = SafeFileName(Path.GetFileName(sourcePath));
        var relDir = Path.Combine("files", id[..16]);
        var dir = Path.Combine(_root, relDir);
        Directory.CreateDirectory(dir);
        var final = Path.Combine(dir, fileName);
        var part = final + ".part";
        try
        {
            await File.WriteAllBytesAsync(part, data, ct);
            if (await Common.Hashing.Sha256FileAsync(part, ct) != id) throw new IOException("The copy in the library does not match the original file.");
            File.Move(part, final, overwrite: true);
        }
        catch
        {
            TryDelete(part);
            if (!Directory.EnumerateFileSystemEntries(dir).Any()) Directory.Delete(dir);
            throw;
        }

        var entry = new CoreLibraryEntry
        {
            Id = id,
            FileName = fileName,
            Kind = kind,
            Size = data.Length,
            StoredPath = Path.Combine(relDir, fileName),
            DisplayName = DefaultName(core, origin, fileName),
            Version = core?.CoreVersion is { Length: > 0 } v ? v : origin.FilehostVersion,
            Core = core,
            Bitstream = bit,
            Origin = origin,
            Category = origin.FilehostCategory,
            ImportedAt = DateTimeOffset.Now,
        };
        lock (_lock) _entries.Add(entry);
        Save();
        return new CoreImportResult(entry, WasDuplicate: false);
    }

    /// <summary>Imports every .cor and .bit in an archive (for example a Filehost download). Files that fail their checks are listed, not imported.</summary>
    public async Task<PackageImportResult> ImportPackageAsync(string archivePath, CoreOrigin? origin = null, CancellationToken ct = default)
    {
        var folder = Path.Combine(_extractRoot, $"import-{Guid.NewGuid():N}");
        try
        {
            await PackageExtractor.ExtractAsync(archivePath, folder, ct);
            // Some packages keep the core in a zip inside the zip (Vixen-32: vixen32-r6-v076.zip).
            // Nested archives are opened too, at most two levels deep.
            var rejected = new List<(string, string)>();
            for (var depth = 0; depth < 2; depth++)
            {
                var nested = Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories).Where(PackageExtractor.IsArchive).ToList();
                if (nested.Count == 0) break;
                foreach (var inner in nested)
                {
                    ct.ThrowIfCancellationRequested();
                    try { await PackageExtractor.ExtractAsync(inner, inner + ".contents", ct); }
                    catch (Exception e) when (e is not OperationCanceledException)
                    {
                        rejected.Add((Path.GetRelativePath(folder, inner), $"The archive inside the package could not be opened: {e.Message}"));
                    }
                    File.Delete(inner);
                }
            }
            var imported = new List<CoreImportResult>();
            var files = Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories)
                .Where(f => Path.GetExtension(f).ToLowerInvariant() is ".cor" or ".bit")
                .Order(StringComparer.OrdinalIgnoreCase);
            foreach (var f in files)
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    var o = (origin ?? new CoreOrigin()) with { PackageFileName = Path.GetFileName(archivePath), SourcePath = origin?.SourcePath ?? Path.GetFullPath(archivePath) };
                    imported.Add(await ImportAsync(f, o, ct));
                }
                catch (InvalidDataException e) { rejected.Add((Path.GetRelativePath(folder, f), e.Message)); }
            }
            return new PackageImportResult(imported, rejected);
        }
        finally
        {
            if (Directory.Exists(folder)) Directory.Delete(folder, recursive: true);
        }
    }

    /// <summary>Changes the user's metadata. Names, tags and notes are trimmed; the file itself never changes.</summary>
    public CoreLibraryEntry Update(string id, Func<CoreLibraryEntry, CoreLibraryEntry> change)
    {
        var current = Find(id) ?? throw new KeyNotFoundException("That core is no longer in the library.");
        var next = change(current);
        if (next.Id != current.Id || next.StoredPath != current.StoredPath || next.Size != current.Size || next.Kind != current.Kind)
            throw new InvalidOperationException("Only a core's descriptive details can be changed.");
        var name = next.DisplayName.Trim();
        if (name.Length is 0 or > 80) throw new ArgumentException("The name must be 1 to 80 characters.");
        next = next with
        {
            DisplayName = name,
            Category = string.IsNullOrWhiteSpace(next.Category) ? null : next.Category.Trim(),
            Notes = string.IsNullOrWhiteSpace(next.Notes) ? null : next.Notes.Trim(),
            Tags = next.Tags.Select(t => t.Trim()).Where(t => t.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
        };
        return Replace(next);
    }

    /// <summary>Parses a comma or semicolon separated tag list.</summary>
    public static IReadOnlyList<string> ParseTags(string? text) =>
        (text ?? "").Split([',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

    public void Remove(string id)
    {
        var e = Find(id) ?? throw new KeyNotFoundException("That core is no longer in the library.");
        lock (_lock) _entries.RemoveAll(x => x.Id == id);
        Save();
        var dir = Path.GetDirectoryName(PathOf(e))!;
        if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        if (ArtworkPathOf(e) is { } art) TryDelete(art);
    }

    /// <summary>True when the stored file still has the SHA-256 it was imported with.</summary>
    public async Task<bool> VerifyAsync(CoreLibraryEntry e, CancellationToken ct = default) =>
        FileExists(e) && await Common.Hashing.Sha256FileAsync(PathOf(e), ct) == e.Id;

    /// <summary>Copies a core out of the library, checking the copy before it takes the destination's place.</summary>
    public async Task ExportAsync(string id, string destination, CancellationToken ct = default)
    {
        var e = Find(id) ?? throw new KeyNotFoundException("That core is no longer in the library.");
        if (!await VerifyAsync(e, ct)) throw new IOException($"The library copy of {e.FileName} is missing or has changed since it was imported.");
        var part = destination + ".part";
        try
        {
            await using (var src = File.OpenRead(PathOf(e)))
            await using (var dst = File.Create(part))
                await src.CopyToAsync(dst, ct);
            if (await Common.Hashing.Sha256FileAsync(part, ct) != e.Id) throw new IOException("The exported copy does not match the library copy.");
            File.Move(part, destination, overwrite: true);
        }
        catch
        {
            TryDelete(part);
            throw;
        }
    }

    public async Task<CoreLibraryEntry> SetArtworkAsync(string id, string imagePath, CancellationToken ct = default)
    {
        var e = Find(id) ?? throw new KeyNotFoundException("That core is no longer in the library.");
        var ext = Path.GetExtension(imagePath).ToLowerInvariant();
        if (!ArtworkExtensions.Contains(ext)) throw new InvalidDataException("Artwork must be a PNG, JPEG, BMP or GIF image.");
        if (new FileInfo(imagePath).Length > MaxArtworkSize) throw new InvalidDataException("That image is larger than 20 MB.");
        var rel = Path.Combine("art", e.Id[..16] + ext);
        var full = Path.Combine(_root, rel);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        if (ArtworkPathOf(e) is { } old && !string.Equals(old, full, StringComparison.OrdinalIgnoreCase)) TryDelete(old);
        await using (var src = File.OpenRead(imagePath))
        await using (var dst = File.Create(full))
            await src.CopyToAsync(dst, ct);
        return Replace(e with { Artwork = rel });
    }

    public CoreLibraryEntry ClearArtwork(string id)
    {
        var e = Find(id) ?? throw new KeyNotFoundException("That core is no longer in the library.");
        if (ArtworkPathOf(e) is { } art) TryDelete(art);
        return Replace(e with { Artwork = null });
    }

    public CoreLibraryEntry MarkRun(string id) => Replace((Find(id) ?? throw new KeyNotFoundException()) with { LastRunAt = DateTimeOffset.Now });

    public void SetSlotNote(int slot, string? note)
    {
        if (slot is < 0 or > 7) throw new ArgumentOutOfRangeException(nameof(slot));
        lock (_lock)
        {
            if (string.IsNullOrWhiteSpace(note)) _slotNotes.Remove(slot);
            else _slotNotes[slot] = note.Trim();
        }
        Save();
    }

    private CoreLibraryEntry Replace(CoreLibraryEntry next)
    {
        lock (_lock)
        {
            var i = _entries.FindIndex(x => x.Id == next.Id);
            if (i < 0) throw new KeyNotFoundException("That core is no longer in the library.");
            _entries[i] = next;
        }
        Save();
        return next;
    }

    private void Save()
    {
        string json;
        lock (_lock) json = JsonSerializer.Serialize(new IndexFile(1, _entries, _slotNotes), Json);
        var tmp = IndexPath + ".tmp";
        File.WriteAllText(tmp, json);
        File.Move(tmp, IndexPath, overwrite: true);
        Changed?.Invoke();
    }

    private static string DefaultName(CoreFileInfo? core, CoreOrigin origin, string fileName)
    {
        var stem = Path.GetFileNameWithoutExtension(fileName);
        // Official cores all call themselves "MEGA65"; the version tells them apart.
        if (core is { CoreName.Length: > 0 } c && !string.Equals(c.CoreName, "MEGA65", StringComparison.OrdinalIgnoreCase)) return Trim(c.CoreName);
        if (core is { CoreName.Length: > 0 }) return Trim($"MEGA65 {core.CoreVersion}".Trim());
        return Trim(origin.FilehostTitle ?? stem);
    }

    private static string Trim(string s) => s.Length > 80 ? s[..80] : s;

    internal static string SafeFileName(string name)
    {
        var clean = string.Concat(name.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c)).Trim().TrimEnd('.');
        return clean.Length == 0 ? "core.cor" : clean;
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }
}

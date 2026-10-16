using SharpCompress.Archives;

namespace Mega65Gui.Core.Packages;

/// <summary>
/// Extracts .7z / .zip release packages into a fresh folder. Entries that would escape the
/// destination (zip-slip) are rejected rather than skipped, because a package that tries this
/// should not be trusted.
/// </summary>
public static class PackageExtractor
{
    public static readonly string[] SupportedExtensions = [".7z", ".zip", ".tar", ".gz", ".tgz"];

    public static bool IsArchive(string path) =>
        SupportedExtensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase);

    public static async Task<string> ExtractAsync(string archivePath, string destination, CancellationToken ct = default)
    {
        if (Directory.Exists(destination)) Directory.Delete(destination, recursive: true);
        Directory.CreateDirectory(destination);
        var root = Path.GetFullPath(destination) + Path.DirectorySeparatorChar;

        await Task.Run(() =>
        {
            using var archive = ArchiveFactory.OpenArchive(archivePath);
            foreach (var entry in archive.Entries.Where(e => !e.IsDirectory))
            {
                ct.ThrowIfCancellationRequested();
                var key = (entry.Key ?? "").Replace('\\', '/').TrimStart('/');
                var target = Path.GetFullPath(Path.Combine(root, key));
                if (!target.StartsWith(root, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException($"Archive entry '{entry.Key}' points outside the extraction folder.");
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                using var input = entry.OpenEntryStream();
                using var output = File.Create(target);
                input.CopyTo(output);
            }
        }, ct);
        return destination;
    }
}

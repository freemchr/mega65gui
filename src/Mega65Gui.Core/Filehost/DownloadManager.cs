using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Mega65Gui.Core.Common;

namespace Mega65Gui.Core.Filehost;

/// <summary>Sidecar written next to every cached download. The Filehost publishes no checksums, so we record our own.</summary>
public sealed record DownloadManifest(string FileId, string VersionId, string FileName, long Size, string Sha256,
    string SourceUrl, string? ETag, DateTimeOffset DownloadedAt);

public sealed record DownloadProgress(long Received, long? Total);

public sealed class DownloadManager(HttpClient http, AppPaths paths)
{
    public string FolderFor(string versionId) => Path.Combine(paths.Downloads, versionId);

    public DownloadManifest? TryGetCached(string versionId)
    {
        var manifest = Path.Combine(FolderFor(versionId), "manifest.json");
        if (!File.Exists(manifest)) return null;
        var m = JsonSerializer.Deserialize<DownloadManifest>(File.ReadAllText(manifest));
        if (m is null) return null;
        var file = new FileInfo(Path.Combine(FolderFor(versionId), m.FileName));
        return file.Exists && file.Length == m.Size ? m : null;
    }

    public string PathOf(DownloadManifest m) => Path.Combine(FolderFor(m.VersionId), m.FileName);

    /// <summary>Downloads a version into the cache, resuming a partial file and verifying size and SHA-256.</summary>
    public async Task<DownloadManifest> DownloadAsync(FilehostVersionRow version, IProgress<DownloadProgress>? progress = null,
        CancellationToken ct = default)
    {
        if (TryGetCached(version.VersionId) is { } cached && await VerifyAsync(cached, ct)) return cached;

        var url = FilehostClient.ResolveDownloadUri(version) ?? throw new InvalidOperationException("Version has no download location.");
        var dir = FolderFor(version.VersionId);
        Directory.CreateDirectory(dir);
        var safeName = SanitiseFileName(version.FileName);
        var final = Path.Combine(dir, safeName);
        var part = final + ".part";

        long existing = File.Exists(part) ? new FileInfo(part).Length : 0;
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        if (existing > 0) req.Headers.Range = new RangeHeaderValue(existing, null);

        using var resp = await http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
        if (existing > 0 && resp.StatusCode != HttpStatusCode.PartialContent) existing = 0; // server ignored range: restart
        resp.EnsureSuccessStatusCode();

        long? total = resp.Content.Headers.ContentLength is { } len ? len + existing : version.SizeBytes;
        await using (var src = await resp.Content.ReadAsStreamAsync(ct))
        await using (var dst = new FileStream(part, existing > 0 ? FileMode.Append : FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16, true))
        {
            var buffer = new byte[1 << 16];
            long received = existing;
            int n;
            while ((n = await src.ReadAsync(buffer, ct)) > 0)
            {
                await dst.WriteAsync(buffer.AsMemory(0, n), ct);
                received += n;
                progress?.Report(new DownloadProgress(received, total));
            }
        }

        var size = new FileInfo(part).Length;
        if (version.SizeBytes is { } expected && expected != size)
        {
            File.Delete(part);
            throw new InvalidDataException($"Downloaded {size:N0} bytes but the Filehost lists {expected:N0}.");
        }
        File.Move(part, final, overwrite: true);

        var manifest = new DownloadManifest(version.FileId, version.VersionId, safeName, size,
            await Hashing.Sha256FileAsync(final, ct), url.ToString(), resp.Headers.ETag?.Tag, DateTimeOffset.Now);
        await File.WriteAllTextAsync(Path.Combine(dir, "manifest.json"), JsonSerializer.Serialize(manifest), ct);
        return manifest;
    }

    public async Task<bool> VerifyAsync(DownloadManifest m, CancellationToken ct = default)
    {
        var path = PathOf(m);
        return File.Exists(path) && string.Equals(await Hashing.Sha256FileAsync(path, ct), m.Sha256, StringComparison.Ordinal);
    }

    internal static string SanitiseFileName(string name)
    {
        var clean = string.Concat(Path.GetFileName(name).Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c)).TrimEnd('.', ' ');
        return string.IsNullOrEmpty(clean) ? "download.bin" : clean;
    }
}

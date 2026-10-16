using System.Text.Json;
using Mega65Gui.Core.Common;

namespace Mega65Gui.Core.Filehost;

public sealed record CatalogueSnapshot(IReadOnlyList<FilehostFile> Files, DateTimeOffset FetchedAt, bool FromCache, string? StaleReason);

public sealed record CatalogueQuery(string? Text = null, string? Category = null, string? Platform = null, bool OfficialOnly = false);

/// <summary>Catalogue with an on-disk cache. Falls back to the last good copy when offline.</summary>
/// <param name="anonymousClient">
/// Client without the sign-in cookie, used for the catalogue list. A signed-in list also contains
/// owner-only packages; fetching it anonymously keeps the on-disk cache to public files only.
/// Defaults to <paramref name="client"/>.
/// </param>
public sealed class CatalogueService(FilehostClient client, AppPaths paths, TimeProvider? clock = null, FilehostSession? session = null,
    FilehostClient? anonymousClient = null)
{
    public static readonly TimeSpan CatalogueTtl = TimeSpan.FromHours(6);
    public static readonly TimeSpan VersionTtl = TimeSpan.FromHours(6);
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;
    private CatalogueSnapshot? _current;

    private string CatalogueFile => Path.Combine(paths.CatalogueCache, "filespublic.json");

    public CatalogueSnapshot? Current => _current;

    public async Task<CatalogueSnapshot> LoadAsync(bool forceRefresh = false, CancellationToken ct = default)
    {
        var cached = File.Exists(CatalogueFile) ? new FileInfo(CatalogueFile) : null;
        var age = cached is null ? TimeSpan.MaxValue : _clock.GetUtcNow() - cached.LastWriteTimeUtc;

        if (!forceRefresh && cached is not null && age < CatalogueTtl)
            return _current = new CatalogueSnapshot(FilehostClient.ParseCatalogue(await File.ReadAllTextAsync(CatalogueFile, ct)),
                cached.LastWriteTimeUtc, FromCache: true, StaleReason: null);

        try
        {
            var json = await (anonymousClient ?? client).GetCatalogueJsonAsync(ct);
            var files = FilehostClient.ParseCatalogue(json); // validate before replacing the cache
            await WriteAtomicAsync(CatalogueFile, json, ct);
            return _current = new CatalogueSnapshot(files, _clock.GetUtcNow(), FromCache: false, StaleReason: null);
        }
        catch (Exception e) when (cached is not null && e is HttpRequestException or TaskCanceledException or JsonException && !ct.IsCancellationRequested)
        {
            return _current = new CatalogueSnapshot(FilehostClient.ParseCatalogue(await File.ReadAllTextAsync(CatalogueFile, ct)),
                cached.LastWriteTimeUtc, FromCache: true, StaleReason: e.Message);
        }
    }

    public IEnumerable<FilehostFile> Search(CatalogueQuery q)
    {
        IEnumerable<FilehostFile> files = _current?.Files ?? [];
        if (q.OfficialOnly) files = files.Where(f => string.Equals(f.Author?.Trim(), "MEGA65", StringComparison.OrdinalIgnoreCase));
        if (!string.IsNullOrWhiteSpace(q.Category)) files = files.Where(f => string.Equals(f.Category, q.Category, StringComparison.OrdinalIgnoreCase));
        if (!string.IsNullOrWhiteSpace(q.Platform)) files = files.Where(f => f.Platforms.Contains(q.Platform, StringComparer.OrdinalIgnoreCase));
        if (!string.IsNullOrWhiteSpace(q.Text))
        {
            var terms = q.Text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            files = files.Where(f => terms.All(t =>
                f.Title.Contains(t, StringComparison.OrdinalIgnoreCase)
                || (f.Author?.Contains(t, StringComparison.OrdinalIgnoreCase) ?? false)
                || (f.Search?.Contains(t, StringComparison.OrdinalIgnoreCase) ?? false)));
        }
        return files.OrderByDescending(f => f.Updated);
    }

    public IReadOnlyList<string> Categories =>
        (_current?.Files ?? []).Select(f => f.Category).OfType<string>().Distinct(StringComparer.OrdinalIgnoreCase).Order().ToList();

    public bool IsPublic(string fileId) => _current?.Files.Any(f => f.FileId == fileId) ?? false;

    public FilehostSession? Session => session;

    /// <summary>
    /// Version history. Public files are cached. Any other file is only queried when the user is
    /// signed in and the server confirms, through readfiledetail, that their account can see it.
    /// Owner-only results are never written to the disk cache.
    /// </summary>
    public async Task<IReadOnlyList<FilehostVersionRow>> GetVersionsAsync(string fileId, bool forceRefresh = false, CancellationToken ct = default)
    {
        if (!IsPublic(fileId))
        {
            var detail = await GetRestrictedDetailAsync(fileId, ct);
            if (detail is not { IsAvailable: true })
                throw new UnauthorizedAccessException("Your Filehost account does not have access to this file.");
            return await client.GetVersionsAsync(fileId, ct);
        }

        var cacheFile = Path.Combine(paths.CatalogueCache, $"versions-{fileId}.json");
        var info = new FileInfo(cacheFile);
        if (!forceRefresh && info.Exists && _clock.GetUtcNow() - info.LastWriteTimeUtc < VersionTtl)
            return FilehostClient.ParseVersionList(await File.ReadAllTextAsync(cacheFile, ct));

        try
        {
            var rows = await client.GetVersionsAsync(fileId, ct);
            await WriteAtomicAsync(cacheFile, JsonSerializer.Serialize(rows), ct);
            return rows;
        }
        catch (HttpRequestException) when (info.Exists)
        {
            return FilehostClient.ParseVersionList(await File.ReadAllTextAsync(cacheFile, ct));
        }
    }

    public async Task<FilehostFileDetail?> GetDetailAsync(string fileId, CancellationToken ct = default)
    {
        if (!IsPublic(fileId)) return await GetRestrictedDetailAsync(fileId, ct);

        var cacheFile = Path.Combine(paths.CatalogueCache, $"detail-{fileId}.json");
        try
        {
            var d = await client.GetFileDetailAsync(fileId, ct);
            if (d is { IsAvailable: true }) await WriteAtomicAsync(cacheFile, JsonSerializer.Serialize(new[] { d }), ct);
            return d;
        }
        catch (HttpRequestException) when (File.Exists(cacheFile))
        {
            return FilehostClient.ParseFileDetail(await File.ReadAllTextAsync(cacheFile, ct));
        }
    }

    private async Task<FilehostFileDetail?> GetRestrictedDetailAsync(string fileId, CancellationToken ct)
    {
        if (session is not { IsSignedIn: true })
            throw new UnauthorizedAccessException("This file is not public. Sign in to the Filehost with your account to check access.");
        var d = await client.GetFileDetailAsync(fileId, ct);
        if (FilehostSession.IsExpiredState(d?.State))
        {
            session.MarkExpired();
            throw new UnauthorizedAccessException("Your Filehost session has expired. Please sign in again.");
        }
        return d;
    }

    private static async Task WriteAtomicAsync(string path, string content, CancellationToken ct)
    {
        var tmp = path + ".tmp";
        await File.WriteAllTextAsync(tmp, content, ct);
        File.Move(tmp, path, overwrite: true);
    }
}

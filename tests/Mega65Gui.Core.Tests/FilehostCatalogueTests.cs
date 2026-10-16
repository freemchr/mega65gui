using System.Net;
using Mega65Gui.Core.Common;
using Mega65Gui.Core.Filehost;

namespace Mega65Gui.Core.Tests;

public sealed class FilehostCatalogueTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("m65cat").FullName;
    public void Dispose() => Directory.Delete(_root, recursive: true);

    internal static string Fixture(string name) => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", name));

    [Fact]
    public void ParsesCatalogueAndNormalisesFields()
    {
        var files = FilehostClient.ParseCatalogue(Fixture("filespublic-sample.json"));
        Assert.NotEmpty(files);
        var r6 = files.Single(f => f.FileId == "764642f7-83bc-4162-9911-99a0a6283d08");
        Assert.True(OfficialReleases.IsStableCorePackage(r6));
        Assert.Equal("mega65r6", OfficialReleases.TargetFromTitle(r6.Title));
        Assert.DoesNotContain(files.Where(f => f.Title.Contains("Development")), OfficialReleases.IsStableCorePackage);
        Assert.All(files, f => Assert.All(f.Platforms, p => Assert.Equal(p.Trim(), p)));
    }

    [Fact]
    public void DetailReportsRestrictedFilesAsUnavailable()
    {
        Assert.False(FilehostClient.ParseFileDetail(Fixture("filedetail-empty.json"))!.IsAvailable);
        var r6 = FilehostClient.ParseFileDetail(Fixture("filedetail-r6.json"))!;
        Assert.True(r6.IsAvailable);
        Assert.Equal("CoreMEGA65R6norom", r6.LinkId);
    }

    [Theory]
    [InlineData("mega65r3-release-0.97-aaf4542-norom.7z", null, "mega65r3", "0.97", false)]
    [InlineData("mega65r6-release-0.97.2-03b24c6-norom.7z", null, "mega65r6", "0.97.2", false)]
    [InlineData("MEGA65-R3-Rel_0_9-norom.7z", null, "mega65r3", "0.9", false)]
    [InlineData("odd-name.7z", "Release 0.96 - Batch 3", "unknown", "0.96", true)]
    public void ParsesReleaseNumbersFromPackageNames(string file, string? comment, string target, string version, bool rom)
    {
        var info = ReleaseInfo.TryParse(file, comment)!;
        Assert.Equal(target, info.Target);
        Assert.Equal(Version.Parse(version), info.Release);
        Assert.Equal(rom, info.IncludesRom);
    }

    [Fact]
    public void VersionHistoryOrdersReleases()
    {
        var rows = FilehostClient.ParseVersionList(Fixture("versions-r3.json"));
        var latest = rows.Where(r => r.IsActive).Select(r => ReleaseInfo.TryParse(r.FileName, r.Comment)).Max(r => r!.Release);
        Assert.Equal(new Version(0, 97), latest);
    }

    [Fact]
    public async Task CatalogueUsesCacheAndRefusesNonPublicVersionLookups()
    {
        var handler = new StubHandler(Fixture("filespublic-sample.json"));
        var svc = new CatalogueService(new FilehostClient(new HttpClient(handler)), new AppPaths(_root));

        var first = await svc.LoadAsync();
        Assert.False(first.FromCache);
        var second = await svc.LoadAsync();
        Assert.True(second.FromCache);
        Assert.Equal(1, handler.Calls);

        Assert.NotEmpty(svc.Search(new CatalogueQuery(Text: "mega65r6", OfficialOnly: true)));
        await Assert.ThrowsAsync<InvalidOperationException>(() => svc.GetVersionsAsync("a0276005-e71c-4b2d-8d17-2aa92e492c50"));
    }

    [Fact]
    public async Task OfflineFallsBackToStaleCache()
    {
        var paths = new AppPaths(_root);
        await new CatalogueService(new FilehostClient(new HttpClient(new StubHandler(Fixture("filespublic-sample.json")))), paths).LoadAsync();
        var offline = new CatalogueService(new FilehostClient(new HttpClient(new StubHandler(null))), paths);
        var snap = await offline.LoadAsync(forceRefresh: true);
        Assert.True(snap.FromCache);
        Assert.NotNull(snap.StaleReason);
    }

    private sealed class StubHandler(string? body) : HttpMessageHandler
    {
        public int Calls;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Calls++;
            if (body is null) throw new HttpRequestException("offline");
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body) });
        }
    }
}

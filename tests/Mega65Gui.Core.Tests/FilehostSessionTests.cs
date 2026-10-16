using System.Net;
using System.Text.Json;
using Mega65Gui.Core.Common;
using Mega65Gui.Core.Device;
using Mega65Gui.Core.Filehost;
using Mega65Gui.Core.Sd;
using Mega65Gui.Core.Update;

namespace Mega65Gui.Core.Tests;

public sealed class FilehostSessionTests : IDisposable
{
    private const string PublicR6 = "764642f7-83bc-4162-9911-99a0a6283d08";
    private const string PrivateR6 = "5fddec06-abc1-46a8-8990-342c8d84e11f";
    private readonly string _root = Directory.CreateTempSubdirectory("m65sess").FullName;
    public void Dispose() => Directory.Delete(_root, recursive: true);

    /// <summary>Simulates the Filehost: the private package is only visible to a signed-in session cookie.</summary>
    private sealed class FakeFilehost : HttpMessageHandler
    {
        public string? LastLoginBody;
        public bool ExpireSession;
        private readonly HashSet<string> _sessions = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage req, CancellationToken ct)
        {
            var path = req.RequestUri!.AbsolutePath;
            var body = req.Content is null ? "" : await req.Content.ReadAsStringAsync(ct);
            var cookie = req.Headers.TryGetValues("Cookie", out var c) ? string.Join(";", c) : "";
            var signedIn = _sessions.Any(s => cookie.Contains(s));

            switch (path)
            {
                case "/php/login.php":
                    LastLoginBody = body;
                    if (!body.Contains("logindata%5Bpassword%5D=correct")) return Json("""{"state":"NOTOK","message":"Wrong password"}""");
                    var r = Json("""{"state":"OK"}""");
                    _sessions.Add("PHPSESSID=s1");
                    r.Headers.Add("Set-Cookie", "PHPSESSID=s1; path=/");
                    return r;
                case "/php/logout.php":
                    _sessions.Clear();
                    return Json("""{"state":"OK"}""");
                case "/php/readfilespublic.php":
                    return Json(FilehostCatalogueTests.Fixture("filespublic-sample.json"));
                case "/php/readfiledetail.php" when body.Contains(PrivateR6):
                    if (ExpireSession) return Json("""[{"state":"TIMEOUT"}]""");
                    return Json(signedIn
                        ? $$"""[{"state":"OK","fileid":"{{PrivateR6}}","title":"MEGA65 Core Release Package (mega65r6)","author":"MEGA65"}]"""
                        : """[{"state":"EMPTY"}]""");
                case "/php/readfiledetail.php":
                    return Json(FilehostCatalogueTests.Fixture("filedetail-r6.json"));
                case "/php/readversionlistpublic.php" when body.Contains(PrivateR6):
                    return Json(JsonSerializer.Serialize(new[] { new { fileid = PrivateR6, version = "2.1", versionid = "rom21",
                        filename = "mega65r6-release-0.97.2-03b24c6.7z", size = "10", comment = "Release 0.97.2", published = "28 Sep 2025",
                        active = "Y", location = "../files/m/rom.7z" } }));
                case "/php/readversionlistpublic.php":
                    return Json(JsonSerializer.Serialize(new[] { new { fileid = PublicR6, version = "2.1", versionid = "pub21",
                        filename = "mega65r6-release-0.97.2-03b24c6-norom.7z", size = "10", comment = "Release 0.97.2", published = "28 Sep 2025",
                        active = "Y", location = "../files/m/pub.7z" } }));
            }
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        }

        private static HttpResponseMessage Json(string s) => new(HttpStatusCode.OK) { Content = new StringContent(s) };
    }

    private (FakeFilehost Server, FilehostSession Session, CatalogueService Catalogue) Create()
    {
        var server = new FakeFilehost();
        var http = new HttpClient(new CookieHandler(server));
        var session = new FilehostSession(http);
        var catalogue = new CatalogueService(new FilehostClient(http), new AppPaths(_root), session: session);
        return (server, session, catalogue);
    }

    /// <summary>Minimal cookie jar so the fake server sees the session cookie, like HttpClientHandler.CookieContainer.</summary>
    private sealed class CookieHandler(HttpMessageHandler inner) : DelegatingHandler(inner)
    {
        private readonly CookieContainer _jar = new();
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage req, CancellationToken ct)
        {
            var header = _jar.GetCookieHeader(req.RequestUri!);
            if (header.Length > 0) req.Headers.Add("Cookie", header);
            var resp = await base.SendAsync(req, ct);
            if (resp.Headers.TryGetValues("Set-Cookie", out var set))
                foreach (var s in set) _jar.SetCookies(req.RequestUri!, s);
            return resp;
        }
    }

    [Fact]
    public async Task SignInSendsTheSitesFormFieldsAndReportsBadPasswords()
    {
        var (server, session, _) = Create();
        var bad = await session.SignInAsync("chris", "wrong");
        Assert.Equal(SignInResult.InvalidCredentials, bad.Result);
        Assert.False(session.IsSignedIn);
        Assert.Contains("logindata%5Busername%5D=chris", server.LastLoginBody);

        var ok = await session.SignInAsync("chris", "correct");
        Assert.Equal(SignInResult.Success, ok.Result);
        Assert.Equal("chris", session.UserName);
    }

    [Fact]
    public async Task RestrictedFilesNeedSignInAndServerConfirmation()
    {
        var (_, session, catalogue) = Create();
        await catalogue.LoadAsync();

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => catalogue.GetVersionsAsync(PrivateR6));

        await session.SignInAsync("chris", "correct");
        var versions = await catalogue.GetVersionsAsync(PrivateR6);
        Assert.Equal("rom21", Assert.Single(versions).VersionId);
        Assert.Empty(Directory.EnumerateFiles(Path.Combine(_root, "cache", "catalogue"), $"*{PrivateR6}*"));

        await session.SignOutAsync();
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => catalogue.GetVersionsAsync(PrivateR6));
    }

    [Fact]
    public async Task ExpiredSessionSignsTheUserOut()
    {
        var (server, session, catalogue) = Create();
        await catalogue.LoadAsync();
        await session.SignInAsync("chris", "correct");
        server.ExpireSession = true;
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => catalogue.GetDetailAsync(PrivateR6));
        Assert.False(session.IsSignedIn);
    }

    [Fact]
    public void FindsPrivateLinkInPublicDescription()
    {
        var detail = FilehostClient.ParseFileDetail(FilehostCatalogueTests.Fixture("filedetail-r6.json"))!;
        Assert.Contains(PrivateR6, FilehostLinks.Extract(detail.Description));
    }

    [Fact]
    public async Task UpdateUsesRomPackageOnlyWhenAccountHasAccess()
    {
        var (_, session, catalogue) = Create();
        var paths = new AppPaths(_root);
        var engine = new UpdateEngine(catalogue, new DownloadManager(new HttpClient(), paths), new NullProbe(), new NoGate(), paths);

        var anon = await engine.FindReleaseAsync(HardwareModel.R6, default);
        var (none, note) = await engine.FindRomReleaseAsync(anon!, default);
        Assert.Null(none);
        Assert.Contains("Not signed in", note);

        await session.SignInAsync("chris", "correct");
        var (rom, _) = await engine.FindRomReleaseAsync(anon!, default);
        Assert.NotNull(rom);
        Assert.True(rom.Info.IncludesRom);
        Assert.Equal(PrivateR6, rom.File.FileId);
    }

    private sealed class NullProbe : IDeviceProbe
    {
        public Task<DeviceState> ProbeAsync(ConnectionTarget t, CancellationToken ct = default) => Task.FromResult(DeviceState.Unknown);
    }

    private sealed class NoGate : IApprovalGate
    {
        public Task<bool> ConfirmAsync(string title, string message, CancellationToken ct) => Task.FromResult(false);
        public Task<IReadOnlySet<string>?> ApproveChangesAsync(string title, SyncPlan plan, CancellationToken ct) => Task.FromResult<IReadOnlySet<string>?>(null);
    }
}

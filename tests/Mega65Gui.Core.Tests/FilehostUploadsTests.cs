using System.Text.RegularExpressions;
using System.Net;
using Mega65Gui.Core.Filehost;

namespace Mega65Gui.Core.Tests;

public sealed class FilehostUploadsTests : IDisposable
{
    private const string FileId = "56aac4bb-9cff-4455-91af-6e95bf9f1969";
    private readonly string _root = Directory.CreateTempSubdirectory("m65up").FullName;
    public void Dispose() => Directory.Delete(_root, recursive: true);

    // Shape of a real readfile.php answer (2026-10-18), description shortened.
    private const string ReadFileJson = """
        {"0":{"id":"56aac4bb-9cff-4455-91af-6e95bf9f1969","title":"Mega ST &amp; STe","description":"Line 1\nC:\\path \"quoted\"",
        "searchtags":"atari, st","shortlink":"","filetypeid":"58","ntsc":"Y","pal":"Y","osmega65":"Y","osc65":"N","osc64":"N",
        "osvixen":"N","oswindows":"N","osmac":"N","oslinux":"N","osonline":"N","introdisk":"N","highscore":"N","groupname":"devkit",
        "coreversionid":"-1","romversionid":"-1","locked":"N"},"state":"OK"}
        """;

    private sealed class FakeUploadServer : HttpMessageHandler
    {
        public string? LastPath, LastBody;
        public HttpRequestMessage? LastRequest;
        public string Answer = """{"state":"OK","message":"File uploaded","fileid":"new-id"}""";

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage req, CancellationToken ct)
        {
            LastRequest = req;
            LastPath = req.RequestUri!.AbsolutePath;
            LastBody = req.Content is null ? "" : await req.Content.ReadAsStringAsync(ct);
            var json = LastPath switch
            {
                "/php/readfile.php" => ReadFileJson,
                "/php/readfilelist.php" => $$"""[{"id":"{{FileId}}","title":"Mega ST V1.1"}]""",
                _ => Answer,
            };
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json) };
        }
    }

    private (FakeUploadServer Server, FilehostUploads Uploads, FilehostSession Session) Create()
    {
        var server = new FakeUploadServer();
        var http = new HttpClient(server);
        var session = new FilehostSession(http);
        return (server, new FilehostUploads(http, session), session);
    }

    private string TempFile(string name, int bytes = 100)
    {
        var p = Path.Combine(_root, name);
        File.WriteAllBytes(p, new byte[bytes]);
        return p;
    }

    [Fact]
    public void ParsesTheFilesAttributes()
    {
        var a = FilehostUploads.ParseAttributes(ReadFileJson);
        Assert.Equal("Mega ST & STe", a.Title);
        Assert.Equal("58", a.TypeId);
        Assert.Equal("Line 1\nC:\\path \"quoted\"", a.Description);
        Assert.Equal(UploadAccess.DevKit, a.Access);
        Assert.True(a.OsMega65 && a.Pal && a.Ntsc);
        Assert.False(a.OsWindows || a.Locked);
    }

    [Fact]
    public void RefusesAFileThatIsNotYours()
    {
        var e = Assert.Throws<FilehostUploadException>(() => FilehostUploads.ParseAttributes("""{"state":"EMPTY"}"""));
        Assert.Equal("EMPTY", e.State);
    }

    [Fact]
    public void EscapesAndNamesFieldsLikeTheWebsite()
    {
        var a = new UploadAttributes { Title = " A<b> & C:\\x ", TypeId = "58", Description = "C:\\dir", Access = UploadAccess.DevKit };
        var f = FilehostUploads.UploadFields(FileId, a, "fix \\n", "1.2", "new").ToDictionary(k => k.Key, k => k.Value);
        Assert.Equal("A&lt;b&gt; &amp; C:\\\\x", f["title"]);
        Assert.Equal("C:\\\\dir", f["description"]);
        Assert.Equal("fix \\\\n", f["comment"]);
        Assert.Equal("Y", f["groupmega65"]);
        Assert.Equal("N", f["grouppublic"]);
        Assert.Equal("new", f["mode"]);
        Assert.Equal("1.2", f["version"]);
        Assert.Equal(FileId, f["fileid"]);
    }

    [Fact]
    public async Task UploadsANewVersionAsMultipart()
    {
        var (server, uploads, _) = Create();
        var path = TempFile("MegaST-v1.2.zip");
        var attributes = await uploads.GetAttributesAsync(FileId);
        double last = 0;
        await uploads.UploadNewVersionAsync(FileId, path, attributes, 1.2m, "Floppy fixes", VersionMode.Replace, new SyncProgress(p => last = p));

        Assert.Equal("/php/savefileupload.php", server.LastPath);
        Assert.Contains("XMLHttpRequest", server.LastRequest!.Headers.GetValues("X-Requested-With"));
        Assert.StartsWith("multipart/form-data", server.LastRequest.Content!.Headers.ContentType!.ToString());
        Assert.Contains("name=fileupload; filename=MegaST-v1.2.zip", server.LastBody);
        Assert.Contains("name=mode\r\n\r\nreplace", Normalize(server.LastBody!));
        Assert.Contains("name=version\r\n\r\n1.2", Normalize(server.LastBody!));
        // The attributes read from the Filehost go back unchanged (re-escaped).
        Assert.Contains("name=title\r\n\r\nMega ST &amp; STe", Normalize(server.LastBody!));
        Assert.Equal(1, last);
    }

    private static string Normalize(string body) => body.Replace("Content-Type: text/plain; charset=utf-8\r\n", "")
        .Replace("Content-Disposition: form-data; ", "");

    private sealed class SyncProgress(Action<double> report) : IProgress<double>
    {
        public void Report(double value) => report(value);
    }

    [Fact]
    public async Task ANewFileReturnsItsId()
    {
        var (server, uploads, _) = Create();
        var id = await uploads.UploadNewFileAsync(TempFile("demo.prg"), new UploadAttributes { Title = "Demo", TypeId = "2" });
        Assert.Equal("new-id", id);
        Assert.Contains("name=mode\r\n\r\ninit", Normalize(server.LastBody!));
        Assert.Contains("name=fileid\r\n\r\n-1", Normalize(server.LastBody!));
    }

    [Fact]
    public async Task ChecksTheFileAndAttributesBeforeSending()
    {
        var (server, uploads, _) = Create();
        await Assert.ThrowsAsync<FilehostUploadException>(() => uploads.UploadNewFileAsync(TempFile("x.prg"), new UploadAttributes { Title = "" }));
        await Assert.ThrowsAsync<FilehostUploadException>(() =>
            uploads.UploadNewFileAsync(TempFile("x.png"), new UploadAttributes { Title = "X", TypeId = "2" }));
        await Assert.ThrowsAsync<FilehostUploadException>(() =>
            uploads.UploadNewVersionAsync(FileId, TempFile("x.cor"), new UploadAttributes(), 1.25m, "", VersionMode.New));
        Assert.Null(server.LastPath);
    }

    [Fact]
    public async Task AServerRefusalBecomesAnError()
    {
        var (server, uploads, _) = Create();
        server.Answer = """{"state":"EXIST","message":"This file was already uploaded"}""";
        var e = await Assert.ThrowsAsync<FilehostUploadException>(() =>
            uploads.UploadNewVersionAsync(FileId, TempFile("a.zip"), new UploadAttributes { Title = "A", TypeId = "1" }, 2, "", VersionMode.New));
        Assert.Equal("This file was already uploaded", e.Message);
    }

    [Fact]
    public async Task AnExpiredSessionSignsOut()
    {
        var (server, uploads, session) = Create();
        server.Answer = """{"state":"OK"}""";
        // Simulate a signed-in session, then expiry on the next call.
        typeof(FilehostSession).GetProperty("UserName")!.SetValue(session, "me");
        server.Answer = """{"state":"TIMEOUT"}""";
        await Assert.ThrowsAsync<FilehostUploadException>(() => uploads.SaveAttributesAsync(FileId, new UploadAttributes { Title = "A", TypeId = "1" }));
        Assert.False(session.IsSignedIn);
    }

    [Fact]
    public async Task ListsYourFiles()
    {
        var (_, uploads, _) = Create();
        var files = await uploads.ListMyFilesAsync();
        Assert.Equal("Mega ST V1.1", Assert.Single(files).Title);
    }

    [Theory]
    [InlineData(1, true)]
    [InlineData(1.1, true)]
    [InlineData(9999.9, true)]
    [InlineData(0.9, false)]
    [InlineData(1.25, false)]
    [InlineData(10000, false)]
    public void ValidatesVersions(decimal v, bool ok) => Assert.Equal(ok, FilehostUploads.IsValidVersion(v));

    [Fact]
    public void SuggestsTheNextVersion()
    {
        MyVersionRow V(string v) => new() { VersionId = v, Version = v };
        Assert.Equal(1.2m, FilehostUploads.SuggestNextVersion([V("1.0"), V("1.1")]));
        Assert.Equal(1m, FilehostUploads.SuggestNextVersion([]));
        Assert.Equal("2", FilehostUploads.FormatVersion(2.0m));
    }

    [Theory]
    [InlineData("https://github.com/freemchr/MegaST", "freemchr/MegaST")]
    [InlineData("github.com/freemchr/MegaST.git", "freemchr/MegaST")]
    [InlineData("https://github.com/freemchr/MegaST/releases/tag/v1.1", "freemchr/MegaST")]
    [InlineData("freemchr/MegaST", "freemchr/MegaST")]
    [InlineData("not a repo", null)]
    public void ParsesGitHubRepos(string text, string? repo) => Assert.Equal(repo, GitHubReleases.ParseRepo(text));

    [Theory]
    [InlineData("v1.2", 1.2)]
    [InlineData("1.0", 1.0)]
    [InlineData("MegaST-3", 3.0)]
    [InlineData("v1.2.3", null)]
    [InlineData("v1.25", null)]
    [InlineData("nightly", null)]
    public void TurnsTagsIntoVersions(string tag, double? expected) =>
        Assert.Equal(expected is null ? null : (decimal)expected, GitHubReleases.VersionFromTag(tag));

    private static GitHubRelease Release(string tag, params string[] assets) => new()
    {
        Tag = tag, Name = $"MegaST {tag}", Url = $"https://github.com/freemchr/MegaST/releases/tag/{tag}",
        Assets = assets.Select(a => new GitHubAsset { Name = a, DownloadUrl = "https://example.invalid/" + a, Size = 10 }).ToList(),
    };

    [Fact]
    public void PicksTheReleaseFile()
    {
        var r = Release("v1.2", "notes.md", "MegaST-v1.2.zip", "MegaST-src.tar");
        Assert.Equal("MegaST-v1.2.zip", GitHubReleases.PickAsset(r, "MegaST-v*.zip")!.Name);
        Assert.Equal("MegaST-v1.2.zip", GitHubReleases.PickAsset(r, "")!.Name);
        Assert.Null(GitHubReleases.PickAsset(r, "*.cor"));
    }

    [Fact]
    public void DecidesWhatToSync()
    {
        var link = new UploadLink { FileId = FileId, Repo = "freemchr/MegaST", AssetPattern = "*.zip" };
        MyVersionRow V(string v, string file) => new() { VersionId = v, Version = v, FileName = file };
        var onFilehost = new[] { V("1.1", "MegaST-v1.1.zip"), V("1.0", "MegaST-v1.0.zip") };

        var d = SyncDecision.Decide(link, Release("v1.2", "MegaST-v1.2.zip"), onFilehost);
        Assert.True(d.NeedsUpload);
        Assert.Equal(1.2m, d.Version);
        Assert.Equal("MegaST v1.2 (https://github.com/freemchr/MegaST/releases/tag/v1.2)", d.Comment);

        Assert.False(SyncDecision.Decide(link, Release("v1.1", "MegaST-v1.1.zip"), onFilehost).NeedsUpload);
        Assert.False(SyncDecision.Decide(link with { LastTag = "v1.2" }, Release("v1.2", "MegaST-v1.2.zip"), onFilehost).NeedsUpload);
        Assert.False(SyncDecision.Decide(link, Release("v1.2", "notes.md"), onFilehost).NeedsUpload);
        Assert.False(SyncDecision.Decide(link, null, onFilehost).NeedsUpload);
        // A tag that is not nnnn.n, or one already used, gets the next free version.
        Assert.Equal(1.2m, SyncDecision.Decide(link, Release("v1.2.0", "MegaST-v1.2.0.zip"), onFilehost).Version);
        Assert.Equal(1.2m, SyncDecision.Decide(link, Release("v1.1-fix", "MegaST-v1.1-fix.zip"), onFilehost).Version);
    }

    [Fact]
    public void RemembersLinks()
    {
        var file = Path.Combine(_root, "links.json");
        var store = new UploadLinkStore(file);
        store.Set(new UploadLink { FileId = FileId, Repo = "freemchr/MegaST", LastTag = "v1.1" });
        store.CheckAtStartup = true;
        store.Save();
        var again = new UploadLinkStore(file);
        again.Load();
        Assert.Equal("v1.1", again.For(FileId)!.LastTag);
        Assert.True(again.CheckAtStartup);
    }

    [Fact]
    public void TurnsReleaseNotesIntoAFilehostDescription()
    {
        // Shape of the MegaZXNext V1.0 release notes: a badge, bold, links and hard-wrapped lines.
        const string body = "[![Buy me a coffee](https://img.shields.io/badge/x)](https://buymeacoffee.com/cjf077)\r\n\r\n" +
            "**If you enjoy MegaZXNext, you can [buy me a coffee](https://buymeacoffee.com/cjf077). Thank you!**\r\n\r\n" +
            "# MegaZXNext V1.0\r\n\r\nThe first full release of **MegaZXNext**, a ZX Next core for the [MEGA65](https://mega65.org/),\r\n" +
            "ported from ZXNext_MiSTer.\r\n\r\n## Install\r\n* Copy `zxnext` to the card\r\n* Flash the core\r\n";
        var repo = new GitHubRepository { Name = "MegaZXNext", Description = "Mega 65 ZX Next Core", Url = "https://github.com/freemchr/MegaZXNext" };
        var release = Release("v1.0") with { Name = "MegaZXNext V1.0", Body = body };

        var d = GitHubReleases.BuildDescription(repo, release);
        Assert.Equal(
            "Mega 65 ZX Next Core\n\n" +
            "#bs#If you enjoy MegaZXNext, you can buy me a coffee (https://buymeacoffee.com/cjf077). Thank you!#be#\n\n" +
            "#bs#MegaZXNext V1.0#be#\n\n" +
            "The first full release of #bs#MegaZXNext#be#, a ZX Next core for the MEGA65 (https://mega65.org/), ported from ZXNext_MiSTer.\n\n" +
            "#bs#Install#be#\n- Copy zxnext to the card\n- Flash the core\n\n" +
            "Source: https://github.com/freemchr/MegaZXNext", d);
    }

    [Fact]
    public void SuggestsAPatternForLaterReleases()
    {
        var a = new GitHubAsset { Name = "megazxnext_v1.0_r4_r5_r6.zip", DownloadUrl = "x" };
        Assert.Equal("megazxnext_v*_r4_r5_r6.zip", GitHubReleases.SuggestPattern(a, "v1.0"));
        Assert.Equal("*.zip", GitHubReleases.SuggestPattern(a, "first"));
        var next = Release("v1.1", "megazxnext_v1.1_r4_r5_r6.zip");
        Assert.NotNull(GitHubReleases.PickAsset(next, "megazxnext_v*_r4_r5_r6.zip"));
    }

    [Fact]
    public void TagsComeFromTopics() =>
        Assert.Equal("mega65, zx-next", GitHubReleases.BuildSearchTags(new GitHubRepository { Name = "x", Topics = ["mega65", "zx", "zx-next"] }));

    [Fact]
    public void ConvertsTheRealMegaZXNextReleaseNotes()
    {
        var text = GitHubReleases.MarkdownToFilehost(FilehostCatalogueTests.Fixture("release-megazxnext-v1.0.md"));
        var lines = text.Split('\n');

        Assert.Equal(Regex.Count(text, "#bs#"), Regex.Count(text, "#be#"));
        Assert.DoesNotContain("**", text);
        Assert.DoesNotContain("|---", text);
        Assert.DoesNotContain(lines, l => l.StartsWith('>'));
        // Tables: one row per line, header in bold.
        Assert.Contains("#bs#Board | File in the zip | Status#be#", lines);
        Assert.Contains("#bs#R6#be# | megazxnext_r6.cor | Tested", lines);
        // Bold that wrapped across lines in the Markdown.
        Assert.Contains("#bs#FAT16 cards are not supported#be#", text);
        Assert.Contains("#bs#Right Joy#be# (port 2)", text);
        Assert.Contains(lines, l => l.StartsWith("#bs#Note:#be# MegaZXNext is not a ZX Spectrum Next"));
    }


    [Fact]
    public void SuggestsSearchTagsFromGitHub()
    {
        var repo = new GitHubRepository { Name = "MegaZXNext", Description = "Mega 65 ZX Next Core", Topics = ["mega65", "fpga-core"] };
        var release = Release("v1.0") with { Name = "MegaZXNext V1.0", Body = FilehostCatalogueTests.Fixture("release-megazxnext-v1.0.md") };
        var tags = GitHubReleases.SuggestTags(repo, release, "MegaZXNext", "megazxnext_v1.0_r4_r5_r6.zip");

        Assert.Contains("fpga core", tags);
        Assert.Contains("spectrum", tags);
        Assert.Contains("nextzxos", tags);
        Assert.DoesNotContain("amstrad", tags); // only in the copyright line
        Assert.Contains("core", tags);
        // Already searched by the Filehost (title words), too short, or noise.
        Assert.DoesNotContain("mega65", tags);
        Assert.DoesNotContain("next", tags);
        Assert.DoesNotContain(tags, t => t.Length < 3);
        Assert.True(tags.Count <= 12);
        Assert.True(GitHubReleases.JoinTags(tags).Length <= 200);
    }

    [Fact]
    public void RefusesBoldThatIsNeverEnded()
    {
        // What reached the Filehost on 2026-10-18: "Thank you!#be#" was missing, so the whole page was bold.
        var broken = "Mega 65 ZX Next Core\n\n#bs#If you enjoy MegaZXNext, you can buy me a coffee. \n\n#bs#MegaZXNext V1.0#be#\n\nText";
        Assert.Contains("started again", UploadAttributes.BoldProblem(broken));
        Assert.Contains("never ended", UploadAttributes.BoldProblem("#bs#Bold to the end"));
        Assert.Contains("without a #bs#", UploadAttributes.BoldProblem("text#be#"));
        Assert.Null(UploadAttributes.BoldProblem("#bs#a#be# and #BS#b#BE#"));
        Assert.Contains(new UploadAttributes { Title = "A", TypeId = "1", Description = broken }.Problems(), p => p.StartsWith("Description:"));
    }

    [Fact]
    public void ReadsPreviews()
    {
        // Shape of a real readmedias.php answer for Mega ST (2026-10-18).
        var rows = FilehostUploads.NumberedRows<PreviewMedia>("""
            {"state":"OK","0":{"mediaid":"cddb59ca","location":"..\/filesmedia\/t\/tos104desktop_3zwAqV.png","thumbnail":"..\/filesmedia\/t\/tos104desktop_3zwAqV_prv.png","type":"I","title":"Mega ST V1.1"},
             "1":{"mediaid":"1085ce4e","location":"https:\/\/youtu.be\/x","thumbnail":"","type":"V","title":"Mega ST V1.1"}}
            """);
        Assert.Equal(2, rows.Count);
        Assert.Equal("https://files.mega65.org/filesmedia/t/tos104desktop_3zwAqV_prv.png", rows[0].ThumbnailUri!.ToString());
        Assert.Equal("Video", rows[1].Kind);
        Assert.Equal("https://youtu.be/x", rows[1].LocationUri!.ToString());
        Assert.Empty(FilehostUploads.NumberedRows<PreviewMedia>("""{"state":"EMPTY"}"""));
    }

    [Fact]
    public async Task UploadsAPreviewPicture()
    {
        var (server, uploads, _) = Create();
        server.Answer = """{"state":"OK","message":"Preview uploaded","mediaid":"m1","thumbnail":"x","type":"I"}""";
        Assert.Equal("Preview uploaded", await uploads.UploadPreviewAsync(FileId, TempFile("screen.png")));
        Assert.Equal("/php/savemediaupload.php", server.LastPath);
        Assert.Contains("name=mediaupload; filename=screen.png", server.LastBody);
        Assert.Contains("name=origin\r\n\r\nF", Normalize(server.LastBody!));
        await Assert.ThrowsAsync<FilehostUploadException>(() => uploads.UploadPreviewAsync(FileId, TempFile("notes.txt")));
    }

    [Fact]
    public void DropsHtmlFromReleaseNotes()
    {
        var text = GitHubReleases.MarkdownToFilehost(
            "<p align=\"center\"><img src=\"https://raw.githubusercontent.com/x/logo.png\" alt=\"Logo\" width=\"700\"></p>\n\n" +
            "Text with <b>bold</b>, a <a href=\"https://mega65.org\">link</a> and Tom &amp; Jerry.<br>Next line\n<!-- hidden -->");
        Assert.Equal("Text with #bs#bold#be#, a link (https://mega65.org) and Tom & Jerry.\nNext line", text);
    }
}

using System.IO.Compression;
using System.Net;
using System.Text.Json;
using Mega65Gui.Core.Common;
using Mega65Gui.Core.Device;
using Mega65Gui.Core.Filehost;
using Mega65Gui.Core.Packages;
using Mega65Gui.Core.Sd;
using Mega65Gui.Core.Update;

namespace Mega65Gui.Core.Tests;

public sealed class UpdateEngineTests : IDisposable
{
    private const string R6FileId = "764642f7-83bc-4162-9911-99a0a6283d08";
    private readonly string _root = Directory.CreateTempSubdirectory("m65upd").FullName;
    private string Card => Path.Combine(_root, "card");
    public void Dispose() => Directory.Delete(_root, recursive: true);

    private byte[] BuildPackage()
    {
        using var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            void Add(string name, string content)
            {
                using var w = new StreamWriter(zip.CreateEntry(name).Open());
                w.Write(content);
            }
            const string p = "mega65r6-release-0.97.2-03b24c6/";
            Add(p + "README.md", "readme");
            Add(p + "mega65r6-20250919.07-r-0.97.2-03b24c6.cor", "R6 CORE");
            Add(p + "beta-pcb/mega65r5-20250919.07-r-0.97.2-03b24c6.cor", "R5 CORE");
            Add(p + "extra/HICKUP.M65", "hickup");
            Add(p + "sdcard-files/FREEZER.M65", "freezer v2");
            Add(p + "sdcard-files/MEGAINFO.M65", "megainfo");
            Add(p + "sdcard-files/ETHLOAD.M65", "ethload");
        }
        return ms.ToArray();
    }

    private (UpdateEngine Engine, FakeGate Gate) Create(DeviceState device, bool approveAll = true)
    {
        var package = BuildPackage();
        var versions = JsonSerializer.Serialize(new[]
        {
            new { fileid = R6FileId, version = "2.1", versionid = "v21", filename = "mega65r6-release-0.97.2-03b24c6-norom.7z",
                  size = package.Length.ToString(), comment = "Release 0.97.2", published = "28 Sep 2025", active = "Y", location = "../files/m/pkg.7z" },
            new { fileid = R6FileId, version = "2.0", versionid = "v20", filename = "mega65r6-release-0.97-aaaaaaa-norom.7z",
                  size = "1", comment = "Release 0.97", published = "1 May 2025", active = "Y", location = "../files/m/old.7z" },
        });
        var handler = new RoutingHandler(new()
        {
            ["/php/readfilespublic.php"] = FilehostCatalogueTests.Fixture("filespublic-sample.json"),
            ["/php/readversionlistpublic.php"] = versions,
        }, new() { ["/files/m/pkg.7z"] = package });
        var http = new HttpClient(handler);
        var paths = new AppPaths(Path.Combine(_root, "app"));
        var gate = new FakeGate(approveAll);
        var engine = new UpdateEngine(new CatalogueService(new FilehostClient(http), paths), new DownloadManager(http, paths),
            new FakeProbe(device), gate, paths);
        return (engine, gate);
    }

    private void SeedCard()
    {
        Directory.CreateDirectory(Card);
        File.WriteAllText(Path.Combine(Card, "FREEZER.M65"), "freezer v1");
        File.WriteAllText(Path.Combine(Card, "MEGAINFO.M65"), "megainfo");
        File.WriteAllText(Path.Combine(Card, "MYGAME.D81"), "mine");
    }

    [Fact]
    public async Task FullRunUpdatesCardStagesCoreAndGuidesFlash()
    {
        SeedCard();
        var device = DeviceState.Unknown with { Connection = ConnectionTarget.Serial("COM3"), Model = HardwareModel.R6, ModelSource = Provenance.ReadFromDevice, CoreBuild = "release-0.97,aaaaaaa" };
        var (engine, gate) = Create(device);

        var report = await engine.RunAsync(new UpdateOptions(Card, ConnectionTarget.Serial("COM3")));

        Assert.Equal(new Version(0, 97, 2), report.Release!.Info.Release);
        Assert.Equal(CoreComparison.UpdateAvailable, report.Core);
        Assert.Equal("freezer v2", File.ReadAllText(Path.Combine(Card, "FREEZER.M65")));
        Assert.Equal("ethload", File.ReadAllText(Path.Combine(Card, "ETHLOAD.M65")));
        Assert.Equal("mine", File.ReadAllText(Path.Combine(Card, "MYGAME.D81")));
        Assert.False(File.Exists(Path.Combine(Card, "HICKUP.M65")));
        Assert.Equal("R6 CORE", File.ReadAllText(Path.Combine(Card, "mega65r6-20250919.07-r-0.97.2-03b24c6.cor")));
        Assert.Contains(report.Steps, s => s.Number == 7 && s.Detail.Contains("1 file(s) backed up"));
        Assert.Equal(StepStatus.NeedsUser, report.Steps.Single(s => s.Number == 10).Status);
        Assert.Equal(StepStatus.Done, report.Steps.Single(s => s.Number == 11).Status);
        Assert.Equal(12, report.Steps.Count);
        var reportFile = report.Steps.Single(s => s.Number == 12).Detail;
        Assert.True(File.Exists(reportFile));
        Assert.DoesNotContain("| Running |", File.ReadAllText(reportFile));
        Assert.Equal(3, gate.Prompts); // summary, SD changes, core staging
    }

    [Fact]
    public async Task UpToDateCoreIsNotStaged()
    {
        SeedCard();
        var device = DeviceState.Unknown with { Model = HardwareModel.R6, ModelSource = Provenance.ReadFromDevice, CoreBuild = "release-0.97.2,20250919.07,03b24c6" };
        var (engine, _) = Create(device);
        var report = await engine.RunAsync(new UpdateOptions(Card, ConnectionTarget.Serial("COM3")));
        Assert.Equal(CoreComparison.UpToDate, report.Core);
        Assert.Equal(StepStatus.Skipped, report.Steps.Single(s => s.Number == 9).Status);
        Assert.Empty(Directory.EnumerateFiles(Card, "*.cor"));
    }

    [Fact]
    public async Task DecliningLeavesCardUntouched()
    {
        SeedCard();
        var (engine, _) = Create(DeviceState.Unknown, approveAll: false);
        var report = await engine.RunAsync(new UpdateOptions(Card, null, HardwareModel.R6));
        Assert.Equal("freezer v1", File.ReadAllText(Path.Combine(Card, "FREEZER.M65")));
        Assert.Equal("Cancelled at summary.", report.Outcome);
        Assert.True(File.Exists(report.Steps.Single(s => s.Number == 12).Detail));
    }

    [Fact]
    public async Task UnknownModelStopsAndAsksUser()
    {
        SeedCard();
        var (engine, gate) = Create(DeviceState.Unknown);
        var report = await engine.RunAsync(new UpdateOptions(Card));
        Assert.Equal(StepStatus.NeedsUser, report.Steps.Single(s => s.Number == 2).Status);
        Assert.Equal(0, gate.Prompts);
    }

    [Fact]
    public async Task ExtractorRejectsPathTraversal()
    {
        var zipPath = Path.Combine(_root, "evil.zip");
        using (var zip = ZipFile.Open(zipPath, ZipArchiveMode.Create))
        using (var w = new StreamWriter(zip.CreateEntry("../escaped.txt").Open())) w.Write("x");
        await Assert.ThrowsAsync<InvalidDataException>(() => PackageExtractor.ExtractAsync(zipPath, Path.Combine(_root, "out")));
        Assert.False(File.Exists(Path.Combine(_root, "escaped.txt")));
    }

    private sealed class FakeProbe(DeviceState state) : IDeviceProbe
    {
        public Task<DeviceState> ProbeAsync(ConnectionTarget target, CancellationToken ct = default) => Task.FromResult(state);
    }

    private sealed class FakeGate(bool approve) : IApprovalGate
    {
        public int Prompts;
        public Task<bool> ConfirmAsync(string title, string message, CancellationToken ct) { Prompts++; return Task.FromResult(approve); }
        public Task<IReadOnlySet<string>?> ApproveChangesAsync(string title, SyncPlan plan, CancellationToken ct)
        {
            Prompts++;
            return Task.FromResult<IReadOnlySet<string>?>(approve ? plan.Changes.Select(c => c.RelativePath).ToHashSet() : null);
        }
    }

    private sealed class RoutingHandler(Dictionary<string, string> text, Dictionary<string, byte[]> binary) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var path = request.RequestUri!.AbsolutePath;
            HttpContent? content = text.TryGetValue(path, out var t) ? new StringContent(t)
                : binary.TryGetValue(path, out var b) ? new ByteArrayContent(b) : null;
            return Task.FromResult(content is null
                ? new HttpResponseMessage(HttpStatusCode.NotFound)
                : new HttpResponseMessage(HttpStatusCode.OK) { Content = content });
        }
    }
}

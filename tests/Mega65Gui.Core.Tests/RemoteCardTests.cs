using Mega65Gui.Core.Device;
using Mega65Gui.Core.Sd;
using Mega65Gui.Core.Update;

namespace Mega65Gui.Core.Tests;

public sealed class RemoteCardTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("m65remote").FullName;
    public void Dispose() => Directory.Delete(_root, recursive: true);

    /// <summary>Lines from a real mega65_ftp 1.00 "dir" of an R6 card (2026-10-16).</summary>
    [Fact]
    public void ParsesRealDirListing()
    {
        const string output = """
            2026-10-16T04:57:48.533Z NOTE SD card is SDHC
            Type 'help' to get a list of commands available.
                   <DIR> | 2026-09-27 14:39:54  | CPC4ME~1     | cpc4mega65
                      74 | 2026-07-23 07:19:34  | AEXP-V1.CFG  | aexp-V1.cfg
                   25333 | 2024-06-30 17:55:16  | AUDIOMIX.M65 | 
                  131072 | 2024-06-30 17:55:16  | MEGA65.ROM   | 
                 8388608 | 2026-09-27 14:21:28  | MEGA65~1.COR | mega65r6-20250919.07-r-0.97.2-03b24c6.cor
            9 Dir(s), 50 File(s)
            """;
        var entries = Mega65FtpCard.ParseDir(output);
        Assert.Equal(5, entries.Count);
        Assert.True(entries[0].IsDirectory);
        Assert.Equal(25333, entries.Single(e => e.Matches("AUDIOMIX.M65")).Size);
        Assert.True(entries.Single(e => e.ShortName == "MEGA65~1.COR").Matches("mega65r6-20250919.07-r-0.97.2-03b24c6.cor"));
    }

    /// <summary>A card inside the MEGA65, simulated by a folder.</summary>
    internal sealed class FolderCard(string folder) : IRemoteCard
    {
        public int Uploads;
        public string Description => "simulated MEGA65 card";

        public Task<IReadOnlyList<RemoteEntry>> ListRootAsync(CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<RemoteEntry>>(Directory.EnumerateFiles(folder)
                .Select(f => new RemoteEntry(Path.GetFileName(f).ToUpperInvariant(), Path.GetFileName(f), new FileInfo(f).Length, false)).ToList());

        public Task DownloadAsync(IReadOnlyList<string> names, string localDir, IProgress<string>? log, CancellationToken ct)
        {
            Directory.CreateDirectory(localDir);
            foreach (var n in names) File.Copy(Path.Combine(folder, n), Path.Combine(localDir, n), true);
            return Task.CompletedTask;
        }

        public Task UploadAsync(string localDir, IReadOnlyList<string> names, IProgress<string>? log, CancellationToken ct)
        {
            Uploads++;
            foreach (var n in names) File.Copy(Path.Combine(localDir, n), Path.Combine(folder, n), true);
            return Task.CompletedTask;
        }
    }

    [Fact]
    public async Task RemoteSyncBacksUpUploadsOnlyChangesAndVerifies()
    {
        var pkg = Directory.CreateDirectory(Path.Combine(_root, "pkg")).FullName;
        var card = Directory.CreateDirectory(Path.Combine(_root, "card")).FullName;
        File.WriteAllText(Path.Combine(pkg, "FREEZER.M65"), "new");
        File.WriteAllText(Path.Combine(pkg, "BANNER.M65"), "same");
        File.WriteAllText(Path.Combine(pkg, "ETHLOAD.M65"), "added");
        File.WriteAllText(Path.Combine(card, "FREEZER.M65"), "old");
        File.WriteAllText(Path.Combine(card, "BANNER.M65"), "same");
        File.WriteAllText(Path.Combine(card, "MYGAME.D81"), "mine");

        var fake = new FolderCard(card);
        var sync = new RemoteSdSync(fake);
        var (plan, backup) = await sync.PlanAsync(pkg, Path.Combine(_root, "backups"));
        var actions = plan.Items.ToDictionary(i => i.RelativePath, i => i.Action);
        Assert.Equal(SyncAction.Replace, actions["FREEZER.M65"]);
        Assert.Equal(SyncAction.Unchanged, actions["BANNER.M65"]);
        Assert.Equal(SyncAction.Add, actions["ETHLOAD.M65"]);
        Assert.Equal(SyncAction.TargetOnly, actions["MYGAME.D81"]);

        var result = await sync.ApplyAsync(plan, plan.Changes.Select(c => c.RelativePath).ToHashSet(), backup);
        Assert.True(result.AllSucceeded);
        Assert.Equal(1, fake.Uploads);
        Assert.Equal("new", File.ReadAllText(Path.Combine(card, "FREEZER.M65")));
        Assert.Equal("old", File.ReadAllText(result.Results.Single(r => r.Item.RelativePath == "FREEZER.M65").BackupPath!));
        Assert.Equal("mine", File.ReadAllText(Path.Combine(card, "MYGAME.D81")));
    }
}

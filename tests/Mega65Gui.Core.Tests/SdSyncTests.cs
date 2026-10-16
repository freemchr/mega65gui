using Mega65Gui.Core.Sd;

namespace Mega65Gui.Core.Tests;

public sealed class SdSyncTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("m65sync").FullName;
    private string Pkg => Path.Combine(_root, "pkg");
    private string Card => Path.Combine(_root, "card");
    private string Backups => Path.Combine(_root, "backups");

    public SdSyncTests()
    {
        Write(Pkg, "MEGA65.D81", "new-disk");
        Write(Pkg, "FREEZER.M65", "same");
        Write(Pkg, "SUB/NEW.PRG", "added");
        Write(Card, "mega65.d81", "old-disk");   // different case, different content
        Write(Card, "FREEZER.M65", "same");
        Write(Card, "MYGAME.D81", "user file");
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private static void Write(string root, string rel, string content)
    {
        var p = Path.Combine(root, rel);
        Directory.CreateDirectory(Path.GetDirectoryName(p)!);
        File.WriteAllText(p, content);
    }

    [Fact]
    public async Task PlanClassifiesEveryFile()
    {
        var plan = await new SdSyncPlanner().PlanAsync(Pkg, Card);
        var byPath = plan.Items.ToDictionary(i => i.RelativePath, i => i.Action, StringComparer.OrdinalIgnoreCase);
        Assert.Equal(SyncAction.Replace, byPath["MEGA65.D81"]);
        Assert.Equal(SyncAction.Unchanged, byPath["FREEZER.M65"]);
        Assert.Equal(SyncAction.Add, byPath["SUB/NEW.PRG"]);
        Assert.Equal(SyncAction.TargetOnly, byPath["MYGAME.D81"]);
        Assert.Contains(plan.Warnings, w => w.Contains("contiguous"));
    }

    [Fact]
    public async Task ApplyBacksUpReplacesAndNeverDeletes()
    {
        var plan = await new SdSyncPlanner().PlanAsync(Pkg, Card);
        var approved = plan.Changes.Select(c => c.RelativePath).ToHashSet();
        var result = await new SdSyncExecutor().ApplyAsync(plan, approved, Backups);

        Assert.True(result.AllSucceeded);
        Assert.Equal("new-disk", File.ReadAllText(Path.Combine(Card, "mega65.d81"))); // kept original casing
        Assert.Equal("added", File.ReadAllText(Path.Combine(Card, "SUB", "NEW.PRG")));
        Assert.Equal("user file", File.ReadAllText(Path.Combine(Card, "MYGAME.D81")));
        var backup = result.Results.Single(r => r.Item.Action == SyncAction.Replace).BackupPath!;
        Assert.Equal("old-disk", File.ReadAllText(backup));
        Assert.Empty(Directory.EnumerateFiles(Card, "*.m65tmp", SearchOption.AllDirectories));

        var replan = await new SdSyncPlanner().PlanAsync(Pkg, Card);
        Assert.Empty(replan.Changes);
    }

    [Fact]
    public async Task UnapprovedItemsAreSkipped()
    {
        var plan = await new SdSyncPlanner().PlanAsync(Pkg, Card);
        var result = await new SdSyncExecutor().ApplyAsync(plan, new HashSet<string>(), Backups);
        Assert.All(result.Results, r => Assert.Equal(ItemOutcome.Skipped, r.Outcome));
        Assert.Equal("old-disk", File.ReadAllText(Path.Combine(Card, "mega65.d81")));
    }
}

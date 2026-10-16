using Mega65Gui.Core.Features;

namespace Mega65Gui.Core.Tests;

public class DocsTests
{
    [Fact]
    public void ParityDocumentListsEveryCatalogFeature()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Mega65Gui.slnx"))) dir = dir.Parent;
        Assert.NotNull(dir);
        var doc = File.ReadAllText(Path.Combine(dir.FullName, "docs", "M65CONNECT-PARITY.md"));
        var missing = FeatureCatalog.All.Where(f => !doc.Contains(f.Name, StringComparison.Ordinal)).Select(f => f.Name).ToList();
        Assert.Empty(missing);
    }
}

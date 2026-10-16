using Mega65Gui.Core.Filehost;

namespace Mega65Gui.Core.Tests;

public class FilehostVersionTests
{
    private static IReadOnlyList<FilehostVersionRow> Sample() =>
        FilehostClient.ParseVersionList(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "versionlist-sample.json")));

    [Fact]
    public void ParsesRealResponseSample()
    {
        var rows = Sample();
        Assert.Equal(6, rows.Count);
        var r = rows[0];
        Assert.Equal("13e1ce8a-ed5b-4046-aea6-491323697ead", r.FileId);
        Assert.Equal(200.0m, r.VersionNumber);
        Assert.True(r.IsActive);
        Assert.Equal(597, r.SizeBytes);
        Assert.Equal(new DateOnly(2024, 10, 19), r.PublishedDate);
    }

    [Fact]
    public void ResolvesRelativeLocationAgainstPhpFolder()
    {
        var uri = FilehostClient.ResolveDownloadUri(Sample()[0]);
        Assert.Equal("https://files.mega65.org/files/m/mega65-core-dev-build_kV0BAC.7z", uri!.ToString());
    }
}

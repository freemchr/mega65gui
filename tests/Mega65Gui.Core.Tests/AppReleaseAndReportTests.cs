using Mega65Gui.Core.Common;
using Mega65Gui.Core.Filehost;

namespace Mega65Gui.Core.Tests;

public class AppReleaseAndReportTests
{
    private static GitHubRelease Release(string tag) => new() { Tag = tag, Url = $"https://github.com/freemchr/mega65gui/releases/tag/{tag}" };

    [Theory]
    [InlineData("v1.2", "1.1.1", true)]   // minor steps from now on (1.2, 1.3, …)
    [InlineData("v1.1.1", "1.1.1", false)]
    [InlineData("v1.2", "1.2.0.0", false)] // the assembly version of a 1.2 build
    [InlineData("v1.10", "1.9", true)]     // numbers, not text: 1.10 is after 1.9
    [InlineData("v1.1.0", "1.1.1", false)]
    [InlineData("nightly", "1.1.1", false)]
    public void OffersOnlyANewerRelease(string tag, string current, bool offered) =>
        Assert.Equal(offered, AppReleaseCheck.NewerThan(Release(tag), Version.Parse(current)) is not null);

    [Fact]
    public void ShowsShortVersions()
    {
        Assert.Equal("1.2", AppReleaseCheck.Display(new Version(1, 2, 0, 0)));
        Assert.Equal("1.1.1", AppReleaseCheck.Display(new Version(1, 1, 1)));
        Assert.Equal(new Version(1, 2, 0), AppReleaseCheck.ParseTag("v1.2"));
    }

    [Fact]
    public void ReportDoesNotNameTheUser()
    {
        var text = ProblemReport.Scrub(@"Card  C:\Users\Chris\Downloads\sd and chris's notes", @"C:\Users\Chris", "Chris");
        Assert.Equal(@"Card  %USERPROFILE%\Downloads\sd and <user>'s notes", text);
    }

    [Fact]
    public void IssueLinkCarriesTheDetailsAndStaysShort()
    {
        var url = ProblemReport.IssueUrl("https://github.com/freemchr/mega65gui/", "Problem: ", "COM ports  COM3 – Bluetooth (not a MEGA65)");
        Assert.StartsWith("https://github.com/freemchr/mega65gui/issues/new?title=Problem%3A%20&body=", url);
        Assert.Contains(Uri.EscapeDataString("COM3 – Bluetooth (not a MEGA65)"), url);

        var longUrl = ProblemReport.IssueUrl("https://github.com/freemchr/mega65gui", "x", new string('a', 20_000));
        Assert.True(longUrl.Length < 8000);
    }

    [Fact]
    public void LinesUpTheDetails() =>
        Assert.Equal("Page        About\nConnection  -\n".Replace("\n", Environment.NewLine),
            ProblemReport.Lines([("Page", "About"), ("Connection", null)]));
}

using Mega65Gui.Core.Device;

namespace Mega65Gui.Core.Tests;

public class KeyMatrixTests
{
    [Theory]
    [InlineData('a', 0x0A, 0x7F)]
    [InlineData('A', 0x0A, 0x0F)]
    [InlineData('1', 0x38, 0x7F)]
    [InlineData('!', 0x38, 0x0F)]
    [InlineData(' ', 0x3C, 0x7F)]
    [InlineData('"', 0x3B, 0x0F)]
    public void MapsCharacters(char c, byte key, byte modifier) => Assert.Equal((key, modifier), KeyMatrix.ForChar(c));

    [Fact]
    public void UnknownCharactersAreRejected() => Assert.Null(KeyMatrix.ForChar('~'));

    [Fact]
    public void BuildsPressAndReleaseCommands()
    {
        Assert.Equal((0x01, 0x7F), KeyMatrix.For(SpecialKey.Return));
        Assert.Equal(["sffd3615 01 7f\r", "sffd3615 7f 7f 7f\r"], KeyMatrix.Commands(0x01, 0x7F));
        // Shift first on its own, then shift + key, then release.
        Assert.Equal(["sffd3615 0f 7f\r", "sffd3615 0f 37\r", "sffd3615 7f 7f 7f\r"], KeyMatrix.Commands(0x37, 0x0F));
    }
}

public class SelfTestProgramTests
{
    [Fact]
    public void BuildsAValidBasic65Program()
    {
        var prg = HardwareSelfTest.BasicPrintPrg("HI");
        Assert.Equal(new byte[] { 0x01, 0x20 }, prg[..2]);            // load address $2001
        var next = prg[2] | prg[3] << 8;
        Assert.Equal(0x2001 + prg.Length - 2 - 2, next);              // link points at the end-of-program marker
        Assert.Equal(0x99, prg[6]);                                   // PRINT token
        Assert.Equal(new byte[] { 0, 0, 0 }, prg[^3..]);              // end of line, end of program
    }
}

public class SelfTestParsingTests
{
    [Fact]
    public void StripsAnsiColourCodesFromScreenText()
    {
        const string raw = "\u001b[48;2;0;0;240m \u001b[38;2;240;240;240mR\u001b[38;2;240;240;240mE\u001b[38;2;240;240;240mA\u001b[38;2;240;240;240mD\u001b[38;2;240;240;240mY\u001b[38;2;240;240;240m.\u001b[0m";
        Assert.Contains("READY.", HardwareSelfTest.PlainScreen(raw));
    }

    [Fact]
    public void FtpProblemsPrintedAsOutputAreErrors() =>
        Assert.Single(FtpSession.OutputProblems("x\nTODO: Unable to delete directories as yet. Raise a ticket\n"));
}

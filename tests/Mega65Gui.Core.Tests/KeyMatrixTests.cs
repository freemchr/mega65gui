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
        Assert.Equal(("sffd3615 01 7f 7f\r", "sffd3615 7f 7f 7f\r"), KeyMatrix.Commands(0x01, 0x7F));
    }
}

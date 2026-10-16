using System.IO.Pipelines;
using System.Text;
using Mega65Gui.Core.Device;

namespace Mega65Gui.Core.Tests;

public class FtpSessionTests
{
    [Fact]
    public async Task ReadsOutputUpToThePromptAndTracksTheDirectory()
    {
        var pipe = new Pipe();
        var typed = new StringWriter();
        var session = new FtpSession(typed, pipe.Reader.AsStream());

        async Task Send(string s) => await pipe.Writer.WriteAsync(Encoding.Latin1.GetBytes(s));

        await Send("NOTE fast SD card access routine installed\nMEGA65 SD-Card:/> ");
        Assert.Contains("routine installed", await session.WaitForPromptAsync(TimeSpan.FromSeconds(5), default));

        var cd = session.ExecuteAsync("cd \"doc\"");
        await Send("MEGA65 SD-Card:/doc> ");
        var reply = await cd;
        Assert.Equal("/doc", reply.CurrentDirectory);
        Assert.Equal("cd \"doc\"\n", typed.ToString());

        // Output arriving in pieces, with a dir listing before the prompt.
        var dir = session.ListAsync();
        await Send("     74 | 2026-07-23 07:19:34  | AEXP-V1.CFG  | aexp-V1.cfg\n");
        await Send("   <DIR> | 2026-07-23 07:19:34  | DOCS     | \n1 Dir(s), 1 File(s)\nMEGA65 SD-");
        await Send("Card:/doc> ");
        var entries = await dir;
        Assert.Equal(2, entries.Count);
        Assert.Contains(entries, e => e.IsDirectory && e.Name == "DOCS");
    }

    [Theory]
    [InlineData("file.prg", "\"file.prg\"")]
    [InlineData("my game.d81", "\"my game.d81\"")]
    public void QuotesNames(string name, string quoted) => Assert.Equal(quoted, FtpSession.Quote(name));
}

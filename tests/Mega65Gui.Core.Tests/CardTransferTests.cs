using Mega65Gui.Core.Common;
using Mega65Gui.Core.Cores;
using Mega65Gui.Core.Device;
using Mega65Gui.Core.Roms;
using Mega65Gui.Core.Sd;

namespace Mega65Gui.Core.Tests;

public class CardTransferTests : IDisposable
{
    private readonly TempDir _tmp = new();
    public void Dispose() => _tmp.Dispose();

    /// <summary>The card inside the MEGA65 as folders and files in memory; entries have 8.3 names in capitals.</summary>
    private sealed class Card : ICardTree
    {
        public Dictionary<string, byte[]> Files { get; } = new(StringComparer.OrdinalIgnoreCase);
        public HashSet<string> Dirs { get; } = new(StringComparer.OrdinalIgnoreCase) { "" };
        public List<string> Uploaded { get; } = [];
        public string Description => "card";

        private static string Short(string name) => name.ToUpperInvariant().Replace(' ', '_');

        public Task<IReadOnlyList<RemoteEntry>?> ListAsync(string dir, CancellationToken ct)
        {
            // Folders may be named by their short or long name, as mega65_ftp's cd accepts.
            var real = Dirs.FirstOrDefault(d => string.Equals(Short(d), Short(dir), StringComparison.Ordinal));
            if (real is null) return Task.FromResult<IReadOnlyList<RemoteEntry>?>(null);
            string Child(string p) => p[(real.Length == 0 ? 0 : real.Length + 1)..];
            bool In(string p) => p.Length > 0 && (real.Length == 0 ? !p.Contains('/') : p.StartsWith(real + "/", StringComparison.OrdinalIgnoreCase) && !Child(p).Contains('/'));
            IReadOnlyList<RemoteEntry> list = Dirs.Where(In).Select(d => new RemoteEntry(Short(Child(d)), Child(d), 0, true))
                .Concat(Files.Where(f => In(f.Key)).Select(f => new RemoteEntry(Short(Child(f.Key)), Child(f.Key), f.Value.Length, false))).ToList();
            return Task.FromResult<IReadOnlyList<RemoteEntry>?>(list);
        }

        public Task MakeDirectoryAsync(string parent, string name, CancellationToken ct)
        {
            // As FtpCardTree: the parent folder has to be there.
            if (!Dirs.Contains(parent)) throw new IOException($"The folder /{parent} is not on the card.");
            Dirs.Add(parent.Length == 0 ? name : parent + "/" + name);
            return Task.CompletedTask;
        }

        public Task UploadAsync(string localPath, string dir, string name, CancellationToken ct)
        {
            var path = (dir.Length == 0 ? "" : dir + "/") + name;
            Files[path] = File.ReadAllBytes(localPath);
            Uploaded.Add(path);
            return Task.CompletedTask;
        }

        public Task DownloadAsync(string dir, RemoteEntry entry, string localPath, CancellationToken ct)
        {
            var real = Dirs.First(d => string.Equals(Short(d), Short(dir), StringComparison.Ordinal));
            File.WriteAllBytes(localPath, Files[(real.Length == 0 ? "" : real + "/") + entry.Name]);
            return Task.CompletedTask;
        }
    }

    private string LocalGame()
    {
        var root = Path.Combine(_tmp.Path, "pc", "My Game");
        Directory.CreateDirectory(Path.Combine(root, "levels"));
        File.WriteAllText(Path.Combine(root, "game.prg"), "prg");
        File.WriteAllText(Path.Combine(root, "levels", "one.seq"), "level 1");
        var hidden = Path.Combine(root, "desktop.ini");
        File.WriteAllText(hidden, "explorer");
        File.SetAttributes(hidden, FileAttributes.Hidden | FileAttributes.System);
        return root;
    }

    [Fact]
    public async Task Copies_a_whole_folder_to_the_card_keeping_its_layout()
    {
        var card = new Card();
        var progress = new List<TransferProgress>();
        var r = await new CardTransfer(card) { Progress = new SyncProgress<TransferProgress>(progress.Add) }.UploadAsync([LocalGame()], "");

        Assert.Equal(2, r.Copied);
        Assert.Equal(2, r.FoldersMade);
        Assert.Equal("prg", System.Text.Encoding.ASCII.GetString(card.Files["My Game/game.prg"]));
        Assert.True(card.Files.ContainsKey("My Game/levels/one.seq"));
        Assert.DoesNotContain(card.Files.Keys, k => k.EndsWith("desktop.ini"));
        Assert.Equal(1.0, progress[^1].Fraction);
    }

    [Fact]
    public async Task Writes_capital_names_when_asked()
    {
        var card = new Card();
        await new CardTransfer(card) { UpperCaseNames = true }.UploadAsync([LocalGame()], "");
        Assert.Contains("MY GAME/LEVELS/ONE.SEQ", card.Uploaded);
    }

    [Fact]
    public async Task Asks_about_each_clash_until_an_all_answer()
    {
        var card = new Card();
        var game = LocalGame();
        await new CardTransfer(card).UploadAsync([game], "");
        card.Uploaded.Clear();

        var asked = new List<string>();
        var r = await new CardTransfer(card) { AskOverwrite = w => { asked.Add(w); return Task.FromResult(OverwriteChoice.SkipAll); } }.UploadAsync([game], "");
        Assert.Single(asked); // "Skip all" answers for the second clash too
        Assert.Equal(0, r.Copied);
        Assert.Equal(2, r.Skipped);
        Assert.Empty(card.Uploaded);

        var replaced = await new CardTransfer(card) { AskOverwrite = _ => Task.FromResult(OverwriteChoice.Replace) }.UploadAsync([game], "");
        Assert.Equal(2, replaced.Copied);
        Assert.Equal(0, replaced.FoldersMade); // the folders were already there
    }

    [Fact]
    public async Task Cancel_and_stop_end_the_copy_between_files()
    {
        var card = new Card();
        var game = LocalGame();
        await new CardTransfer(card).UploadAsync([game], "");
        var cancelled = await new CardTransfer(card) { AskOverwrite = _ => Task.FromResult(OverwriteChoice.Cancel) }.UploadAsync([game], "");
        Assert.True(cancelled.Stopped);
        Assert.Equal(0, cancelled.Copied);

        using var stop = new CancellationTokenSource();
        stop.Cancel();
        var stopped = await new CardTransfer(new Card()).UploadAsync([game], "", stop.Token);
        Assert.True(stopped.Stopped);
    }

    [Fact]
    public async Task A_folder_that_cannot_be_made_fails_its_files_but_not_the_whole_copy()
    {
        var card = new Card();
        card.Files["My Game"] = "a file in the way"u8.ToArray();
        var loose = _tmp.File("loose.prg", "x"u8.ToArray());
        var r = await new CardTransfer(card).UploadAsync([LocalGame(), loose], "");

        Assert.Equal(1, r.Copied); // loose.prg
        Assert.Equal(2, r.Failed);
        Assert.Equal(0, r.Skipped);
        Assert.Contains(r.Problems, p => p.Contains("in the way"));
    }

    [Fact]
    public async Task Copies_card_folders_to_the_pc()
    {
        var card = new Card();
        card.Dirs.Add("games");
        card.Dirs.Add("games/disks");
        card.Files["games/menu.prg"] = "menu"u8.ToArray();
        card.Files["games/disks/Side 1.d81"] = new byte[10];
        card.Files["MEGA65.ROM"] = "rom"u8.ToArray();
        var pc = Path.Combine(_tmp.Path, "down");
        Directory.CreateDirectory(pc);

        var root = (await card.ListAsync("", default))!;
        var r = await new CardTransfer(card).DownloadAsync(root.Where(e => e.Name == "games").ToList(), "", pc);

        Assert.Equal(2, r.Copied);
        Assert.Equal("menu", File.ReadAllText(Path.Combine(pc, "games", "menu.prg")));
        Assert.True(File.Exists(Path.Combine(pc, "games", "disks", "Side 1.d81")));
        Assert.False(File.Exists(Path.Combine(pc, "MEGA65.ROM")));
    }

    [Fact]
    public void Card_names_are_made_safe_for_windows() =>
        Assert.Equal("A_B_.PRG", CardTransfer.LocalName("A:B?.PRG"));

    [Theory]
    [InlineData("$GAME.PRG", 10, "directory listing")]
    [InlineData("GAME,1.PRG", 10, "commas")]
    [InlineData("EMPTY.PRG", 0, "empty")]
    [InlineData("SHORT.D81", 1000, "not 819,200")]
    [InlineData("MY LONG DISK.D81", 819200, "short name")]
    [InlineData("CAFÉ.PRG", 10, "accented")]
    public void Points_out_names_that_trouble_the_mega65(string name, long size, string expected) =>
        Assert.Contains(CardNames.Warnings([(name, size, false)]), w => w.Contains(expected));

    [Fact]
    public void Ordinary_names_need_no_warning() =>
        Assert.Empty(CardNames.Warnings([("GAME.D81", 819200, false), ("MEGA65.ROM", 131072, false), ("GAMES", 0, true)]));

    [Fact]
    public void Reads_free_space_from_df() =>
        Assert.Equal((1234L, 7600L), CardSpace.Parse("This will take a while on larger cards.\nsdcard totals: 1234 out of 7600 MB free (315904 out of 1945600 Cluster(s) free)\n"));

    /// <summary>Progress&lt;T&gt; reports on the thread pool; tests need the reports in order and at once.</summary>
    private sealed class SyncProgress<T>(Action<T> report) : IProgress<T>
    {
        public void Report(T value) => report(value);
    }
}

public class SendOptionsAndKeysTests
{
    private static readonly ConnectionTarget Com = ConnectionTarget.Serial("COM5");
    private static readonly ConnectionTarget Eth = ConnectionTarget.Ethernet("fe80::1%eth0");

    [Fact]
    public void Prg_options_become_m65_flags()
    {
        var (tool, args) = Mega65Commands.SendPrgArgs(Com, "a.prg", new PrgOptions(Run: true, ResetFirst: false, UseFileLoadAddress: true, Pal: false));
        Assert.Equal("m65", tool);
        Assert.Equal(["-l", "COM5", "-s", "2000000", "-1", "-n", "-r", "a.prg"], args);
        // The defaults are what the app always sent before.
        Assert.Equal(Mega65Commands.SendPrgArgs(Com, "a.prg", true, false).Args, Mega65Commands.SendPrgArgs(Com, "a.prg", new PrgOptions()).Args);
    }

    [Fact]
    public void Ethernet_can_mount_a_disk_with_the_program()
    {
        var (tool, args) = Mega65Commands.SendPrgArgs(Eth, "a.prg", new PrgOptions(C64Mode: true, Pal: true, MountD81: "GAME.D81"));
        Assert.Equal("etherload", tool);
        Assert.Equal(["-i", "fe80::1%eth0", "-4", "--pal", "-m", "GAME.D81", "-r", "a.prg"], args);
        Assert.Throws<NotSupportedException>(() => Mega65Commands.SendPrgArgs(Com, "a.prg", new PrgOptions(MountD81: "GAME.D81")));
    }

    [Fact]
    public void Serves_a_disk_and_switches_modes_without_a_reset()
    {
        Assert.Equal(["-l", "COM5", "-s", "2000000", "-d", @"C:\disks\game.d81"], Mega65Commands.VirtualD81Args(Com, @"C:\disks\game.d81").Args);
        Assert.DoesNotContain("-F", Mega65Commands.Go64Args(Com).Args);
        Assert.Contains("-4", Mega65Commands.Go64Args(Com).Args);
        Assert.Throws<NotSupportedException>(() => Mega65Commands.VirtualD81Args(Eth, "x.d81"));
        Assert.Equal("sys58552\n.\n", Mega65Commands.PrepareBasicListing(Mega65Commands.Go65Command));
    }

    [Fact]
    public void Mcs_for_a_slot_starts_at_the_slot_address()
    {
        Assert.Equal(["core.cor", "out.mcs", "1000000"], Mega65Commands.Bit2McsArgs("core.cor", "out.mcs", 2).Args);
        Assert.Equal(["a.bit", "a.mcs"], Mega65Commands.Bit2McsArgs("a.bit", "a.mcs", null).Args);
        Assert.Throws<ArgumentOutOfRangeException>(() => Mega65Commands.Bit2McsArgs("c.cor", "o.mcs", 8));
    }

    [Theory]
    [InlineData(SpecialKey.Escape, 0x47, KeyMatrix.None)]
    [InlineData(SpecialKey.Tab, 0x41, KeyMatrix.None)]
    [InlineData(SpecialKey.Help, 0x43, KeyMatrix.None)]
    [InlineData(SpecialKey.NoScroll, 0x40, KeyMatrix.None)]
    [InlineData(SpecialKey.F9, 0x44, KeyMatrix.None)]
    [InlineData(SpecialKey.F10, 0x44, KeyMatrix.LeftShift)]
    [InlineData(SpecialKey.F14, 0x46, KeyMatrix.LeftShift)]
    public void Knows_the_mega65_extra_keys(SpecialKey key, byte code, byte modifier) =>
        Assert.Equal((code, modifier), KeyMatrix.For(key));

    [Fact]
    public void Ctrl_with_a_key_presses_ctrl_first()
    {
        var ctrlA = KeyMatrix.With(KeyMatrix.ForChar('a')!.Value, KeyMatrix.Ctrl);
        Assert.Equal(["sffd3615 3a 7f\r", "sffd3615 3a 0a\r", "sffd3615 7f 7f 7f\r"], KeyMatrix.Commands(ctrlA.Key, ctrlA.Modifier));
    }
}

public class RomPresetAndPatchTests : IDisposable
{
    private readonly TempDir _tmp = new();
    public void Dispose() => _tmp.Dispose();

    [Fact]
    public void Settings_survive_a_preset_file()
    {
        var s = new RomSettings((byte)RomSpeed.Mhz40, (byte)'E', 1, 6, 6, 2, 13, 7);
        Assert.Equal(s, RomSettings.FromPreset(s.ToPreset()));
    }

    [Fact]
    public void A_preset_with_impossible_values_is_refused() =>
        Assert.Throws<InvalidDataException>(() => RomSettings.FromPreset(new RomSettings(9, (byte)'A', 1, 6, 6, 2, 13, 7).ToPreset()));

    [Fact]
    public async Task Makes_a_patch_with_romdiff()
    {
        var original = _tmp.File("920395.ROM", new byte[RomFile.Size]);
        var changed = _tmp.File("MY.ROM", new byte[RomFile.Size]);
        var output = Path.Combine(_tmp.Path, "my.rdf");
        var runner = new FakeRunner { OnRun = a => File.WriteAllText(a[2], "patch") };
        var r = await new RomPatcher(runner).CreateAsync("romdiff", original, changed, output);
        Assert.True(r.Succeeded);
        Assert.Equal([original, changed, output], runner.Calls.Single().Args);
    }

    [Fact]
    public async Task Refuses_an_original_name_romdiff_cannot_store()
    {
        var original = _tmp.File(new string('a', 28) + ".ROM", new byte[RomFile.Size]);
        await Assert.ThrowsAsync<InvalidDataException>(() => new RomPatcher(new FakeRunner()).CreateAsync("romdiff", original, original, "x.rdf"));
    }
}

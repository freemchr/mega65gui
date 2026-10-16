using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using Mega65Gui.Core.Sd;

namespace Mega65Gui.Core.Device;

/// <summary>Result of one command typed into mega65_ftp.</summary>
public sealed record FtpReply(string Command, string Output, string CurrentDirectory, IReadOnlyList<string> Errors)
{
    public bool Failed => Errors.Count > 0;
}

/// <summary>
/// An interactive mega65_ftp session: the tool is started once and commands are typed at its
/// prompt ("MEGA65 SD-Card:/dir> "), so the MEGA65 is not reset after every operation. The
/// session holds the serial port; close it before other device operations. Closing sends "exit",
/// which unloads the helper and resets the MEGA65.
/// </summary>
public sealed partial class FtpSession : IAsyncDisposable
{
    [GeneratedRegex(@"MEGA65 SD-Card:(?<dir>[^\r\n]*?)> $")]
    private static partial Regex Prompt();

    private readonly Process? _process;
    private readonly TextWriter _input;
    private readonly Stream _output;
    private readonly StringBuilder _buffer = new();
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly byte[] _chunk = new byte[4096];
    private readonly List<string> _errors = [];

    public string CurrentDirectory { get; private set; } = "/";
    public DateTime LastUsed { get; private set; } = DateTime.Now;
    public bool IsOpen => _process is null || !_process.HasExited;
    public event Action<string>? Log;

    /// <summary>For tests: drive the session over arbitrary streams.</summary>
    internal FtpSession(TextWriter input, Stream output)
    {
        _input = input;
        _output = output;
    }

    private FtpSession(Process process)
    {
        _process = process;
        _input = process.StandardInput;
        _output = process.StandardOutput.BaseStream;
        process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is not { Length: > 0 } line) return;
            Log?.Invoke(line);
            // mega65_ftp logs "… ERRO message" / "… CRIT message" on stderr.
            if (line.Contains(" ERRO ") || line.Contains(" CRIT "))
                lock (_errors) _errors.Add(line[(line.IndexOf(line.Contains(" ERRO ") ? " ERRO " : " CRIT ", StringComparison.Ordinal) + 6)..].Trim());
        };
        process.BeginErrorReadLine();
    }

    public static async Task<FtpSession> StartAsync(string exe, IReadOnlyList<string> args, CancellationToken ct = default)
    {
        var psi = new ProcessStartInfo(exe)
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.Latin1,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        var p = Process.Start(psi) ?? throw new IOException("mega65_ftp could not be started.");
        var session = new FtpSession(p);
        try
        {
            await session.WaitForPromptAsync(TimeSpan.FromSeconds(60), ct);
        }
        catch
        {
            await session.DisposeAsync();
            throw;
        }
        return session;
    }

    /// <summary>Types a command and returns everything printed before the next prompt.</summary>
    public async Task<FtpReply> ExecuteAsync(string command, TimeSpan? timeout = null, CancellationToken ct = default)
    {
        if (command.Contains('\n') || command.Contains('\r')) throw new ArgumentException("Commands must be a single line.", nameof(command));
        await _gate.WaitAsync(ct);
        try
        {
            LastUsed = DateTime.Now;
            lock (_errors) _errors.Clear();
            await _input.WriteAsync(command + "\n");
            await _input.FlushAsync(ct);
            var output = await WaitForPromptAsync(timeout ?? TimeSpan.FromMinutes(10), ct);
            await Task.Delay(60, ct); // stderr can trail the prompt slightly
            LastUsed = DateTime.Now;
            string[] errors;
            lock (_errors) errors = [.. _errors];
            return new FtpReply(command, output, CurrentDirectory, errors);
        }
        finally
        {
            _gate.Release();
        }
    }

    internal async Task<string> WaitForPromptAsync(TimeSpan timeout, CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);
        while (true)
        {
            var text = _buffer.ToString();
            var m = Prompt().Match(text);
            if (m.Success)
            {
                CurrentDirectory = m.Groups["dir"].Value.Length > 0 ? m.Groups["dir"].Value : "/";
                _buffer.Clear();
                return text[..m.Index];
            }
            int n;
            try
            {
                n = await _output.ReadAsync(_chunk, cts.Token);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                throw new TimeoutException("mega65_ftp did not respond. Is the MEGA65 switched on and running its core?");
            }
            if (n == 0) throw new IOException("mega65_ftp stopped: " + _buffer.ToString().Trim());
            _buffer.Append(Encoding.Latin1.GetString(_chunk, 0, n));
        }
    }

    /// <summary>Quotes a name for mega65_ftp's command parser.</summary>
    public static string Quote(string name) =>
        name.Contains('"') ? throw new ArgumentException("Names containing quotes are not supported.") : $"\"{name}\"";

    // ---- convenience operations ----

    public async Task<IReadOnlyList<RemoteEntry>> ListAsync(CancellationToken ct = default) =>
        Mega65FtpCard.ParseDir((await ExecuteAsync("dir", null, ct)).Output)
            .Where(e => e.ShortName is not "." and not "..")
            .ToList();

    public Task<FtpReply> ChangeDirectoryAsync(string name, CancellationToken ct = default) => ExecuteAsync($"cd {Quote(name)}", null, ct);
    public Task<FtpReply> DownloadAsync(string remote, string localPath, CancellationToken ct = default) => ExecuteAsync($"get {Quote(remote)} {Quote(localPath)}", null, ct);
    public Task<FtpReply> UploadAsync(string localPath, string remote, CancellationToken ct = default) => ExecuteAsync($"put {Quote(localPath)} {Quote(remote)}", null, ct);
    public Task<FtpReply> DeleteAsync(string remote, CancellationToken ct = default) => ExecuteAsync($"del {Quote(remote)}", null, ct);
    public Task<FtpReply> RenameAsync(string from, string to, CancellationToken ct = default) => ExecuteAsync($"rename {Quote(from)} {Quote(to)}", null, ct);
    public Task<FtpReply> MakeDirectoryAsync(string name, CancellationToken ct = default) => ExecuteAsync($"mkdir {Quote(name)}", null, ct);
    public Task<FtpReply> MountAsync(string d81, CancellationToken ct = default) => ExecuteAsync($"mount {Quote(d81)}", null, ct);
    public Task<FtpReply> ClustersAsync(string remote, CancellationToken ct = default) => ExecuteAsync($"clusters {Quote(remote)}", null, ct);
    public Task<FtpReply> FreeSpaceAsync(CancellationToken ct = default) => ExecuteAsync("df", null, ct);

    public async ValueTask DisposeAsync()
    {
        if (_process is null) return;
        try
        {
            if (!_process.HasExited)
            {
                await _input.WriteAsync("exit\n");
                await _input.FlushAsync();
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                try { await _process.WaitForExitAsync(cts.Token); }
                catch (OperationCanceledException) { _process.Kill(entireProcessTree: true); }
            }
        }
        catch (IOException) { /* process already gone */ }
        finally
        {
            _process.Dispose();
        }
    }
}

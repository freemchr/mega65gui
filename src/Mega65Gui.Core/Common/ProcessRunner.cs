using System.Diagnostics;
using System.Text;

namespace Mega65Gui.Core.Common;

public sealed record ProcessResult(int ExitCode, string StdOut, string StdErr, TimeSpan Duration)
{
    public bool Succeeded => ExitCode == 0;
}

/// <summary>Runs external tools (m65, mega65_ftp, etherload). Abstracted so device code is testable.</summary>
public interface IProcessRunner
{
    Task<ProcessResult> RunAsync(string fileName, IReadOnlyList<string> args, TimeSpan timeout,
        IProgress<string>? output = null, CancellationToken ct = default);
}

public sealed class ProcessRunner : IProcessRunner
{
    public async Task<ProcessResult> RunAsync(string fileName, IReadOnlyList<string> args, TimeSpan timeout,
        IProgress<string>? output = null, CancellationToken ct = default)
    {
        var psi = new ProcessStartInfo(fileName)
        {
            // Tools must never wait for keyboard input; stdin is redirected and closed at once.
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);

        using var proc = new Process { StartInfo = psi };
        var stdout = new StringBuilder();
        var stderr = new StringBuilder();
        proc.OutputDataReceived += (_, e) => { if (e.Data is null) return; stdout.AppendLine(e.Data); output?.Report(e.Data); };
        proc.ErrorDataReceived += (_, e) => { if (e.Data is null) return; stderr.AppendLine(e.Data); output?.Report(e.Data); };

        var sw = Stopwatch.StartNew();
        proc.Start();
        proc.StandardInput.Close();
        proc.BeginOutputReadLine();
        proc.BeginErrorReadLine();

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(timeout);
        try
        {
            await proc.WaitForExitAsync(timeoutCts.Token);
        }
        catch (OperationCanceledException)
        {
            try { proc.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
            if (ct.IsCancellationRequested) throw;
            return new ProcessResult(-1, stdout.ToString(), stderr + $"\nTimed out after {timeout}.", sw.Elapsed);
        }
        return new ProcessResult(proc.ExitCode, stdout.ToString(), stderr.ToString(), sw.Elapsed);
    }
}

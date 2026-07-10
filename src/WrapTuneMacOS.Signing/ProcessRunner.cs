using System.Diagnostics;
using System.Text;

namespace WrapTuneMacOS.Signing;

/// <summary>
/// Runs an external process and captures its output. Arguments are passed via
/// <see cref="ProcessStartInfo.ArgumentList"/> (never a shell command line), so
/// paths with spaces and shell metacharacters cannot be misinterpreted or
/// injected. A cancelled or timed-out run kills the child process tree —
/// disposing a <see cref="Process"/> only releases handles, it never
/// terminates the OS process.
/// </summary>
internal static class ProcessRunner
{
    public static async Task<(int ExitCode, string StdOut, string StdErr)> RunAsync(
        string fileName, IReadOnlyList<string> args, CancellationToken ct = default,
        TimeSpan? timeout = null)
    {
        var psi = new ProcessStartInfo
        {
            FileName = fileName,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);

        using var proc = new Process { StartInfo = psi };
        var stdout = new StringBuilder();
        var stderr = new StringBuilder();
        proc.OutputDataReceived += (_, e) => { if (e.Data is not null) stdout.AppendLine(e.Data); };
        proc.ErrorDataReceived += (_, e) => { if (e.Data is not null) stderr.AppendLine(e.Data); };

        proc.Start();
        proc.BeginOutputReadLine();
        proc.BeginErrorReadLine();

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);
        if (timeout is { } t) linked.CancelAfter(t);
        try
        {
            await proc.WaitForExitAsync(linked.Token);
        }
        catch (OperationCanceledException)
        {
            try { proc.Kill(entireProcessTree: true); } catch { /* already exited */ }
            ct.ThrowIfCancellationRequested();   // caller-initiated cancel → propagate
            throw new TimeoutException(
                $"{Path.GetFileName(fileName)} did not finish within {timeout!.Value.TotalSeconds:0} seconds.");
        }

        return (proc.ExitCode, stdout.ToString(), stderr.ToString());
    }
}

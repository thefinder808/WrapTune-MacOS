using System.Diagnostics;
using System.Text;

namespace WrapTuneMacOS.Signing;

/// <summary>
/// Runs an external process and captures its output. Arguments are passed via
/// <see cref="ProcessStartInfo.ArgumentList"/> (never a shell command line), so
/// paths with spaces and shell metacharacters cannot be misinterpreted or
/// injected.
/// </summary>
internal static class ProcessRunner
{
    public static async Task<(int ExitCode, string StdOut, string StdErr)> RunAsync(
        string fileName, IReadOnlyList<string> args, CancellationToken ct = default)
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
        await proc.WaitForExitAsync(ct);

        return (proc.ExitCode, stdout.ToString(), stderr.ToString());
    }
}

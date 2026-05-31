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
        string fileName, IReadOnlyList<string> args, CancellationToken ct = default,
        IReadOnlyDictionary<string, string>? environment = null)
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

        // Extra env vars apply only to this child process. Used to pass secrets
        // (e.g. an Azure token via `--storepass env:VAR`) without exposing them in
        // the argument list, which is visible to other local users via `ps`.
        if (environment is not null)
            foreach (var (k, v) in environment) psi.Environment[k] = v;

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

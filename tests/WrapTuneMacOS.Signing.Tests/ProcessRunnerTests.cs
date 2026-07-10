using WrapTuneMacOS.Signing;

namespace WrapTuneMacOS.Signing.Tests;

/// <summary>
/// Behavior tests for the process helper, driven by tiny stub shell scripts.
/// The contract: a cancelled or timed-out run must never leave the child
/// process alive (dispose only releases handles — it does not terminate).
/// </summary>
public sealed class ProcessRunnerTests
{
    private static string WriteStub(string body)
    {
        var path = Path.Combine(Path.GetTempPath(), "wraptune-stub-" + Guid.NewGuid().ToString("N") + ".sh");
        File.WriteAllText(path, "#!/bin/sh\n" + body);
        File.SetUnixFileMode(path,
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        return path;
    }

    [Fact]
    public async Task Captures_exit_code_and_output()
    {
        var stub = WriteStub("echo out-line\necho err-line >&2\nexit 3\n");
        try
        {
            var (exit, stdout, stderr) = await ProcessRunner.RunAsync(stub, []);
            Assert.Equal(3, exit);
            Assert.Contains("out-line", stdout);
            Assert.Contains("err-line", stderr);
        }
        finally { File.Delete(stub); }
    }

    [Fact]
    public async Task Timeout_kills_a_hung_process_and_reports_it()
    {
        var stub = WriteStub("sleep 30\n");
        try
        {
            var ex = await Assert.ThrowsAsync<TimeoutException>(
                () => ProcessRunner.RunAsync(stub, [], default, TimeSpan.FromMilliseconds(300)));
            Assert.Contains("did not finish", ex.Message);
        }
        finally { File.Delete(stub); }
    }

    [Fact]
    public async Task Cancellation_kills_the_child_process()
    {
        // The stub proves it survived by touching a marker after its sleep. A
        // properly killed child never gets there.
        var marker = Path.Combine(Path.GetTempPath(), "wraptune-marker-" + Guid.NewGuid().ToString("N"));
        var stub = WriteStub("sleep 2\ntouch \"$1\"\n");
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(300));
            await Assert.ThrowsAnyAsync<OperationCanceledException>(
                () => ProcessRunner.RunAsync(stub, [marker], cts.Token));

            await Task.Delay(TimeSpan.FromSeconds(3));
            Assert.False(File.Exists(marker), "the child kept running after cancellation");
        }
        finally
        {
            File.Delete(stub);
            if (File.Exists(marker)) File.Delete(marker);
        }
    }
}

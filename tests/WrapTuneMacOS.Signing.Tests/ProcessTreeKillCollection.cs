namespace WrapTuneMacOS.Signing.Tests;

/// <summary>
/// Every test that makes <c>ProcessRunner</c> kill a child's process tree
/// belongs in this collection, which xUnit runs on its own — never alongside
/// another test class.
///
/// <para>Why it matters. <c>Process.Kill(entireProcessTree: true)</c> sends
/// each process SIGSTOP before it sends SIGKILL, and the script it kills leaves
/// its own child (the stub's <c>sleep</c>) behind as an orphan, which is then
/// killed too. Unix has a rule for exactly that mixture: when a process group
/// becomes orphaned while any member is stopped, the kernel sends SIGHUP to
/// every process in the group. One tree kill on its own never has a stopped
/// process left when its orphan dies. Two at once do — the second test's
/// script is stopped at the moment the first test's orphan exits.</para>
///
/// <para>On a GitHub macOS runner the test host, the step's shell and the
/// runner's own agent all share one process group, so that SIGHUP took the
/// agent down with it: no timeout fired, no log was uploaded, and the job sat
/// until its limit. It only bites the first time an orphan exits on a given
/// runner, which made it look random — roughly one CI run in three on
/// 2026-11-20, and the unexplained stalls of 2026-07, 2026-08-04 and
/// 2026-10-27. Reproduced in plain shell on fresh runners that day, with no
/// .NET involved.</para>
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class ProcessTreeKillCollection
{
    public const string Name = "Kills child process trees";
}

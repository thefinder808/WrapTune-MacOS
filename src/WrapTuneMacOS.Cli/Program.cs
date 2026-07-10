using WrapTuneMacOS.Cli;

// Thin entry point — all behavior lives in Cli.RunAsync so tests drive the
// exact code path users hit, without spawning processes.
using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

return await Cli.RunAsync(args, Console.Out, Console.Error, cts.Token);

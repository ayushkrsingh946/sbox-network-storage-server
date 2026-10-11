namespace SboxNetworkStorage.Cli.Tests;

// Test classes that touch process-wide state race with each other when xUnit runs classes in parallel:
// - Console.SetOut/SetError redirects: one class's redirect steals another class's output (or restores it
//   mid-flight, leaving an empty capture). The CLI writes straight to Console, so there is no writer to inject.
// - Writing a script and then executing it: a Process.Start in another class forks while the script is still
//   open for writing, and the exec fails with ETXTBSY ("Text file busy").
// Every such class joins this collection, which xUnit runs on its own after the parallel classes.
[CollectionDefinition("Console", DisableParallelization = true)]
public sealed class ConsoleCollection;

// These tests share process-global state — Application.Current's theme variant
// and the static AppSettings.BaseDirOverride test seam — so parallel test
// collections can race each other (the 2026-07-10 CI flake: a [Fact] settings
// test flipped the override mid-flight under an [AvaloniaFact] theme assert).
// The suite runs in seconds; serialize it.
[assembly: CollectionBehavior(DisableTestParallelization = true)]

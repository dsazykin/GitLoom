using Xunit;

// Test parallelization is disabled assembly-wide, deliberately. The suite mixes two kinds of test
// that are unsafe to run concurrently:
//   1. [AvaloniaFact] render harnesses + ViewModel tests share ONE global headless Avalonia
//      Application/Dispatcher (see TestAppBuilder) — driving it from multiple threads races.
//   2. Integration tests spawn real git (and, for interactive rebase, the built GitLoom.App as
//      GIT_SEQUENCE_EDITOR) against per-test temp repos; heavy concurrent process spawning made a
//      random one time out under load.
// Running collections in parallel surfaced an intermittent ~1-in-3 failure of a *different* random
// test each run. Serializing the whole assembly makes the suite deterministic. Keep this unless the
// UI tests are isolated onto their own single-threaded collection and the process-spawn tests are
// throttled — until then, do not re-enable parallelization.
[assembly: CollectionBehavior(DisableTestParallelization = true)]

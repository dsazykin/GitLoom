// The daemon in-proc tier binds hosts, temp token files, and free ports; keep it
// deterministic and non-overlapping (mirrors Mainguard.Tests' policy).
[assembly: CollectionBehavior(DisableTestParallelization = true)]

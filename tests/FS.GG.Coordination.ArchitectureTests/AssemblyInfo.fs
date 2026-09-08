namespace FS.GG.Coordination.ArchitectureAssembly

open Xunit

// Many architecture collections launch independent dotnet/fsi child processes.
// Bound collection concurrency so the host cannot abort otherwise-correct gates
// under a transient process or memory spike. Two threads leave capacity for the
// candidate-level unit, architecture and formal partitions that run concurrently.
[<assembly: CollectionBehavior(MaxParallelThreads = 2)>]
do ()

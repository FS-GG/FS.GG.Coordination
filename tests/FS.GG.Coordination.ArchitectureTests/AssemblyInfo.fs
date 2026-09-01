namespace FS.GG.Coordination.ArchitectureAssembly

open Xunit

// Many architecture collections launch independent dotnet/fsi child processes.
// Bound collection concurrency so the host cannot abort otherwise-correct gates
// under a transient process or memory spike.
[<assembly: CollectionBehavior(MaxParallelThreads = 4)>]
do ()

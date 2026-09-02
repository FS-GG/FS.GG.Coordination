namespace FS.GG.Coordination.ArchitectureAssembly

open Xunit

// Many architecture collections launch independent dotnet/fsi child processes.
// Bound collection concurrency so nested source-mutation validators and the
// other process-backed gates fit on the two-core hosted qualification runner.
[<assembly: CollectionBehavior(MaxParallelThreads = 2)>]
do ()

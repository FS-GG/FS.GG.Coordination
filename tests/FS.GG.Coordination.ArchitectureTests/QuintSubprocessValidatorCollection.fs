namespace FS.GG.Coordination.ArchitectureCollections

open Xunit

// These retained Q2 gates each launch several Quint subprocesses. Keep them out of
// the architecture suite's parallel collections so hosted resource pressure cannot
// turn a valid evidence contract into an opaque child-process failure.
[<CollectionDefinition("Quint subprocess validators", DisableParallelization = true)>]
type QuintSubprocessValidatorCollection() = class end

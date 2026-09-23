namespace FS.GG.Coordination.GitHub

open System

[<RequireQualifiedAccess>]
module V1AdmissionGenesisSourceRead =
    /// Decode a fresh, stable main-ref census and native compare response. An unmerged source
    /// is represented by IsOnMain=false; malformed or contradictory evidence is refused.
    val decode:
        asOf: DateTimeOffset ->
        raw: ReadOnlyMemory<byte> ->
            Result<GenesisSourceRead, string list>

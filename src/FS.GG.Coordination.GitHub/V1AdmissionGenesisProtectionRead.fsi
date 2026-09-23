namespace FS.GG.Coordination.GitHub

open System

[<RequireQualifiedAccess>]
module V1AdmissionGenesisProtectionRead =
    /// Decode fresh, exact effective branch rules and a verified ordinary-App token scope.
    /// Missing bypass visibility or any broader writer permission is a refusal.
    val decode:
        asOf: DateTimeOffset ->
        raw: ReadOnlyMemory<byte> ->
            Result<GenesisProtectionRead, string list>

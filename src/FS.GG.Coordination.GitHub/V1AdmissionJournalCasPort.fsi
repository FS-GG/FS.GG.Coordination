namespace FS.GG.Coordination.GitHub

open System

[<RequireQualifiedAccess>]
module V1AdmissionJournalCasPort =
    /// Join fresh native reads to a public-only append handoff. The writer's
    /// response is never promoted to acceptance: only exact durable readback can
    /// confirm the append, and this port cannot issue an effect dispatch permit.
    val create:
        now: (unit -> DateTimeOffset) ->
        readRaw: (unit -> Result<byte array, string>) ->
        writeRaw: (ReadOnlyMemory<byte> -> Result<unit, string>) ->
            RegistryJournalPort

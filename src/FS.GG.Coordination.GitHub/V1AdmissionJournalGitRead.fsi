namespace FS.GG.Coordination.GitHub

open System

[<RequireQualifiedAccess>]
module V1AdmissionJournalGitRead =
    /// Decode a bounded, stable native Git observation and independently replay every
    /// commit before exposing the installed admission registry to a production port.
    val decode: asOf: DateTimeOffset -> raw: ReadOnlyMemory<byte> -> Result<RegistryJournalRead, string list>

    /// Bind the native collector to a fail-closed read-only journal port. A failed read is
    /// unreadable, never absent; writes are refused until a separate CAS writer is reviewed.
    val createReadOnlyPort:
        now: (unit -> DateTimeOffset) ->
        readRaw: (unit -> Result<byte array, string>) ->
            RegistryJournalPort

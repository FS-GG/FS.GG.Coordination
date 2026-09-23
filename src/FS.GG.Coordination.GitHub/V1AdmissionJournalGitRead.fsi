namespace FS.GG.Coordination.GitHub

open System

[<RequireQualifiedAccess>]
module V1AdmissionJournalGitRead =
    /// Decode a bounded, stable native Git observation and independently replay every
    /// commit before exposing the installed admission registry to a production port.
    val decode: asOf: DateTimeOffset -> raw: ReadOnlyMemory<byte> -> Result<RegistryJournalRead, string list>

namespace FS.GG.Coordination.GitHub

open System

[<RequireQualifiedAccess>]
module V1AdmissionJournalCasPort =
    let create
        (now: unit -> DateTimeOffset)
        (readRaw: unit -> Result<byte array, string>)
        (writeRaw: ReadOnlyMemory<byte> -> Result<unit, string>)
        =
        let reader = V1AdmissionJournalGitRead.createReadOnlyPort now readRaw
        { Read = reader.Read
          Write = fun proposal ->
              match V1AdmissionJournalCasPlan.encode proposal with
              | Error _ -> ReceiveDefiniteRefusal "admission-cas-plan-invalid"
              | Ok bytes ->
                  try
                      writeRaw (ReadOnlyMemory bytes) |> ignore
                  with _ -> ()
                  ReceiveResponseUnknown }

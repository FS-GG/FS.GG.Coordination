namespace FS.GG.Coordination.GitHub

open System
open System.Globalization
open System.Text.Json

[<RequireQualifiedAccess>]
module V1AdmissionGenesisProtectionRead =
    let private fields =
        [ "schema"; "observedAt"; "repositoryId"; "writerRulesetId"
          "writerRulesetActive"; "writerRulesetMatchesRef"; "writerBypassAppIds"
          "integrityRulesetId"; "integrityRulesetActive"; "integrityRulesetMatchesRef"
          "integrityRejectsDeletion"; "integrityRejectsNonFastForward"; "integrityBypassAppIds"
          "credentialAppId"; "credentialInstallationId"; "credentialRepositoryIds"
          "credentialContentsWrite"; "credentialHasOtherWritePermissions" ]

    let private exactProperties (value: JsonElement) =
        if value.ValueKind <> JsonValueKind.Object then
            false
        else
            let names = value.EnumerateObject() |> Seq.map _.Name |> Seq.toList
            names.Length = (names |> List.distinct |> List.length)
            && Set.ofList names = Set.ofList fields

    let private timestamp value =
        DateTimeOffset.ParseExact(
            value,
            "yyyy-MM-ddTHH:mm:ss'Z'",
            CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal ||| DateTimeStyles.AdjustToUniversal
        )

    let decode asOf (raw: ReadOnlyMemory<byte>) =
        try
            if raw.Length > 8192 then
                Error [ "genesis-protection-evidence-size" ]
            else
                use document = JsonDocument.Parse raw
                let root = document.RootElement
                let entry (name: string) = root.GetProperty name
                let number name = (entry name).GetInt64()
                let truth name = (entry name).GetBoolean()
                let numbers name =
                    (entry name).EnumerateArray()
                    |> Seq.map _.GetInt64()
                    |> Seq.toList
                let observedAt = (entry "observedAt").GetString() |> timestamp
                let read: GenesisProtectionRead =
                    { ObservedAt = observedAt
                      RepositoryId = number "repositoryId"
                      WriterRulesetId = number "writerRulesetId"
                      WriterRulesetActive = truth "writerRulesetActive"
                      WriterRulesetMatchesRef = truth "writerRulesetMatchesRef"
                      WriterBypassAppIds = numbers "writerBypassAppIds"
                      IntegrityRulesetId = number "integrityRulesetId"
                      IntegrityRulesetActive = truth "integrityRulesetActive"
                      IntegrityRulesetMatchesRef = truth "integrityRulesetMatchesRef"
                      IntegrityRejectsDeletion = truth "integrityRejectsDeletion"
                      IntegrityRejectsNonFastForward = truth "integrityRejectsNonFastForward"
                      IntegrityBypassAppIds = numbers "integrityBypassAppIds"
                      CredentialAppId = number "credentialAppId"
                      CredentialInstallationId = number "credentialInstallationId"
                      CredentialRepositoryIds = numbers "credentialRepositoryIds"
                      CredentialContentsWrite = truth "credentialContentsWrite"
                      CredentialHasOtherWritePermissions = truth "credentialHasOtherWritePermissions" }
                if not (exactProperties root)
                   || (entry "schema").GetString() <> "fsgg.v1-admission-genesis-protection-read/1"
                   || observedAt > asOf
                   || asOf - observedAt > TimeSpan.FromMinutes 2.
                   || read.RepositoryId <> 1351660651L
                   || read.WriterRulesetId <> 21872113L
                   || not read.WriterRulesetActive
                   || not read.WriterRulesetMatchesRef
                   || read.WriterBypassAppIds <> [ 4882140L ]
                   || read.IntegrityRulesetId <> 21872115L
                   || not read.IntegrityRulesetActive
                   || not read.IntegrityRulesetMatchesRef
                   || not read.IntegrityRejectsDeletion
                   || not read.IntegrityRejectsNonFastForward
                   || not read.IntegrityBypassAppIds.IsEmpty
                   || read.CredentialAppId <> 4882140L
                   || read.CredentialInstallationId <> 160261608L
                   || read.CredentialRepositoryIds <> [ 1351660651L ]
                   || not read.CredentialContentsWrite
                   || read.CredentialHasOtherWritePermissions then
                    Error [ "genesis-protection-evidence-binding" ]
                else
                    Ok read
        with _ ->
            Error [ "genesis-protection-evidence-invalid" ]

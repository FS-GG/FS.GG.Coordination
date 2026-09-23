namespace FS.GG.Coordination.GitHub

open System

type GenesisInstallerNativeReaders =
    {
        Now: unit -> DateTimeOffset
        ReadAbsentGit: unit -> Result<byte array, string>
        ReadInstalledGit: GitObjectId -> Result<byte array, string>
        ReadCutoverHead: unit -> Result<GitObjectId, string>
        ReadRef: string -> GenesisRefRead
        ReadTrustAnchor: unit -> Result<byte array, string>
        ReadSource: unit -> Result<GenesisSourceRead, string>
        ReadProtection: unit -> Result<GenesisProtectionRead, string>
        ReadApproval: int64 -> Result<GenesisProtectedNativeRead, string>
    }

type GenesisInstallerObjectPort =
    {
        PutObject: string -> GitObjectId -> byte array -> Result<GitObjectId, string>
        ReadObject: string -> GitObjectId -> Result<byte array, string>
        CreateRefExpectedAbsent: string -> GitObjectId -> Result<unit, string>
    }

[<RequireQualifiedAccess>]
module V1AdmissionGenesisPortBinding =
    let private samePlan expected actual =
        V1AdmissionRegistry.genesisAddress expected = V1AdmissionRegistry.genesisAddress actual
        && V1AdmissionRegistry.genesisAuthorityCommit expected = V1AdmissionRegistry.genesisAuthorityCommit actual
        && V1AdmissionRegistry.genesisCommit expected = V1AdmissionRegistry.genesisCommit actual
        && V1AdmissionRegistry.genesisObjects expected = V1AdmissionRegistry.genesisObjects actual

    let private checkedAbsent now plan (raw: byte array) =
        let operationId = (V1AdmissionRegistry.genesisCommit plan).OperationId
        V1AdmissionGenesisGitRead.decode (ReadOnlyMemory raw)
        |> Result.bind (fun evidence ->
            V1AdmissionGenesisGitRead.verifyPlan now operationId evidence
            |> Result.bind (fun freshPlan ->
                if samePlan plan freshPlan then
                    Ok(V1AdmissionGenesisGitRead.registryRead evidence)
                else
                    Error [ "genesis-live-absent-plan-drift" ]))

    let create initialAbsentGit plan native objects =
        let operationId = (V1AdmissionRegistry.genesisCommit plan).OperationId
        V1AdmissionGenesisGitRead.decode initialAbsentGit
        |> Result.bind (fun initial ->
            V1AdmissionGenesisGitRead.verifyPlan (native.Now()) operationId initial
            |> Result.bind (fun verified ->
                if samePlan plan verified then Ok initial
                else Error [ "genesis-live-initial-plan-drift" ]))
        |> Result.map (fun initial ->
            let target = (V1AdmissionRegistry.genesisObjects plan).CommitObjectId
            let address = V1AdmissionRegistry.genesisAddress plan
            let originalAuthority = V1AdmissionGenesisGitRead.authorityPort initial
            let initialObservedAt = V1AdmissionGenesisGitRead.observedAt initial
            let stillFresh () =
                let now = native.Now()
                initialObservedAt <= now && now - initialObservedAt <= TimeSpan.FromMinutes 2.
            let authority: AuthorityGitPort =
                { ReadObjects =
                    fun () ->
                        if stillFresh () then originalAuthority.ReadObjects()
                        else Error "genesis-live-authority-stale"
                  RereadHead =
                    fun () ->
                        if stillFresh () then native.ReadCutoverHead()
                        else Error "genesis-live-authority-stale" }
            let readRegistry requested =
                if requested <> address then
                    Error "genesis-live-registry-address"
                else
                    match native.ReadRef address.Ref with
                    | GenesisRefAbsent ->
                        native.ReadAbsentGit()
                        |> Result.mapError List.singleton
                        |> Result.bind (checkedAbsent (native.Now()) plan)
                        |> Result.mapError (String.concat ",")
                    | GenesisRefAt observed when observed = target ->
                        native.ReadInstalledGit target
                        |> Result.mapError List.singleton
                        |> Result.bind (fun raw ->
                            V1AdmissionGenesisGitRead.decodeInstalled (native.Now()) plan (ReadOnlyMemory raw))
                        |> Result.mapError (String.concat ",")
                    | GenesisRefAt _ -> Error "genesis-live-competing-ref"
                    | GenesisRefUnknown _ -> Error "genesis-live-ref-unknown"
            { Now = native.Now
              Authority = authority
              ReadRegistry = readRegistry
              ReadRef = native.ReadRef
              ReadTrustAnchor = native.ReadTrustAnchor
              ReadSource = native.ReadSource
              ReadProtection = native.ReadProtection
              ReadApproval = native.ReadApproval
              PutObject = objects.PutObject
              ReadObject = objects.ReadObject
              CreateRefExpectedAbsent = objects.CreateRefExpectedAbsent })

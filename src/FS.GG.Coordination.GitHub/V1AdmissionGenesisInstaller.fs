namespace FS.GG.Coordination.GitHub

open System

type GenesisSourceRead =
    {
        ObservedAt: DateTimeOffset
        RepositoryId: int64
        Commit: GitObjectId
        Tree: GitObjectId
        IsOnMain: bool
    }

type GenesisRefRead =
    | GenesisRefAbsent
    | GenesisRefAt of GitObjectId
    | GenesisRefUnknown of string

type GenesisProtectionRead =
    {
        ObservedAt: DateTimeOffset
        RepositoryId: int64
        WriterRulesetId: int64
        WriterRulesetActive: bool
        WriterRulesetMatchesRef: bool
        WriterBypassAppIds: int64 list
        IntegrityRulesetId: int64
        IntegrityRulesetActive: bool
        IntegrityRulesetMatchesRef: bool
        IntegrityRejectsDeletion: bool
        IntegrityRejectsNonFastForward: bool
        IntegrityBypassAppIds: int64 list
        CredentialAppId: int64
        CredentialInstallationId: int64
        CredentialRepositoryIds: int64 list
        CredentialContentsWrite: bool
        CredentialHasOtherWritePermissions: bool
    }

type GenesisInstallerPort =
    {
        Now: unit -> DateTimeOffset
        Authority: AuthorityGitPort
        ReadRegistry: AggregateAddress -> Result<RegistryJournalRead, string>
        ReadRef: string -> GenesisRefRead
        ReadTrustAnchor: unit -> Result<byte array, string>
        ReadSource: unit -> Result<GenesisSourceRead, string>
        ReadProtection: unit -> Result<GenesisProtectionRead, string>
        ReadApproval: int64 -> Result<GenesisProtectedNativeRead, string>
        PutObject: string -> GitObjectId -> byte array -> Result<GitObjectId, string>
        ReadObject: string -> GitObjectId -> Result<byte array, string>
        CreateRefExpectedAbsent: string -> GitObjectId -> Result<unit, string>
    }

type GenesisInstallOutcome =
    | GenesisInstalled
    | GenesisAlreadyInstalled
    | GenesisInstallRefused of string list
    | GenesisInstallIndeterminate of string list

[<RequireQualifiedAccess>]
module V1AdmissionGenesisInstaller =
    type private PreflightFailure =
        | PreflightRefused of string list
        | PreflightIndeterminate of string list

    let private unreadable reason = PreflightIndeterminate [ reason ]

    let private fresh now observed = observed <= now && now - observed <= TimeSpan.FromMinutes 2.

    let private sourceValid now (intent: GenesisAuthorizationIntent) (source: GenesisSourceRead) =
        fresh now source.ObservedAt
        && source.RepositoryId = 1346720714L
        && source.Commit = intent.SourceCommit
        && source.Tree = intent.SourceTree
        && source.IsOnMain

    let private protectionValid now (protection: GenesisProtectionRead) =
        fresh now protection.ObservedAt
        && protection.RepositoryId = 1351660651L
        && protection.WriterRulesetId = 21872113L
        && protection.WriterRulesetActive
        && protection.WriterRulesetMatchesRef
        && protection.WriterBypassAppIds = [ 4882140L ]
        && protection.IntegrityRulesetId = 21872115L
        && protection.IntegrityRulesetActive
        && protection.IntegrityRulesetMatchesRef
        && protection.IntegrityRejectsDeletion
        && protection.IntegrityRejectsNonFastForward
        && protection.IntegrityBypassAppIds.IsEmpty
        && protection.CredentialAppId = 4882140L
        && protection.CredentialInstallationId = 160261608L
        && protection.CredentialRepositoryIds = [ 1351660651L ]
        && protection.CredentialContentsWrite
        && not protection.CredentialHasOtherWritePermissions

    let private samePlan expected fresh =
        V1AdmissionRegistry.genesisAddress expected = V1AdmissionRegistry.genesisAddress fresh
        && V1AdmissionRegistry.genesisCommit expected = V1AdmissionRegistry.genesisCommit fresh
        && V1AdmissionRegistry.genesisObjects expected = V1AdmissionRegistry.genesisObjects fresh

    let private preflight port plan intent signature =
        try
            V1AdmissionRegistry.readVerified port.Authority
            |> Result.mapError PreflightIndeterminate
            |> Result.bind (fun authority ->
                if V1AdmissionRegistry.genesisMatchesAuthority plan authority then
                    Ok authority
                else
                    Error(PreflightRefused [ "genesis-authority-drift" ]))
            |> Result.bind (fun authority ->
                port.ReadTrustAnchor()
                |> Result.mapError (fun _ -> unreadable "genesis-trust-read-unknown")
                |> Result.bind (fun trust ->
                    port.ReadApproval signature.ProtectedRunId
                    |> Result.mapError (fun _ -> unreadable "genesis-native-approval-read-unknown")
                    |> Result.bind (fun native ->
                        let now = port.Now()
                        V1AdmissionGenesisAuthorization.verify now trust plan intent signature
                        |> Result.mapError PreflightRefused
                        |> Result.bind (fun signed ->
                            V1AdmissionGenesisProtectedApproval.verify now plan intent signed native
                            |> Result.mapError PreflightRefused)
                        |> Result.map (fun _ -> authority))))
            |> Result.bind (fun authority ->
                port.ReadSource()
                |> Result.mapError (fun _ -> unreadable "genesis-source-read-unknown")
                |> Result.bind (fun source ->
                    if sourceValid (port.Now()) intent source then Ok authority
                    else Error(PreflightRefused [ "genesis-source-not-merged-or-stale" ])))
            |> Result.bind (fun authority ->
                port.ReadProtection()
                |> Result.mapError (fun _ -> unreadable "genesis-protection-read-unknown")
                |> Result.bind (fun protection ->
                    if protectionValid (port.Now()) protection then Ok authority
                    else Error(PreflightRefused [ "genesis-protection-or-writer-drift" ])))
            |> Result.bind (fun authority ->
                let address = V1AdmissionRegistry.genesisAddress plan
                let target = (V1AdmissionRegistry.genesisObjects plan).CommitObjectId
                port.ReadRegistry address
                |> Result.mapError (fun _ -> unreadable "genesis-journal-read-unknown")
                |> Result.bind (fun journal ->
                    match port.ReadRef address.Ref with
                    | GenesisRefAbsent ->
                        V1AdmissionRegistry.planGenesis
                            (V1AdmissionRegistry.genesisCommit plan).OperationId authority journal
                        |> Result.mapError PreflightIndeterminate
                        |> Result.bind (fun freshPlan ->
                            if samePlan plan freshPlan then Ok false
                            else Error(PreflightRefused [ "genesis-plan-drift" ]))
                    | GenesisRefAt commit when commit = target ->
                        V1AdmissionRegistry.verifyGenesisReadback plan journal
                        |> Result.mapError PreflightIndeterminate
                        |> Result.map (fun _ -> true)
                    | GenesisRefAt _ -> Error(PreflightRefused [ "genesis-competing-ref" ])
                    | GenesisRefUnknown _ -> Error(unreadable "genesis-ref-read-unknown")))
            |> Result.bind (fun alreadyInstalled ->
                port.ReadTrustAnchor()
                |> Result.mapError (fun _ -> unreadable "genesis-final-trust-read-unknown")
                |> Result.bind (fun trust ->
                    port.ReadApproval signature.ProtectedRunId
                    |> Result.mapError (fun _ -> unreadable "genesis-final-approval-read-unknown")
                    |> Result.bind (fun native ->
                        let now = port.Now()
                        V1AdmissionGenesisAuthorization.verify now trust plan intent signature
                        |> Result.mapError PreflightRefused
                        |> Result.bind (fun signed ->
                            V1AdmissionGenesisProtectedApproval.verify now plan intent signed native
                            |> Result.mapError PreflightRefused)
                        |> Result.map (fun _ -> alreadyInstalled))))
        with _ ->
            Error(unreadable "genesis-preflight-indeterminate")

    let private objectList plan =
        let objects = V1AdmissionRegistry.genesisObjects plan
        [
            "blob", objects.EventObjectId, objects.EventBytes
            "blob", objects.HeadObjectId, objects.HeadBytes
            "tree", objects.TreeObjectId, objects.TreeBytes
            "commit", objects.CommitObjectId, objects.CommitBytes
        ]

    let private putAndVerify port (kind, oid, bytes) =
        try
            match port.PutObject kind oid bytes with
            | Ok returned when returned <> oid -> Error [ "genesis-object-oid-mismatch" ]
            | _ ->
                match port.ReadObject kind oid with
                | Ok observed when observed = bytes -> Ok()
                | _ -> Error [ "genesis-object-readback-unknown" ]
        with _ ->
            Error [ "genesis-object-write-indeterminate" ]

    let private verifyFinal port plan =
        try
            let address = V1AdmissionRegistry.genesisAddress plan
            let target = (V1AdmissionRegistry.genesisObjects plan).CommitObjectId
            match port.ReadRef address.Ref, port.ReadRegistry address with
            | GenesisRefAt commit, Ok journal when commit = target ->
                V1AdmissionRegistry.verifyGenesisReadback plan journal
                |> Result.map (fun _ -> ())
            | _ -> Error [ "genesis-final-readback-indeterminate" ]
        with _ ->
            Error [ "genesis-final-readback-indeterminate" ]

    let apply port plan intent signature =
        match preflight port plan intent signature with
        | Error(PreflightRefused reasons) -> GenesisInstallRefused reasons
        | Error(PreflightIndeterminate reasons) -> GenesisInstallIndeterminate reasons
        | Ok true -> GenesisAlreadyInstalled
        | Ok false ->
            match objectList plan |> List.fold (fun result item -> result |> Result.bind (fun () -> putAndVerify port item)) (Ok()) with
            | Error reasons -> GenesisInstallIndeterminate reasons
            | Ok() ->
                match preflight port plan intent signature with
                | Error(PreflightRefused reasons) -> GenesisInstallRefused reasons
                | Error(PreflightIndeterminate reasons) -> GenesisInstallIndeterminate reasons
                | Ok true -> GenesisAlreadyInstalled
                | Ok false ->
                    let address = V1AdmissionRegistry.genesisAddress plan
                    let target = (V1AdmissionRegistry.genesisObjects plan).CommitObjectId
                    try
                        let _ = port.CreateRefExpectedAbsent address.Ref target
                        match verifyFinal port plan with
                        | Ok() ->
                            match preflight port plan intent signature with
                            | Ok true -> GenesisInstalled
                            | _ -> GenesisInstallIndeterminate [ "genesis-postcreate-preflight-not-verified" ]
                        | Error reasons -> GenesisInstallIndeterminate reasons
                    with _ ->
                        match verifyFinal port plan with
                        | Ok() ->
                            match preflight port plan intent signature with
                            | Ok true -> GenesisInstalled
                            | _ -> GenesisInstallIndeterminate [ "genesis-postcreate-preflight-not-verified" ]
                        | Error reasons -> GenesisInstallIndeterminate reasons

namespace FS.GG.Coordination.GitHub

open System
open System.Security.Cryptography
open System.Text

type AdmissionServicePorts =
    {
        Authority: AuthorityGitPort
        Journal: RegistryJournalPort
    }

type AdmissionServiceDecision =
    | AdmissionDurablyAppended of OperationHandle * GitObjectId
    | AdmissionAlreadyDurable of OperationHandle * GitObjectId
    | AdmissionParentConflict of GitObjectId option
    | AdmissionServiceRefused of string list
    | AdmissionServiceIndeterminate of string list

[<RequireQualifiedAccess>]
module V1AdmissionService =
    let private operationAddress =
        ShardedJournalAdapter.address Operation "fleet-v1-admission:fs-gg-production"
        |> Result.defaultWith (string >> invalidOp)

    let private commandId (context: MutationContext) =
        let identity = Encoding.UTF8.GetBytes context.OperationId
        let digest = SHA256.HashData identity |> Convert.ToHexString |> _.ToLowerInvariant()
        $"admit-{digest}-{context.OperationGeneration}"

    let private matchingHandle authority context registry =
        match V1AdmissionRegistry.admit (V1AdmissionRegistry.head registry) authority context registry with
        | RegistryAdmissionAlreadyPresent handle ->
            Ok(handle, V1AdmissionRegistry.head registry)
        | _ -> Error [ "admission-confirmed-binding-moved" ]

    let admit (ports: AdmissionServicePorts) (context: MutationContext) =
        try
            let observed = ports.Journal.Read operationAddress
            match V1AdmissionRegistry.restore observed with
            | Error _ -> AdmissionServiceIndeterminate [ "admission-journal-unverified" ]
            | Ok registry ->
                match V1AdmissionRegistry.readVerified ports.Authority with
                | Error _ -> AdmissionServiceIndeterminate [ "admission-authority-unverified" ]
                | Ok authority ->
                    match V1AdmissionRegistry.admit (V1AdmissionRegistry.head registry) authority context registry with
                    | RegistryAdmissionRefused reasons -> AdmissionServiceRefused reasons
                    | RegistryAdmissionAlreadyPresent handle ->
                        AdmissionAlreadyDurable(handle, V1AdmissionRegistry.head registry)
                    | RegistryAdmissionAppended candidate ->
                        match V1AdmissionRegistry.planAppend (commandId context) observed candidate with
                        | Error reasons -> AdmissionServiceRefused reasons
                        | Ok proposal ->
                            match V1AdmissionRegistry.readVerified ports.Authority with
                            | Error _ -> AdmissionServiceIndeterminate [ "admission-authority-reread-unverified" ]
                            | Ok fresh when fresh <> authority ->
                                AdmissionServiceIndeterminate [ "admission-authority-moved" ]
                            | Ok _ ->
                                match V1AdmissionRegistry.appendAndReconcile ports.Journal proposal with
                                | DurableAppendAccepted(durable, None) ->
                                    match matchingHandle authority context durable with
                                    | Ok(handle, commit) -> AdmissionDurablyAppended(handle, commit)
                                    | Error reasons -> AdmissionServiceIndeterminate reasons
                                | DurableAppendAccepted(_, Some _) ->
                                    AdmissionServiceIndeterminate [ "admission-unexpected-dispatch-permit" ]
                                | DurableAppendParentConflict latest ->
                                    AdmissionParentConflict(latest |> Option.map V1AdmissionRegistry.head)
                                | DurableAppendRefused _ ->
                                    AdmissionServiceRefused [ "admission-append-refused" ]
                                | DurableAppendIndeterminate _ ->
                                    AdmissionServiceIndeterminate [ "admission-append-unknown" ]
        with _ ->
            AdmissionServiceIndeterminate [ "admission-port-exception" ]

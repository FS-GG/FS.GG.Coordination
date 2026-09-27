module FS.GG.Coordination.MigrationReceiverCohortOrchestrationTests

open System
open Xunit
open FS.GG.Coordination.Cli
open FS.GG.Coordination.GitHub
open FS.GG.Coordination.Qualification.Contracts

let private receiverIds =
    [ "sdd"; "rendering"; "governance"; "templates"; "game"; "audio"; "net" ]

let private revision index = String.replicate 39 "a" + string index
let private tree index = String.replicate 39 "b" + string index

let private receiverRef receiver =
    $"refs/heads/gs2-09-7/run/receivers/{receiver}"

let private mappings =
    receiverIds
    |> List.mapi (fun index receiver ->
        {
            ReceiverCopyId = receiver
            ReceiverCopyRepository = "FS-GG/FS.GG.GitHub.Substrate.Sandbox"
            ReceiverCopySourceRevision = revision (index + 1)
            ReceiverCopySourceTree = tree (index + 1)
            ReceiverCopyPlannedRef = receiverRef receiver
            ReceiverCopyRequiredEntries = []
            ReceiverCopyMissingBlobSha1s = []
        })

let private copyPlan =
    {
        ReceiverCopyRunIdentity = Unchecked.defaultof<_>
        ReceiverCopyReceiptVerification = Unchecked.defaultof<_>
        ReceiverCopyCensus = Unchecked.defaultof<_>
        ReceiverCopyMappings = mappings
        ReceiverCopyRetainedBlobSha256BySha1 = Map.empty
        ReceiverCopyFingerprint = String.replicate 64 "c"
    }

let private rosterRepository =
    {
        RosterRepositoryId = 1353050537L
        RosterRepositoryNodeId = "R_kgDOUKXpqQ"
        RosterRepositoryFullName = "FS-GG/FS.GG.GitHub.Substrate.Sandbox"
        RosterPrivate = true
        RosterArchived = false
        RosterDisabled = false
        RosterPermissions = Map [ "pull", true ]
    }

let private rosterOptions =
    {
        ApiBase = Uri "https://api.github.test/"
        InstallationId = 7001L
        AccountLogin = "FS-GG"
        AccountId = 9L
        AccountNodeId = "ORG_9"
        RequiredPermissions = Map [ "contents", "read"; "metadata", "read" ]
        AppToken = ""
        InstallationToken = ""
        UserAgent = "cohort-test"
    }

let private scope =
    {
        InstallationId = 7001L
        AccountLogin = "FS-GG"
        AccountId = 9L
        AccountNodeId = "ORG_9"
        RepositorySelection = "selected"
        Permissions = rosterOptions.RequiredPermissions
        RepositoriesUrl = "https://api.github.test/installation/repositories"
    }

let private rawPage =
    {
        RosterRequestedUri = "https://api.github.test/installation/repositories?per_page=100&page=1"
        RosterRequestIdentitySha256 = String.replicate 64 "1"
        RosterRawBody = "{}"
        RosterRawSha256 = String.replicate 64 "2"
        RosterNextUri = None
    }

let private rosterPass =
    {
        ScopeSettings = scope
        RepositoryTotalCount = 1
        Repositories = [ rosterRepository ]
        Pages = [ rawPage ]
        PassFingerprint = String.replicate 64 "3"
    }

let private installation =
    {
        App = Unchecked.defaultof<_>
        AppFirst = rawPage
        AppSecond = rawPage
        ComposerRosterOptions = rosterOptions
        ComposerRosterCapture =
            {
                First = rosterPass
                Second = rosterPass
                CaptureFingerprint = String.replicate 64 "4"
            }
        CaptureFingerprint = String.replicate 64 "5"
    }

let private cohort =
    {
        Repositories =
            [
                {
                    Id = rosterRepository.RosterRepositoryId
                    NodeId = rosterRepository.RosterRepositoryNodeId
                    FullName = rosterRepository.RosterRepositoryFullName
                    SourceHead = String.replicate 40 "d"
                    TargetHead = String.replicate 40 "e"
                }
            ]
        Receivers =
            mappings
            |> List.map (fun mapping ->
                {
                    Receiver = mapping.ReceiverCopyId
                    RepositoryId = rosterRepository.RosterRepositoryId
                    RefName = mapping.ReceiverCopyPlannedRef
                    ExpectedHead = mapping.ReceiverCopySourceRevision
                })
        ProjectOrganization = "FS-GG"
        ProjectNumber = 2
        ProjectNodeId = "PVT_2"
        SourceRevision = String.replicate 40 "f"
        Isolated = true
    }

let private options =
    {
        Cohort = cohort
        Repository =
            {
                ApiBase = Uri "https://api.github.test/"
                GraphQLUri = Uri "https://api.github.test/graphql"
                Token = "token"
                UserAgent = "cohort-test"
                Owner = "FS-GG"
                Repository = "FS.GG.GitHub.Substrate.Sandbox"
                ExpectedRepositoryId = rosterRepository.RosterRepositoryId
            }
        Project =
            {
                GraphQLUri = Uri "https://api.github.test/graphql"
                Token = "token"
                UserAgent = "cohort-test"
                Organization = "FS-GG"
                ProjectNumber = 2
                ExpectedProjectNodeId = "PVT_2"
            }
    }

let private validate captured provider plan =
    MigrationReceiverCohortOrchestration.validateBindingsForTests captured provider plan

let private expectUnavailable fragment result =
    match result with
    | Error reason ->
        Assert.StartsWith("receiver-cohort-orchestration-unavailable:", reason)
        Assert.Contains(fragment, reason)
    | Ok _ -> Assert.Fail($"expected refusal containing {fragment}")

[<Fact>]
let ``one sandbox installation binds all seven exact plan refs`` () =
    Assert.Equal(Ok(), validate installation options copyPlan)

[<Fact>]
let ``missing duplicate and foreign receiver refs refuse before provider capture`` () =
    validate
        installation
        { options with
            Cohort =
                { cohort with
                    Receivers = cohort.Receivers.Tail
                }
        }
        copyPlan
    |> expectUnavailable "cohort-seven-receivers"

    let first = cohort.Receivers.Head

    let duplicate =
        { first with
            Receiver = cohort.Receivers.Tail.Head.Receiver
        }

    validate
        installation
        { options with
            Cohort =
                { cohort with
                    Receivers = duplicate :: cohort.Receivers.Tail
                }
        }
        copyPlan
    |> expectUnavailable "invalid-cohort"

    let changed =
        { first with
            RefName = first.RefName + "-foreign"
        }

    validate
        installation
        { options with
            Cohort =
                { cohort with
                    Receivers = changed :: cohort.Receivers.Tail
                }
        }
        copyPlan
    |> expectUnavailable "copy-plan-ref-mismatch"

[<Fact>]
let ``changed installation scope and provider identities refuse`` () =
    let foreignRepository =
        { rosterRepository with
            RosterRepositoryNodeId = "R_foreign"
        }

    let foreignPass =
        { rosterPass with
            Repositories = [ foreignRepository ]
        }

    let foreignCapture =
        { installation with
            ComposerRosterCapture =
                { installation.ComposerRosterCapture with
                    First = foreignPass
                    Second = foreignPass
                }
        }

    validate foreignCapture options copyPlan
    |> expectUnavailable "installation-repository-mismatch"

    let wrongRepository =
        { options with
            Repository =
                { options.Repository with
                    ExpectedRepositoryId = 99L
                }
        }

    validate installation wrongRepository copyPlan
    |> expectUnavailable "provider-repository-mismatch"

    let wrongProject =
        { options with
            Project =
                { options.Project with
                    ExpectedProjectNodeId = "PVT_foreign"
                }
        }

    validate installation wrongProject copyPlan
    |> expectUnavailable "provider-project-mismatch"

[<Fact>]
let ``unaccepted receiver names and two-pass installation drift refuse`` () =
    let changedMapping =
        { mappings.Head with
            ReceiverCopyId = "unknown"
        }

    validate
        installation
        options
        { copyPlan with
            ReceiverCopyMappings = changedMapping :: mappings.Tail
        }
    |> expectUnavailable "copy-plan-seven-receivers"

    let drifted =
        { installation with
            ComposerRosterCapture =
                { installation.ComposerRosterCapture with
                    Second =
                        { rosterPass with
                            PassFingerprint = String.replicate 64 "9"
                        }
                }
        }

    validate drifted options copyPlan
    |> expectUnavailable "installation-two-pass-drift"

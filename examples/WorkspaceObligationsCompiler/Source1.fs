namespace FS.GG.Coordination.WorkspaceCompiler

open System
open FS.GG.Coordination.Core
open FS.GG.Coordination.Core.Orchestration
open FS.GG.Coordination.Core.OrdinaryAssignment

/// Actual Root source-1 facts supplied on 2026-10-29. These do not describe a provider/native original.
/// The caller supplies Root's independently selected keys and current unresolved-fact disposition.
module Source1 =
    // Root independently selected these from authored tasks before observing compiler output.
    let expectedKeys = set [ "T001"; "T002"; "T003"; "T004" ]

    let acceptedFingerprint =
        "f04022214c6525525016da008d93551db60cec085769fd22f65de49183b70cca"

    let custodyRevision =
        "local:base666e0ff00bcbd36a9234de6fa40be41696da76eb;authored-sha256:4dcf1900f5c465dea543e092018d2c73cd090c6a8e008e866bff407520d7c84e"

    let selectedAt = DateTimeOffset.Parse "2026-10-29T13:14:12.430911+00:00"
    let deadline = DateTimeOffset.Parse "2026-10-29T16:14:12.430911+00:00"

    let checks =
        set
            [
                "git diff --check"
                "dotnet build examples/WorkspaceObligationsCompiler/WorkspaceObligationsCompiler.fsproj --configuration Release"
                "dotnet test tests/FS.GG.Coordination.WorkspaceCompiler.Tests/FS.GG.Coordination.WorkspaceCompiler.Tests.fsproj --configuration Release"
            ]

    let originalFacts (keys: Set<string>) (missingFacts: Set<string>) =
        keys
        |> Set.toList
        |> List.map (fun key ->
            {
                Key = key
                Identity = Board(WorkItemIdentity.create "R_kgDOUEVTyg" 1346720714L "I_kwDOUEVTys8AAAABa5Hv3w" 1081L)
                // Source-owned workspace fields are filled by Compiler; no operation fact is filled there.
                Workspace =
                    {
                        Version = "workspace-execution/1"
                        Workspace = ""
                        Provider = ""
                        Repositories = []
                        Components = []
                        Sources = []
                    }
                Assessment =
                    {
                        Version = "work-assessment/1"
                        Phase = "source-implementation"
                        Outcome = "independently-usable-source-compiler"
                        Objective = "full native1081 Done when"
                        Plan = "https://github.com/FS-GG/FS.GG.Coordination/issues/1081"
                        Facts =
                            set["protected88/current native Delivery/authenticated source1 Root admission"
                                "published2.5.3 isolated restore and Release compile reported PASS with original failures retained"
                                "source-only parent promises retained"]
                        Interpretation =
                            "actual package API join supports implementation, no genuine accepted live model or complete positive invocation yet"
                        Difficulty = 75
                        Uncertainty = 55
                        EffectRisk = 25
                        MissingFacts = missingFacts
                    }
                Attempt = Id.attempt (Guid.Parse "5b8fec28-6fa2-41fa-a8b0-1720c699f9cf")
                Generation = Some(Id.generation 1L)
                SelectedAt = selectedAt
                Deadline = Some deadline
                Recovery =
                    Some
                        {
                            OriginalStartedAt = selectedAt
                            OriginalDeadline = deadline
                            MaxAttempts = 1
                            AttemptOrdinal = 1
                        }
                Dependencies = Set.empty
                RequiredChecks = checks
                Capabilities =
                    set["git"
                        "dotnet"
                        "codex"]
                Tools =
                    set["git"
                        "dotnet"]
                Skills =
                    set["atlas"
                        "pnext-item"
                        "intra-repo-parallel-work"]
                Context =
                    set["native1081-acceptance"
                        "sdd928-source-contract"
                        "accepted-workspace-model"
                        "complete-authored-population"
                        "source1-original-facts"]
                AllowedPaths =
                    set["examples/WorkspaceObligationsCompiler"
                        "tests/FS.GG.Coordination.WorkspaceCompiler.Tests"
                        "FS.GG.Coordination.sln"
                        "work/1081-workspace-obligations-compiler"
                        "readiness/1081-workspace-obligations-compiler"]
                AuthorizedEffects = set["workspace-edit"]
                RequiredOutputs =
                    set["candidate"
                        "validation-evidence"
                        "typed-consumer-evidence"]
                RequiredReviews = set["independent-source-review"]
                Demand =
                    Map.ofList["cpu-millicores", Some 1000L
                               "memory-bytes", Some 2147483648L
                               "storage-bytes", Some 2147483648L
                               "task-slots", Some 1L]
                Priority = 0
            })

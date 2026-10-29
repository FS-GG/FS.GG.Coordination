namespace FS.GG.Coordination.WorkspaceCompiler

open System
open System.IO
open System.Security.Cryptography
open System.Text
open FS.GG.Coordination.Core.OrdinaryAssignment
open FS.GG.SDD.Artifacts
open FS.GG.SDD.Artifacts.TypedSpecifications

module ReferenceConsumer =
    let workId = "1081-workspace-obligations-compiler"
    let workRoot = "work/" + workId
    let readinessRoot = "readiness/" + workId

    let private sha bytes =
        SHA256.HashData(bytes: byte array) |> Convert.ToHexStringLower

    let private require =
        function
        | Ok value -> value
        | Error diagnostics -> failwithf "%A" diagnostics

    let private id value = SpecificationId.create value |> require

    let authoredPaths =
        [ ".fsgg/project.yml"; ".fsgg/sdd.yml"; ".fsgg/agents.yml" ]
        @ ([
            "charter.md"
            "spec.md"
            "clarifications.md"
            "checklist.md"
            "plan.md"
            "tasks.yml"
            "evidence.yml"
           ]
           |> List.map (fun p -> workRoot + "/" + p))

    let private bindings =
        [
            ("CHAR-1081", WorkspaceModuleKind.WorkChange, "charter.md")
            ("SPEC-1081", WorkspaceModuleKind.ProductSpecification, "spec.md")
            ("CLAR-1081", WorkspaceModuleKind.Decision, "clarifications.md")
            ("CHECK-1081", WorkspaceModuleKind.CiObligation, "checklist.md")
            ("PLAN-1081", WorkspaceModuleKind.WorkChange, "plan.md")
            ("TASK-1081", WorkspaceModuleKind.WorkChange, "tasks.yml")
            ("REPO-1081", WorkspaceModuleKind.RepositoryProfile, "repository-profile.md")
        ]
        @ ([ 1..4 ]
           |> List.map (fun n -> sprintf "EV%03d" n, WorkspaceModuleKind.EvidenceRequirement, "evidence.yml"))

    /// Explicit caller-selected revision/custody. Hashes establish byte correspondence, not Git provenance.
    let source root revision path : SourceBytes =
        use stream =
            new FileStream(Path.Combine(root, path), FileMode.Open, FileAccess.Read, FileShare.Read)

        let length = stream.Length

        if length <= 0L || length > 1024L * 1024L then
            invalidArg "path" "source-byte-bound"

        let bytes = Array.zeroCreate<byte>(int length)
        stream.ReadExactly bytes

        if stream.ReadByte() <> -1 || stream.Length <> length then
            failwith "Source changed during read."

        {
            Source =
                {
                    Repository = "FS-GG/FS.GG.Coordination"
                    Revision = revision
                    Path = path
                    Sha256 = sha bytes
                }
            Bytes = bytes
        }

    let private codePaths =
        [
            "Compiler.fsi"
            "Compiler.fs"
            "Source1.fs"
            "ReferenceConsumer.fs"
            "WorkspaceObligationsCompiler.fsproj"
            "packages.lock.json"
        ]
        |> List.map (fun p -> "examples/WorkspaceObligationsCompiler/" + p)

    let private testPaths =
        [
            "GeneratedControls.fs"
            "WorkspaceCompilerTests.fs"
            "FS.GG.Coordination.WorkspaceCompiler.Tests.fsproj"
            "packages.lock.json"
        ]
        |> List.map (fun p -> "tests/FS.GG.Coordination.WorkspaceCompiler.Tests/" + p)

    /// Prepare this live Delivery's actual authored work model and exact-base proposal.
    /// This command writes a proposal, never accepted authority.
    let propose root =
        let snapshots =
            authoredPaths
            |> List.map (fun path ->
                let s = source root "source-proposal" path

                {
                    Path = path
                    Text = Encoding.UTF8.GetString s.Bytes
                    RawBytes = Some s.Bytes
                }
                : FileSnapshot)

        let generated =
            Serialization.generateWorkModel
                {
                    WorkId = workId
                    Snapshots = snapshots
                    GeneratorVersion = SchemaVersion.currentGeneratorVersion ()
                    ExpectedOutputPath = Some(readinessRoot + "/work-model.json")
                }

        let blocking = WorkModel.blockingDiagnostics generated.Model

        if not blocking.IsEmpty then
            failwithf "Authored work refuses: %A" blocking

        Directory.CreateDirectory(Path.Combine(root, readinessRoot)) |> ignore
        File.WriteAllText(Path.Combine(root, readinessRoot, "work-model.json"), generated.Json)

        let modules =
            bindings
            |> List.map (fun (key, kind, file) ->
                {
                    Id = id key
                    Kind = kind
                    ContentSha256 = (source root "source-proposal" (workRoot + "/" + file)).Source.Sha256
                    References = []
                    Assumptions = []
                    EvidenceObligationIds =
                        if key = "TASK-1081" then
                            [ 1..4 ] |> List.map (sprintf "EV%03d" >> id)
                        else
                            []
                })

        let acceptedPath = Path.Combine(root, readinessRoot, "workspace-model.json")

        let initial: WorkspaceModel =
            if File.Exists acceptedPath then
                File.ReadAllText acceptedPath |> WorkspaceLifecycle.deserializeModel |> require
            else
                {
                    SchemaVersion = 1
                    Revision = 0L
                    Modules = []
                }

        let fingerprint = WorkspaceLifecycle.fingerprint initial |> require

        let proposal: ChangeProposal =
            {
                SchemaVersion = 1
                IssueRef = "https://github.com/FS-GG/FS.GG.Coordination/issues/1081"
                ProseSha256 = (source root "source-proposal" (workRoot + "/spec.md")).Source.Sha256
                BaseFingerprint = fingerprint
                AuthoringDepth = AuthoringDepth.StructuredSdd
                Changes =
                    modules
                    |> List.filter (fun m -> initial.Modules |> List.tryFind (fun old -> old.Id = m.Id) <> Some m)
                    |> List.map WorkspaceChange.Upsert
                Disposition = ProposalDisposition.CoherentDelta
                EvidenceFingerprint = None
            }

        File.WriteAllText(
            Path.Combine(root, readinessRoot, "workspace-base.json"),
            WorkspaceLifecycle.serializeModel initial |> require
        )

        File.WriteAllText(
            Path.Combine(root, readinessRoot, "workspace-proposal.json"),
            WorkspaceLifecycle.serializeProposal initial proposal |> require
        )

        let candidate: WorkspaceModel =
            { initial with
                Revision = initial.Revision + 1L
                Modules = modules
            }

        File.WriteAllText(
            Path.Combine(root, readinessRoot, "workspace-candidate.json"),
            WorkspaceLifecycle.serializeModel candidate |> require
        )

        WorkspaceLifecycle.fingerprint candidate |> require

    /// Existing lifecycle reduction. The caller must supply actual Root acceptance of this fingerprint.
    let accept root expectedFingerprint acceptedBy evidence acceptedAt =
        let read file =
            File.ReadAllText(Path.Combine(root, readinessRoot, file))

        let initial =
            WorkspaceLifecycle.deserializeModel (read "workspace-base.json") |> require

        let proposal =
            WorkspaceLifecycle.deserializeProposal (read "workspace-proposal.json")
            |> require

        let accepted =
            WorkspaceLifecycle.reduce
                initial
                proposal
                {
                    AcceptedBy = acceptedBy
                    EvidenceRefs = [ evidence ]
                    AcceptedAtUtc = acceptedAt
                }
            |> require

        let actual = WorkspaceLifecycle.fingerprint accepted |> require

        if actual <> expectedFingerprint then
            failwith "Acceptance fingerprint differs from reduced proposal."

        File.WriteAllText(
            Path.Combine(root, readinessRoot, "workspace-model.json"),
            WorkspaceLifecycle.serializeModel accepted |> require
        )

    /// A genuine typed API caller; original facts are separately supplied, not manufactured here.
    let private invokeCaptured root revision fingerprint observedAt originalFacts completeness =
        let accepted = source root revision (readinessRoot + "/workspace-model.json")
        let model = source root revision (readinessRoot + "/work-model.json")
        let authored = authoredPaths |> List.map (source root revision)

        let moduleSources =
            bindings
            |> List.map (fun (key, _, file) -> id key, (source root revision (workRoot + "/" + file)).Source)
            |> Map.ofList

        let code = codePaths |> List.map (source root revision)
        let profile = source root revision (workRoot + "/repository-profile.md")
        let tests = testPaths |> List.map (source root revision)
        let solution = source root revision "FS.GG.Coordination.sln"
        let plan = source root revision "eng/bootstrap-qualification-plan.json"

        let architecture =
            source root revision "tests/FS.GG.Coordination.ArchitectureTests/BootstrapCiTests.fs"

        let closure = code @ tests @ [ profile; solution; plan; architecture ]

        let part: Component =
            {
                Id = "WorkspaceObligationsCompiler"
                Root = "examples/WorkspaceObligationsCompiler"
                Language = "F#"
                Commands = Source1.checks
                Sources = code |> List.map _.Source
            }

        let testPart: Component =
            {
                Id = "WorkspaceCompiler.Tests"
                Root = "tests/FS.GG.Coordination.WorkspaceCompiler.Tests"
                Language = "F#"
                Commands = Source1.checks
                Sources = tests |> List.map _.Source
            }

        let architecturePart: Component =
            {
                Id = "ArchitectureTests-census"
                Root = "tests/FS.GG.Coordination.ArchitectureTests"
                Language = "F#"
                Commands = Set.empty
                Sources = [ architecture.Source ]
            }

        let authoredPart: Component =
            {
                Id = "1081-authored-source"
                Root = workRoot
                Language = "SDD"
                Commands = Set.empty
                Sources =
                    (authored @ [ profile ])
                    |> List.map _.Source
                    |> List.filter (fun s -> s.Path.StartsWith(workRoot + "/", StringComparison.Ordinal))
            }

        let configPart: Component =
            {
                Id = "maintained-config"
                Root = ".fsgg"
                Language = "YAML"
                Commands = Set.empty
                Sources =
                    authored
                    |> List.map _.Source
                    |> List.filter (fun s -> s.Path.StartsWith(".fsgg/", StringComparison.Ordinal))
            }

        let generatedPart: Component =
            {
                Id = "1081-generated-lifecycle"
                Root = readinessRoot
                Language = "JSON"
                Commands = Set.empty
                Sources = [ accepted.Source; model.Source ]
            }

        Compiler.compile
            accepted
            fingerprint
            moduleSources
            model
            authored
            closure
            (Framework(id "REPO-1081"))
            root
            [
                {
                    Identity = "FS-GG/FS.GG.Coordination"
                    Revision = revision
                }
            ]
            [ part; testPart; architecturePart; authoredPart; configPart; generatedPart ]
            observedAt
            completeness
            originalFacts

    /// Missing selected files refuse; source IO never silently shrinks the population.
    let invoke root revision fingerprint observedAt originalFacts completeness =
        try
            invokeCaptured root revision fingerprint observedAt originalFacts completeness
        with
        | :? IOException -> Error(NeedsEvidence [ "source-custody-unavailable" ])
        | :? UnauthorizedAccessException -> Error(NeedsEvidence [ "source-custody-unavailable" ])

    [<EntryPoint>]
    let main argv =
        try
            match Array.toList argv with
            | [ "propose"; root ] ->
                printfn "Proposed fingerprint: %s" (propose root)
                0
            | [ "accept"; root; fingerprint; by; evidence; at ] ->
                accept root fingerprint by evidence at
                0
            | [ "reference"; root ] ->
                // This one source-1 example consumes Root's actual acceptance/facts; it cannot renew them.
                let revision = Source1.custodyRevision

                let complete: Completeness =
                    {
                        ExpectedWorkKeys = Source1.expectedKeys
                        Sources =
                            [
                                (source root revision (workRoot + "/tasks.yml")).Source
                                (source root revision (workRoot + "/repository-profile.md")).Source
                            ]
                        SourceBound = true
                        ManifestSha256 = ""
                    }

                let authority =
                    invoke
                        root
                        revision
                        Source1.acceptedFingerprint
                        DateTimeOffset.UtcNow
                        (Source1.originalFacts Source1.expectedKeys Set.empty)
                        complete
                    |> require

                printfn
                    "Source authority: %s; works: %s; production projection: unsupported"
                    authority.Completeness.ManifestSha256
                    (authority.Works |> List.map _.Key |> String.concat ",")

                0
            | _ ->
                eprintfn
                    "Use propose ROOT; accept ROOT FINGERPRINT ACCEPTED_BY EVIDENCE ACCEPTED_AT; or reference ROOT. Only Root's admitted source-1 facts are used by this reference."

                2
        with error ->
            eprintfn "%s" error.Message
            1

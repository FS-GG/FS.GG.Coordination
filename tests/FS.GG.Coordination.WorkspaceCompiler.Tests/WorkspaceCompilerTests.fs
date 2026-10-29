namespace FS.GG.Coordination.WorkspaceCompiler.Tests

open System
open System.IO
open System.Security.Cryptography
open System.Text
open Xunit
open FS.GG.Coordination.Core.OrdinaryAssignment
open FS.GG.SDD.Artifacts
open FS.GG.SDD.Artifacts.TypedSpecifications
open FS.GG.Coordination.WorkspaceCompiler

module private Inputs =
    // Independent authored-population expectation, selected by Root before compilation.
    let keys = set [ "T001"; "T002"; "T003"; "T004" ]
    // Deterministic builds map __SOURCE_DIRECTORY__ to /_/. Discover the actual
    // checkout from the built assembly, with a finite bound and a real corpus witness.
    let root =
        let rec locate remaining (directory: DirectoryInfo) =
            if remaining = 0 || isNull directory then
                failwith "Actual1081 source corpus unavailable."
            elif
                File.Exists(Path.Combine(directory.FullName, "work/1081-workspace-obligations-compiler/tasks.yml"))
                && File.Exists(Path.Combine(directory.FullName, ".fsgg/project.yml"))
            then
                directory.FullName
            else
                locate (remaining - 1) directory.Parent

        locate 16 (DirectoryInfo AppContext.BaseDirectory)

    let revision = "source-control-copy/1081"

    let require =
        function
        | Ok value -> value
        | Error e -> failwithf "%A" e

    let id text = SpecificationId.create text |> require

    let bytes path =
        ReferenceConsumer.source root revision path

    let modelPath = ReferenceConsumer.readinessRoot + "/workspace-model.json"
    let accepted = lazy (bytes modelPath)

    let parsed =
        lazy
            (WorkspaceLifecycle.deserializeModel (Encoding.UTF8.GetString accepted.Value.Bytes)
             |> require)

    let fingerprint = lazy Source1.acceptedFingerprint

    let authored () =
        ReferenceConsumer.authoredPaths |> List.map bytes

    let closure () =
        [ bytes (ReferenceConsumer.workRoot + "/repository-profile.md") ]

    let modules () =
        parsed.Value.Modules
        |> List.map (fun m ->
            let source =
                (authored () @ closure ())
                |> List.find (fun s -> s.Source.Sha256 = m.ContentSha256)

            m.Id, source.Source)
        |> Map.ofList

    let workModel () =
        bytes (ReferenceConsumer.readinessRoot + "/work-model.json")

    let profile = bytes (ReferenceConsumer.workRoot + "/repository-profile.md")

    let parts () : Component list =
        [
            {
                Id = "1081-authored-source"
                Root = ReferenceConsumer.workRoot
                Language = "SDD"
                Commands = Source1.checks
                Sources = [ profile.Source ]
            }
        ]

    let completeness () : Completeness =
        {
            ExpectedWorkKeys = keys
            Sources = [ (bytes (ReferenceConsumer.workRoot + "/tasks.yml")).Source ]
            SourceBound = true
            ManifestSha256 = ""
        }

    let facts () = Source1.originalFacts keys Set.empty
    let now = Source1.selectedAt.AddMinutes 20.

    let compile model fingerprint moduleSources wm sources provider parts completeness facts now =
        Compiler.compile
            model
            fingerprint
            moduleSources
            wm
            sources
            (closure ())
            provider
            root
            [
                {
                    Identity = "FS-GG/FS.GG.Coordination"
                    Revision = revision
                }
            ]
            parts
            now
            completeness
            facts

    let run () =
        compile
            accepted.Value
            fingerprint.Value
            (modules ())
            (workModel ())
            (authored ())
            (Framework(id "REPO-1081"))
            (parts ())
            (completeness ())
            (facts ())
            now

    let refusal reason =
        function
        | Error value -> Assert.Contains(reason, sprintf "%A" value)
        | Ok _ -> failwith ("Expected causal refusal: " + reason)

    let changed (value: SourceBytes) text =
        let raw = Encoding.UTF8.GetBytes(text: string)

        {
            Source =
                { value.Source with
                    Sha256 = SHA256.HashData raw |> Convert.ToHexStringLower
                }
            Bytes = raw
        }

module WorkspaceCompilerTests =
    open Inputs

    [<Fact>]
    let ``real authored population composes and uses existing canonical authority`` () =
        let authority = run () |> require
        Assert.Equal<Set<string>>(keys, authority.Works |> List.map _.Key |> Set.ofList)
        Assert.Equal(authorityDigest authority, authority.Completeness.ManifestSha256)

        Assert.All(
            authority.Works,
            fun w ->
                Assert.Equal("REPO-1081", w.Workspace.Provider)
                Assert.Equal(Some Source1.deadline, w.Deadline)
                Assert.Contains("atlas", w.Skills)
                Assert.Contains("EV" + w.Key.Substring(1), w.RequiredOutputs)
        )

        Compiler.productionProjection authority
        |> refusal "reference-compiler-has-no-production-projection"

    [<Fact>]
    let ``actual separately maintained reference caller consumes same complete source`` () =
        let authority =
            ReferenceConsumer.invoke root revision fingerprint.Value now (facts ()) (completeness ())
            |> require

        Assert.Equal<Set<string>>(keys, authority.Completeness.ExpectedWorkKeys)

    [<Fact>]
    let ``independent expected population cannot omit a task`` () =
        let c =
            { completeness () with
                ExpectedWorkKeys = Set.remove "T004" keys
            }

        compile
            accepted.Value
            fingerprint.Value
            (modules ())
            (workModel ())
            (authored ())
            (Framework(id "REPO-1081"))
            (parts ())
            c
            (facts ())
            now
        |> refusal "independent-expected-work-population"

    [<Fact>]
    let ``omitted original work facts do not shrink independent population`` () =
        compile
            accepted.Value
            fingerprint.Value
            (modules ())
            (workModel ())
            (authored ())
            (Framework(id "REPO-1081"))
            (parts ())
            (completeness ())
            ((facts ()).Tail)
            now
        |> refusal "original-work-facts-population"

    [<Fact>]
    let ``forged generated task list is checked against authored bytes`` () =
        let wm =
            WorkModel.parseWorkModel
                {
                    Path = (workModel ()).Source.Path
                    Text = Encoding.UTF8.GetString (workModel ()).Bytes
                    RawBytes = Some((workModel ()).Bytes)
                }
            |> require

        let forged =
            { wm with
                Tasks = wm.Tasks |> List.filter (fun t -> t.Id <> "T004")
            }
            |> Serialization.serializeWorkModel

        compile
            accepted.Value
            fingerprint.Value
            (modules ())
            (changed (workModel ()) forged)
            (authored ())
            (Framework(id "REPO-1081"))
            (parts ())
            (completeness ())
            (facts ())
            now
        |> refusal "work-model-authored-correspondence"

    [<Fact>]
    let ``changed accepted base refuses`` () =
        compile
            accepted.Value
            (String('0', 64))
            (modules ())
            (workModel ())
            (authored ())
            (Framework(id "REPO-1081"))
            (parts ())
            (completeness ())
            (facts ())
            now
        |> refusal "accepted-workspace-fingerprint"

    [<Fact>]
    let ``removed or renamed module binding refuses`` () =
        compile
            accepted.Value
            fingerprint.Value
            (Map.remove (id "TASK-1081") (modules ()))
            (workModel ())
            (authored ())
            (Framework(id "REPO-1081"))
            (parts ())
            (completeness ())
            (facts ())
            now
        |> refusal "accepted-module-population"

    [<Fact>]
    let ``changed authored bytes refuse even with a newly computed raw hash`` () =
        let sources =
            authored ()
            |> List.map (fun s ->
                if s.Source.Path.EndsWith("/tasks.yml") then
                    changed s (Encoding.UTF8.GetString(s.Bytes) + "\n# changed\n")
                else
                    s)

        compile
            accepted.Value
            fingerprint.Value
            (modules ())
            (workModel ())
            sources
            (Framework(id "REPO-1081"))
            (parts ())
            (completeness ())
            (facts ())
            now
        |> refusal "accepted-module-source"

    [<Fact>]
    let ``wrong profile kind refuses`` () =
        compile
            accepted.Value
            fingerprint.Value
            (modules ())
            (workModel ())
            (authored ())
            (Framework(id "SPEC-1081"))
            (parts ())
            (completeness ())
            (facts ())
            now
        |> refusal "accepted-repository-profile"

    [<Fact>]
    let ``component root dot remains unsupported`` () =
        let parts = parts () |> List.map (fun p -> { p with Root = "." })

        compile
            accepted.Value
            fingerprint.Value
            (modules ())
            (workModel ())
            (authored ())
            (Framework(id "REPO-1081"))
            parts
            (completeness ())
            (facts ())
            now
        |> refusal "component-root-unsupported"

    [<Fact>]
    let ``component cannot claim source paths from another physical root`` () =
        let parts =
            parts ()
            |> List.map (fun p ->
                { p with
                    Root = "examples/WorkspaceObligationsCompiler"
                })

        compile
            accepted.Value
            fingerprint.Value
            (modules ())
            (workModel ())
            (authored ())
            (Framework(id "REPO-1081"))
            parts
            (completeness ())
            (facts ())
            now
        |> refusal "component-source-path-mismatch"

    [<Fact>]
    let ``missing source deadline is not manufactured`` () =
        let facts = facts () |> List.map (fun w -> { w with Deadline = None })

        compile
            accepted.Value
            fingerprint.Value
            (modules ())
            (workModel ())
            (authored ())
            (Framework(id "REPO-1081"))
            (parts ())
            (completeness ())
            facts
            now
        |> refusal "deadline-missing"

    [<Fact>]
    let ``unknown hard resource demand is not zero`` () =
        let facts =
            facts ()
            |> List.map (fun w ->
                { w with
                    Demand = Map.add "memory-bytes" None w.Demand
                })

        compile
            accepted.Value
            fingerprint.Value
            (modules ())
            (workModel ())
            (authored ())
            (Framework(id "REPO-1081"))
            (parts ())
            (completeness ())
            facts
            now
        |> refusal "hard-demand-unknown"

    [<Fact>]
    let ``original source deadline cannot be renewed by later observation`` () =
        compile
            accepted.Value
            fingerprint.Value
            (modules ())
            (workModel ())
            (authored ())
            (Framework(id "REPO-1081"))
            (parts ())
            (completeness ())
            (facts ())
            Source1.deadline
        |> refusal "original-source-window-exhausted"

    [<Fact>]
    let ``unresolved Root assessment remains a refusal`` () =
        let facts =
            facts ()
            |> List.map (fun w ->
                { w with
                    Assessment =
                        { w.Assessment with
                            MissingFacts = set["accepted-workspace-model-and-fingerprint"]
                        }
                })

        compile
            accepted.Value
            fingerprint.Value
            (modules ())
            (workModel ())
            (authored ())
            (Framework(id "REPO-1081"))
            (parts ())
            (completeness ())
            facts
            now
        |> refusal "assessment-missing-facts"

    [<Fact>]
    let ``over-bound independent source input requires decomposition`` () =
        let tooMany = List.replicate 129 (authored ()).Head

        compile
            accepted.Value
            fingerprint.Value
            (modules ())
            (workModel ())
            tooMany
            (Framework(id "REPO-1081"))
            (parts ())
            (completeness ())
            (facts ())
            now
        |> refusal "source-or-work-bound"

    let private generated provider recordChange =
        let catalog, id, record, provenance = GeneratedControls.values revision

        let parts =
            parts ()
            |> List.map (fun p ->
                { p with
                    Language = "opaque"
                    Commands = p.Commands.Add "control:check"
                })

        compile
            accepted.Value
            fingerprint.Value
            (modules ())
            (workModel ())
            (authored ())
            (Generated(catalog, defaultArg provider id, [], provenance (recordChange record)))
            parts
            (completeness ())
            (facts ())
            now

    [<Fact>]
    let ``synthetic generated declaration uses prepared identity without availability claims`` () =
        let authority = generated None (fun x -> x) |> require

        Assert.All(
            authority.Works,
            fun w ->
                Assert.Equal("opaque-control", w.Workspace.Provider)
                Assert.Contains("control:check", w.Capabilities)
                Assert.Contains("control-proof", w.RequiredOutputs)
        )

    [<Fact>]
    let ``selected provider must actually exist in the pinned catalog`` () =
        generated (Some "unselected-control") (fun x -> x)
        |> refusal "provider-selection-invalid"

    [<Fact>]
    let ``changed effective provenance refuses despite valid codec and raw hash`` () =
        generated None (fun p ->
            let effective =
                p.Declaration.EffectiveParameters
                |> List.map (fun (k, v) -> k, (if k = "raw" then "other" else v))

            { p with
                Declaration =
                    { p.Declaration with
                        RawProductName = "other"
                        EffectiveParameters = effective
                    }
                Ownership =
                    { p.Ownership with
                        EffectiveParameters = effective
                    }
            })
        |> refusal "provider-provenance-correspondence"

    [<Fact>]
    let ``different catalog identity in structurally valid provenance refuses`` () =
        generated None (fun p ->
            { p with
                Declaration =
                    { p.Declaration with
                        CatalogRevision = "other"
                    }
            })
        |> refusal "provider-provenance-correspondence"

    [<Fact>]
    let ``unsupported local identity is not replaced with a board issue`` () =
        let facts = facts () |> List.map (fun w -> { w with Identity = Local("", "") })

        compile
            accepted.Value
            fingerprint.Value
            (modules ())
            (workModel ())
            (authored ())
            (Framework(id "REPO-1081"))
            (parts ())
            (completeness ())
            facts
            now
        |> refusal "local-identity-missing"

    [<Fact>]
    let ``existing schema3 reader joins source catalog without a second decoder`` () =
        let catalog, provider, record, _ = GeneratedControls.values revision

        let parts =
            parts ()
            |> List.map (fun p ->
                { p with
                    Language = "opaque"
                    Commands = p.Commands.Add "control:check"
                })

        let authority =
            compile
                accepted.Value
                fingerprint.Value
                (modules ())
                (workModel ())
                (authored ())
                (Generated(catalog, provider, [], GeneratedControls.source3 revision record))
                parts
                (completeness ())
                (facts ())
                now
            |> require

        Assert.All(authority.Works, fun w -> Assert.Equal(provider, w.Workspace.Provider))

    [<Fact>]
    let ``declared provider language cannot disappear from component mapping`` () =
        let catalog, provider, record, provenance = GeneratedControls.values revision

        let parts =
            parts ()
            |> List.map (fun p ->
                { p with
                    Commands = p.Commands.Add "control:check"
                })

        compile
            accepted.Value
            fingerprint.Value
            (modules ())
            (workModel ())
            (authored ())
            (Generated(catalog, provider, [], provenance record))
            parts
            (completeness ())
            (facts ())
            now
        |> refusal "provider-language-component-binding"

    [<Fact>]
    let ``oversize captured source refuses before decoding`` () =
        let oversized =
            { accepted.Value with
                Bytes = Array.zeroCreate (1024 * 1024 + 1)
            }

        compile
            oversized
            fingerprint.Value
            (modules ())
            (workModel ())
            (authored ())
            (Framework(id "REPO-1081"))
            (parts ())
            (completeness ())
            (facts ())
            now
        |> refusal "source-byte-bound"

    [<Fact>]
    let ``unsupported work assessment contract is not reinterpreted`` () =
        let facts =
            facts ()
            |> List.map (fun w ->
                { w with
                    Assessment =
                        { w.Assessment with
                            Version = "work-assessment/99"
                        }
                })

        compile
            accepted.Value
            fingerprint.Value
            (modules ())
            (workModel ())
            (authored ())
            (Framework(id "REPO-1081"))
            (parts ())
            (completeness ())
            facts
            now
        |> refusal "contract-version"

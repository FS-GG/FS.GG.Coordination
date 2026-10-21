namespace FS.GG.Coordination.Orchestration.Host.Tests

open System
open System.IO
open System.Security.Cryptography
open FS.GG.Coordination.Core
open FS.GG.Coordination.Core.Orchestration
open FS.GG.Coordination.Core.OrdinaryAssignment
open Xunit

/// Closed, source-bound fixtures. These capabilities are fixture evidence, not host observations.
module OrdinaryAssignmentFixtures =
    let revision = "9d99573628ee31e966a61a28b9b36f7414b10f33"
    let source path sha : Source = {Repository="FS-GG/FS.GG.Coordination";Revision=revision;Path=path;Sha256=sha}
    let sources = [
        source "src/FS.GG.Coordination.Core/Orchestration.fs" "d71a62d971eb1e73ac2283f2fb3e7b6cf78a49f44b02f441740891c73c361eb4"
        source "tests/FS.GG.Coordination.UnitTests/OrchestrationTests.fs" "60a4887417372543f6133cd1aabfa7d1077d4699e748eb6ff6353dcc3b562d41"
        source "eng/bootstrap-gates/compiler-and-tests.sh" "97dc9e10c6268a6fef80fc026fc51a676eb567c416b32aa64ea4dfc4acf999bb"
        source "tests/FS.GG.Coordination.Orchestration.Execution.Tests/PortableWorkspaceExecutorTests.fs" "4b158b683d1823f497d56556a7ca4703c0e07da2438910aec74e85e2985c2ce5"
        source "tests/portable-workspace/executor/composed/frontend/app.mjs" "eb540909e6779d9b06d7c9ef56b41202d9588c2caa0903caa47724f1cbb3ec95"
        source "tests/portable-workspace/executor/composed/backend/service.py" "d7aa14a616776e319b0811a9701f274b942beb733a88375c02dd41ccefb5bc87"
        source "tests/portable-workspace/executor/composed/backend/build.py" "9cdf8977476a9ba1bf36a710dc571b39055fde21d28ae9d95e3ec869c670e768"
        source "tests/portable-workspace/executor/composed/backend/test.py" "2a04f69dcf6c07b5a47fc61585078522509a48d7041f67679dccfb8e4b0e331d"
        source "tests/portable-workspace/executor/composed/frontend/build.mjs" "61dc3615be54b09f0ec512ce268ecdcd9c674b05da24733d30dc41dd5ca300a4"
        source "tests/portable-workspace/executor/composed/frontend/test.mjs" "1becb637cc44946e0b35ed3c7c1cbeaa36e6655b287f4444af13df8c27f5ba66"
        source "tests/portable-workspace/executor/composed/frontend/journey.mjs" "e29e0db8a660715e68bc5b6a9d6d99d4264f0b087d264548eaec7f3d4e3d577d"
        source "tests/portable-workspace/executor/composed/product/journey.py" "f5cd752f9a2e81ad7dd076b570c52afdb4857e1b4e6f8d0e58723c6e331365a7"
    ]
    let now = DateTimeOffset(2026,10,21,8,0,0,TimeSpan.Zero)
    let workItem = WorkItemIdentity.create "R_coordination_fixture" 1L "I_framework_fixture" 1038L
    let productItem = WorkItemIdentity.create "R_coordination_fixture" 1L "I_product_fixture" 2000L
    let frameworkSources = sources |> List.filter (fun s -> not (s.Path.Contains("portable-workspace")) && not (s.Path.Contains("PortableWorkspace")))
    let productSources = sources |> List.filter (fun s -> s.Path.Contains("portable-workspace") || s.Path.Contains("PortableWorkspace"))
    let frameworkCommands = set [
        "dotnet build FS.GG.Coordination.sln --configuration Release --no-restore --warnaserror"
        "dotnet test tests/FS.GG.Coordination.UnitTests/FS.GG.Coordination.UnitTests.fsproj --configuration Release --no-build --no-restore" ]
    let backendCommands = set ["python3 build.py";"python3 test.py"]
    let frontendCommands = set ["node build.mjs";"node test.mjs"]
    let journeyCommands = set ["python3 journey.py"]
    let productCommands = Set.unionMany [backendCommands;frontendCommands;journeyCommands]
    let fixtureComponent id root language commands refs : Component = {Id=id;Root=root;Language=language;Commands=commands;Sources=refs}
    let workspace name components refs : WorkspaceExecutionContract = {
        Version="workspace-execution/1";Workspace=name;Provider="maintained-source-fixture/1"
        Repositories=[{Identity="FS-GG/FS.GG.Coordination";Revision=revision}];Components=components;Sources=refs }
    let assessment : WorkAssessment = {
        Version="work-assessment/1";Phase="implement";Outcome="source acceptance";Objective="Validate ordinary source assignment"
        Plan="source-only closed fixture";Facts=set ["source-bound complete fixture"];Interpretation="bounded test data"
        Difficulty=40;Uncertainty=20;EffectRisk=10;MissingFacts=Set.empty }
    let requirements key identity workspace capabilities checks cpu memory : Requirements = {
        Key=key;Identity=Board identity;Workspace=workspace;Assessment=assessment
        Attempt=Id.attempt (Guid.Parse(if key="framework" then "10000000-0000-0000-0000-000000000001" else "20000000-0000-0000-0000-000000000001"))
        Generation=Some(Id.generation 1L);SelectedAt=now;Deadline=Some(now.AddMinutes 20.)
        Recovery=Some {OriginalStartedAt=now;OriginalDeadline=now.AddMinutes 20.;MaxAttempts=1;AttemptOrdinal=1}
        Dependencies=set ["accepted-baseline"];RequiredChecks=checks;Capabilities=capabilities
        Tools=capabilities;Skills=set ["source-review"];Context=set ["mandatory-source-contract"]
        AllowedPaths=set [if key="framework" then "src/FS.GG.Coordination.Core" else "tests/portable-workspace/executor/composed"]
        AuthorizedEffects=set ["source-write"];RequiredOutputs=set ["source-change";"validation-evidence"];RequiredReviews=set ["independent-source-review"]
        Demand=Map.ofList ["cpu-millicores",Some cpu;"memory-bytes",Some memory];Priority=10 }
    let framework = requirements "framework" workItem
                        (workspace "coordination-framework" [fixtureComponent "core" "src/FS.GG.Coordination.Core" "fsharp" frameworkCommands frameworkSources] frameworkSources)
                        (set ["dotnet"]) frameworkCommands 700L 536870912L
    let product = requirements "product" productItem
                        (workspace "composed-product" [
                            fixtureComponent "backend" "tests/portable-workspace/executor/composed/backend" "python" backendCommands (productSources |> List.filter (fun s -> s.Path.Contains("/backend/")))
                            fixtureComponent "frontend" "tests/portable-workspace/executor/composed/frontend" "javascript" frontendCommands (productSources |> List.filter (fun s -> s.Path.Contains("/frontend/")))
                            fixtureComponent "journey" "tests/portable-workspace/executor/composed/product" "python" journeyCommands (productSources |> List.filter (fun s -> s.Path.Contains("/product/"))) ] productSources)
                        (set ["python";"node";"subprocess-launch";"subprocess-retirement";"loopback-http"]) productCommands 800L 671088640L
    let profile id capabilities cost : ExecutionProfile = {
        Id=id;Version="fixture-profile/1";Requested={Model="fixture-model";Effort="high";Runtime="fixture-only"};Resolved=None;Observed=None
        Capabilities=capabilities |> Seq.map (fun c -> c,{Available=true;Evidence=Some sources.Head}) |> Map.ofSeq
        Tools=capabilities;Skills=set ["source-review"];Context=set ["mandatory-source-contract"]
        Effects=set ["source-write"];Outputs=set ["source-change";"validation-evidence"];Reviews=set ["independent-source-review"]
        AdditionalDemand=Map.ofList ["cpu-millicores",Some 100L;"memory-bytes",Some 67108864L]
        RecoverySupported=true;Economics=Some {Currency="fixture-currency";ProvenanceClass="declared-fixture/1";CostMicroUnits=cost;DurationMilliseconds=100L;ReworkMilliseconds=10L} }
    let frameworkProfile = profile "framework-dotnet" (set ["dotnet"]) 1L
    let productProfile = profile "product-python-node" product.Capabilities 2L
    let authority works = bindAuthority {Version="ordinary-authority/1";Works=works;Completeness={ExpectedWorkKeys=works |> List.map _.Key |> Set.ofList;Sources=sources;SourceBound=true;ManifestSha256=""}}
    let snapshot (authority: AuthoritativeObligations) =
        let heldId = Id.reservation (Guid.Parse "90000000-0000-0000-0000-000000000001")
        let project = ProjectOrchestrator.initial (Id.project(Guid.Parse "90000000-0000-0000-0000-000000000002")) 4 1
        let project = ProjectOrchestrator.evolve project ProjectResumed
        let project = ProjectOrchestrator.evolve project (Allocated(heldId,workItem))
        {Version=policyVersion;AuthoritySha256=authorityDigest authority;Works=authority.Works;Profiles=[frameworkProfile;productProfile]
         Now=now;Capacity=Map.ofList ["cpu-millicores",Some 2500L;"memory-bytes",Some 2147483648L]
         RecoveryHeadroom=Map.ofList ["cpu-millicores",Some 500L;"memory-bytes",Some 268435456L]
         Reservations=[{Id=heldId;Work=Board workItem;Demand=Map.ofList ["cpu-millicores",Some 100L;"memory-bytes",Some 134217728L];Paths=set ["held/unknown-operation"];TerminalState="unknown"}]
         ReservationPopulationComplete=true;Project=project;SatisfiedDependencies=set ["accepted-baseline"]}
    let accepted = function Ok value -> value | Error error -> failwithf "expected useful positive decision, got %A" error
    let refused = function Error _ -> () | Ok value -> failwithf "expected refusal, got %A" value
    let selectedDecision (selected: OrdinaryAssignment.Assignment list) (decision: OrdinaryAssignment.Decision) =
        let economics = selected |> List.choose (fun a -> a.Specification.Profile.Economics)
        let preference = if economics.Length<>selected.Length then NonEconomicFallback else Economic(economics.Head.Currency,economics.Head.ProvenanceClass,economics |> List.sumBy _.CostMicroUnits,economics |> List.sumBy _.DurationMilliseconds,economics |> List.sumBy _.ReworkMilliseconds)
        {decision with Selected=selected;Preference=preference}

open OrdinaryAssignmentFixtures

type OrdinaryAssignmentPolicyTests() =
    [<Fact>]
    member _.``maintained framework and product select useful different ordinary profiles jointly``() =
        let a=authority [framework;product]
        let s=snapshot a
        let d=OrdinaryAssignmentPolicy.select s |> accepted
        Assert.Equal(2,d.Selected.Length)
        Assert.Equal(Economic("fixture-currency","declared-fixture/1",3L,200L,20L),d.Preference)
        Assert.Equal<string list>(["framework-dotnet";"product-python-node"],d.Selected |> List.map (fun a -> a.Specification.Profile.Id))
        Assert.All(d.Selected,fun a -> Assert.True(a.ExperimentEnrollment.IsNone))
        OrdinaryAssignmentVerifier.verify a s d |> accepted
        Assert.Equal("unknown",s.Reservations.Head.TerminalState)
        Assert.Equal(1,s.Project.Reservations.Count)
        Assert.True(SchedulerReservation.recoveryCapacityPreserved s.Project)

    [<Fact>]
    member _.``closed fixture manifests bind maintained bytes invocation and acceptance``() =
        let rec root (d: DirectoryInfo) =
            if File.Exists(Path.Combine(d.FullName,"eng/bootstrap-gates/compiler-and-tests.sh")) then d.FullName
            elif isNull d.Parent then failwith "source checkout unavailable" else root d.Parent
        let root=root (DirectoryInfo AppContext.BaseDirectory)
        for source in sources do
            let bytes=File.ReadAllBytes(Path.Combine(root,source.Path))
            Assert.Equal(source.Sha256,SHA256.HashData bytes |> Convert.ToHexStringLower)
        let compiler=File.ReadAllText(Path.Combine(root,"eng/bootstrap-gates/compiler-and-tests.sh"))
        for command in frameworkCommands do Assert.Contains(command,compiler)
        let invocation=File.ReadAllText(Path.Combine(root,"tests/FS.GG.Coordination.Orchestration.Execution.Tests/PortableWorkspaceExecutorTests.fs"))
        for command in ["build.mjs";"test.mjs";"build.py";"test.py";"journey.py";"frontend-backend-http-v1"] do Assert.Contains(command,invocation)
        let journey=File.ReadAllText(Path.Combine(root,"tests/portable-workspace/executor/composed/product/journey.py"))
        for operation in ["subprocess.Popen";"subprocess.run";"127.0.0.1";"server.terminate()";"server.wait(timeout=5)"] do Assert.Contains(operation,journey)
        Assert.Equal(3,product.Workspace.Components.Length)
        Assert.Equal(revision,product.Workspace.Repositories.Head.Revision)

    [<Fact>]
    member _.``planner and candidate omissions cannot redefine authoritative obligations``() =
        let a=authority [framework;product]
        let s=snapshot a
        let d=OrdinaryAssignmentPolicy.select s |> accepted
        let mutations : (Requirements -> Requirements) list = [
            fun w -> {w with Dependencies=Set.empty}
            fun w -> {w with RequiredChecks=Set.remove (Set.minElement w.RequiredChecks) w.RequiredChecks}
            fun w -> {w with Capabilities=Set.empty}
            fun w -> {w with Generation=None}
            fun w -> {w with Recovery=None}
            fun w -> {w with Deadline=None} ]
        for mutate in mutations do
            let altered={s with Works=mutate framework :: [product]}
            OrdinaryAssignmentVerifier.verify a altered d |> refused
            let changed=bindAssignment {d.Selected.Head with Specification={d.Selected.Head.Specification with Requirements=mutate framework}}
            let decision={d with Selected=changed::d.Selected.Tail;Eligible=changed::d.Eligible.Tail}
            OrdinaryAssignmentVerifier.verify a s decision |> refused
        let stale={a with Works=[{framework with Priority=11};product]}
        OrdinaryAssignmentVerifier.verify stale s d |> refused
        let omitted={a with Works=[framework]}
        OrdinaryAssignmentVerifier.verify omitted s d |> refused
        OrdinaryAssignmentVerifier.verify {a with Completeness={a.Completeness with SourceBound=false}} s d |> refused

    [<Fact>]
    member _.``joint resources and writable paths reject unions which fit individually``() =
        let a=authority [framework;product]
        let s={snapshot a with Capacity=Map.ofList ["cpu-millicores",Some 1600L;"memory-bytes",Some 2147483648L]}
        let d=OrdinaryAssignmentPolicy.select s |> accepted
        Assert.Equal(2,d.Eligible.Length)
        Assert.Single(d.Selected) |> ignore
        for candidate in d.Eligible do OrdinaryAssignmentVerifier.verify a s (selectedDecision [candidate] d) |> accepted
        OrdinaryAssignmentVerifier.verify a s (selectedDecision d.Eligible d) |> refused
        let product={product with AllowedPaths=framework.AllowedPaths}
        let a=authority [framework;product]
        let s=snapshot a
        let d=OrdinaryAssignmentPolicy.select s |> accepted
        for candidate in d.Eligible do OrdinaryAssignmentVerifier.verify a s (selectedDecision [candidate] d) |> accepted
        OrdinaryAssignmentVerifier.verify a s (selectedDecision d.Eligible d) |> refused

    [<Fact>]
    member _.``missing observed product capabilities never become declarations of support``() =
        for capability in product.Capabilities do
            let a=authority [product]
            let s=snapshot a
            let profile={productProfile with Capabilities=Map.add capability {Available=true;Evidence=None} productProfile.Capabilities}
            OrdinaryAssignmentPolicy.select {s with Profiles=[profile]} |> refused
            OrdinaryAssignmentPolicy.select {s with Profiles=[{profile with Capabilities=Map.remove capability profile.Capabilities}]} |> refused

    [<Fact>]
    member _.``unknown hard resources and unknown held state never release capacity``() =
        let a=authority [framework;product]
        let s=snapshot a
        OrdinaryAssignmentPolicy.select {s with Capacity=Map.add "cpu-millicores" None s.Capacity} |> refused
        OrdinaryAssignmentPolicy.select {s with ReservationPopulationComplete=false} |> refused
        OrdinaryAssignmentPolicy.select {s with Reservations=[]} |> refused
        let held={s.Reservations.Head with Demand=Map.add "cpu-millicores" (Some 2000L) s.Reservations.Head.Demand;TerminalState="process-completed-without-release"}
        OrdinaryAssignmentPolicy.select {s with Reservations=[held]} |> refused

    [<Fact>]
    member _.``replay ordering and authoritative changes have explicit identity effects``() =
        let a=authority [framework;product]
        let s=snapshot a
        let d=OrdinaryAssignmentPolicy.select s |> accepted
        let reordered={s with Works=List.rev s.Works;Profiles=List.rev s.Profiles}
        let replay=OrdinaryAssignmentPolicy.select reordered |> accepted
        Assert.Equal<string list>(d.Selected |> List.map _.AssignmentSha256,replay.Selected |> List.map _.AssignmentSha256)
        OrdinaryAssignmentVerifier.verify a reordered replay |> accepted
        let a2=authority [{framework with Priority=11};product]
        let s2=snapshot a2
        let d2=OrdinaryAssignmentPolicy.select s2 |> accepted
        Assert.NotEqual<string>(d.Selected.Head.AssignmentSha256,d2.Selected.Head.AssignmentSha256)
        OrdinaryAssignmentVerifier.verify a2 s2 d |> refused
        let held=d.Selected.Head.Specification.Reservation
        let project={s.Project with Reservations=Map.add held workItem s.Project.Reservations}
        let reservation={s.Reservations.Head with Id=held}
        OrdinaryAssignmentVerifier.verify a {s with Project=project;Reservations=reservation::s.Reservations} d |> refused

    [<Fact>]
    member _.``original deadline retry lineage outputs effects and arithmetic remain hard``() =
        let a=authority [framework]
        let s=snapshot a
        OrdinaryAssignmentPolicy.select {s with Now=now.AddHours 1.} |> refused
        let changed={framework with Recovery=Some {framework.Recovery.Value with OriginalStartedAt=now.AddMinutes 1.}}
        OrdinaryAssignmentPolicy.select {s with Works=[changed]} |> refused
        let exhausted={framework with Recovery=Some {framework.Recovery.Value with AttemptOrdinal=2}}
        OrdinaryAssignmentPolicy.select {s with Works=[exhausted]} |> refused
        for profile in [{frameworkProfile with Outputs=Set.empty};{frameworkProfile with Effects=Set.empty};{frameworkProfile with Reviews=Set.empty};{frameworkProfile with RecoverySupported=false}] do
            OrdinaryAssignmentPolicy.select {s with Profiles=[profile]} |> refused
        let overflow={frameworkProfile with AdditionalDemand=Map.ofList ["cpu-millicores",Some Int64.MaxValue]}
        OrdinaryAssignmentPolicy.select {s with Profiles=[overflow]} |> refused
        let staleAttempt={framework with Attempt=Id.attempt(Guid.Parse "70000000-0000-0000-0000-000000000001")}
        let d=OrdinaryAssignmentPolicy.select s |> accepted
        OrdinaryAssignmentVerifier.verify a {s with Works=[staleAttempt]} d |> refused

    [<Fact>]
    member _.``optional economics uses explicit fallback without zero imputation``() =
        let a=authority [framework;product]
        let s=snapshot a
        let s={s with Profiles=[{frameworkProfile with Economics=None};productProfile]}
        let d=OrdinaryAssignmentPolicy.select s |> accepted
        Assert.Equal(NonEconomicFallback,d.Preference)
        OrdinaryAssignmentVerifier.verify a s d |> accepted
        let bad={d with Preference=Economic("fixture-currency","declared-fixture/1",0L,0L,0L)}
        OrdinaryAssignmentVerifier.verify a s bad |> refused

    [<Fact>]
    member _.``bounded inputs refuse excessive populations and oversized text``() =
        let a=authority [framework]
        let s=snapshot a
        OrdinaryAssignmentPolicy.select {s with Works=List.replicate 33 framework} |> refused
        OrdinaryAssignmentPolicy.select {s with Works=[{framework with Assessment={framework.Assessment with Objective=String.replicate 513 "x"}}]} |> refused

    [<Fact>]
    member _.``untrusted oversized decisions refuse before selected pair and payload proofs``() =
        let a=authority [framework]
        let s=snapshot a
        let d=OrdinaryAssignmentPolicy.select s |> accepted
        let oversized={d with Selected=List.replicate 10001 d.Selected.Head}
        match OrdinaryAssignmentVerifier.verify a s oversized with
        | Error(NeedsDecomposition ["decision-population-bound"]) -> ()
        | other -> failwithf "expected early bounded population refusal, got %A" other
        let oversized={d with Eligible=List.replicate 10001 d.Selected.Head}
        match OrdinaryAssignmentVerifier.verify a s oversized with
        | Error(NeedsDecomposition ["decision-population-bound"]) -> ()
        | other -> failwithf "expected early eligible population refusal, got %A" other
        let spec=d.Selected.Head.Specification
        let oversizedCandidate={d.Selected.Head with Specification={spec with Requirements={framework with Assessment={assessment with Objective=String.replicate 513 "x"}}}}
        OrdinaryAssignmentVerifier.verify a s {d with Selected=[oversizedCandidate]} |> refused
        let forged=[for n in 1..8 -> {d.Selected.Head with Specification={spec with Requirements={framework with Key="forged-"+string n;AllowedPaths=set [for path in 1..256 -> "forged/"+string path]}}}]
        match OrdinaryAssignmentVerifier.verify a s {d with Selected=forged} with
        | Error(ConflictingEvidence ["authoritative-snapshot-or-candidate-mismatch"]) -> ()
        | Error(NeedsDecomposition _) -> () // A stricter payload ceiling is also an early refusal.
        | other -> failwithf "expected bounded early forged-path refusal, got %A" other
        OrdinaryAssignmentPolicy.select {s with Project={s.Project with RecoveryCapacity= -1}} |> refused
        OrdinaryAssignmentPolicy.select {s with Reservations=[{s.Reservations.Head with Paths=set ["../unsafe"]}]} |> refused

    [<Fact>]
    member _.``held path cross products count against the whole call evaluation ceiling``() =
        let crowdedWork={framework with AllowedPaths=set [for n in 1..256 -> "candidate/path-"+string n]}
        let a=authority [crowdedWork]
        let s=snapshot a
        let held=[for n in 1..64 ->
                    {s.Reservations.Head with Id=Id.reservation(Guid.Parse(sprintf "80000000-0000-0000-0000-%012d" n))
                                              Demand=Map.ofList ["cpu-millicores",Some 0L;"memory-bytes",Some 0L]
                                              Paths=set [for path in 1..256 -> "held/"+string n+"/path-"+string path]}]
        let project={s.Project with Capacity=100;Reservations=held |> List.map (fun r -> r.Id,workItem) |> Map.ofList}
        let s={s with Reservations=held;Project=project;Profiles=[frameworkProfile]}
        Assert.True(snapshotEvaluationBound s>int64 maximumEvaluations)
        match OrdinaryAssignmentPolicy.select s with
        | Error(NeedsDecomposition reasons) -> Assert.Contains(reasons,fun reason -> reason.Contains("whole-call-evaluation-bound"))
        | other -> failwithf "expected pre-proof held-path work refusal, got %A" other

    [<Fact>]
    member _.``empty economic unit and provenance labels select ordinary fallback independently verified``() =
        let a=authority [framework;product]
        for currency,provenance in ["","declared-fixture/1";"   ","declared-fixture/1";"fixture-currency","";"fixture-currency"," \t "] do
            let original=snapshot a
            let profiles=original.Profiles |> List.map (fun profile ->
                {profile with Economics=Some {profile.Economics.Value with Currency=currency;ProvenanceClass=provenance}})
            let s={original with Profiles=profiles}
            let d=OrdinaryAssignmentPolicy.select s |> accepted
            Assert.Equal(2,d.Selected.Length)
            Assert.Equal(NonEconomicFallback,d.Preference)
            OrdinaryAssignmentVerifier.verify a s d |> accepted
            // Matching empty strings are not independent evidence of comparable units.
            let forged={d with Preference=Economic(currency,provenance,3L,200L,20L)}
            OrdinaryAssignmentVerifier.verify a s forged |> refused

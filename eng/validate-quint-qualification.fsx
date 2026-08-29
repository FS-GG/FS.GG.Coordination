open System
open System.Collections.Generic
open System.IO
open System.Security.Cryptography
open System.Text
open System.Text.Json
open System.Text.Json.Nodes
open System.Text.RegularExpressions

let fail code detail = Error($"%s{code}: %s{detail}")
let sha256 (bytes: byte array) = SHA256.HashData bytes |> Convert.ToHexString |> _.ToLowerInvariant()
let strings (node: JsonNode) (name: string) = node[name].AsArray() |> Seq.map _.GetValue<string>() |> Seq.toList
let objects (node: JsonNode) (name: string) = node[name].AsArray() |> Seq.map _.AsObject() |> Seq.toList
let text (node: JsonNode) (name: string) = node[name].GetValue<string>()
let number (node: JsonNode) (name: string) = node[name].GetValue<int>()

let unique code label values =
    let duplicates = values |> List.countBy id |> List.filter (fun (_, count) -> count <> 1)
    if List.isEmpty duplicates then Ok() else fail code ($"%s{label}=%A{duplicates}")

let exact code label expected actual =
    if expected = actual then Ok() else fail code ($"%s{label}; expected=%A{expected}; actual=%A{actual}")

let bind next result = Result.bind next result

let validateDocument root (document: JsonObject) =
    try
        let sourceRelative = text document "source"
        let sourcePath = Path.Combine(root, sourceRelative.Replace('/', Path.DirectorySeparatorChar))
        let sourceBytes = File.ReadAllBytes sourcePath
        let sourceText = Encoding.UTF8.GetString sourceBytes
        let protocolFence = sourceText.Split("```", StringSplitOptions.None)[1]

        let declared pattern =
            Regex.Matches(protocolFence, pattern, RegexOptions.Multiline ||| RegexOptions.CultureInvariant)
            |> Seq.cast<Match>
            |> Seq.map (fun item -> item.Groups[1].Value)
            |> Seq.toList

        let modules = objects document "modules"
        let moduleIds = modules |> List.map (fun item -> text item "id")
        let imports = modules |> List.map (fun item -> text item "id", strings item "imports") |> Map.ofList
        let classifications collection =
            objects document collection
            |> List.map (fun item -> text item "name", text item "class", text item "module")
        let state = classifications "state"
        let actions = classifications "actions"
        let roots = objects document "roots"
        let rootIds = roots |> List.map (fun item -> text item "id")
        let rootModules = roots |> List.map (fun item -> text item "module")
        let allowedClasses = Set [ "essential"; "derived"; "bookkeeping" ]
        let requiredAdmission =
            Set [ "owner"; "imports"; "invariants"; "independentOracles"; "root"; "bounds"; "witnesses"
                  "projections"; "ciImpact"; "budgetEffect" ]
        let requiredOracles =
            Set [ "claim-exclusion"; "stale-projection"; "dependency-concurrency"; "partial-operation"
                  "old-client-fencing"; "ledger-tamper"; "exact-head-review"; "post-merge-verification"
                  "dual-feed-recovery"; "abstraction-equivalence"; "scale-envelope" ]
        let knownProjections =
            Set [ "typed-authority"; "compiled-contract"; "lifecycle-status"; "native-relations"
                  "protocol-stream"; "mutation-census"; "settings-plans"; "qualification-manifest"
                  "model-test-inventory" ]
        let allowedCiImpact = Set [ "bounded-state-root"; "bounded-test-root" ]
        let declaredDefinition name =
            Regex.IsMatch(sourceText, $"(?m)^\\s*(pure\\s+)?(val|def|run|action)\\s+%s{Regex.Escape name}(\\s|\\(|=)")

        let rec closure visiting moduleId =
            if Set.contains moduleId visiting then fail "QQ-MODULE-CYCLE" moduleId
            elif not (Map.containsKey moduleId imports) then fail "QQ-MODULE-UNKNOWN" moduleId
            else
                imports[moduleId]
                |> List.fold (fun state dependency ->
                    state |> bind (fun found -> closure (Set.add moduleId visiting) dependency |> Result.map (Set.union found))) (Ok Set.empty)
                |> Result.map (Set.add moduleId)

        let rec dependencyDepth moduleId =
            match imports[moduleId] with
            | [] -> 1
            | dependencies -> 1 + (dependencies |> List.map dependencyDepth |> List.max)

        let classificationCheck kind declaredNames rows =
            rows
            |> List.map (fun (name, _, _) -> name)
            |> unique "QQ-CLASSIFICATION-DUPLICATE" kind
            |> bind (fun () -> exact "QQ-CLASSIFICATION" kind (Set declaredNames) (rows |> List.map (fun (name, _, _) -> name) |> Set.ofList))
            |> bind (fun () ->
                rows
                |> List.tryFind (fun (_, classification, owner) -> not (Set.contains classification allowedClasses) || not (Map.containsKey owner imports))
                |> function None -> Ok() | Some row -> fail "QQ-CLASSIFICATION-SHAPE" ($"%A{row}"))

        let rootCheck (item: JsonObject) =
            let id = text item "id"
            let owner = text item "module"
            let expectedClosure = strings item "closure" |> Set.ofList
            closure Set.empty owner
            |> bind (fun actualClosure -> exact "QQ-ROOT-CLOSURE" id expectedClosure actualClosure)
            |> bind (fun () ->
                let main = text item "main"
                if sourceText.Contains($"module %s{main} {{", StringComparison.Ordinal) then Ok()
                else fail "QQ-ROOT-MAIN" ($"%s{id}:%s{main}"))
            |> bind (fun () ->
                [ "positive"; "adversarial"; "invalid" ]
                |> List.tryFind (fun field -> not (sourceText.Contains(text item field, StringComparison.Ordinal)))
                |> function None -> Ok() | Some field -> fail "QQ-ROOT-WITNESS" ($"%s{id}:%s{field}"))
            |> bind (fun () ->
                let budget = item["budget"].AsObject()
                let actualDepth = dependencyDepth owner
                let fields = [ "depth"; "states"; "samples"; "elapsedMs"; "peakMiB"; "artifactBytes" ]
                if fields |> List.exists (fun field -> number budget field <= 0) then fail "QQ-BUDGET" id
                elif number budget "depth" < actualDepth then fail "QQ-BUDGET-DEPTH" id
                else Ok())
            |> bind (fun () ->
                let admission = item["admission"].AsObject()
                let actualFields = admission |> Seq.map (fun property -> property.Key) |> Set.ofSeq
                let expectedWitnesses = Set [ text item "positive"; text item "adversarial"; text item "invalid" ]
                if actualFields <> requiredAdmission then fail "QQ-ROOT-ADMISSION-FIELDS" id
                elif text admission "owner" <> owner || text admission "root" <> text item "main" then fail "QQ-ROOT-ADMISSION-IDENTITY" id
                elif strings admission "imports" |> Set.ofList <> expectedClosure then fail "QQ-ROOT-ADMISSION-IMPORTS" id
                elif strings admission "witnesses" |> Set.ofList <> expectedWitnesses then fail "QQ-ROOT-ADMISSION-WITNESSES" id
                elif strings admission "bounds" |> Set.ofList <> Set [ "depth"; "states"; "samples"; "elapsedMs"; "peakMiB"; "artifactBytes" ] then fail "QQ-ROOT-ADMISSION-BOUNDS" id
                elif strings admission "invariants" |> List.exists (declaredDefinition >> not) then fail "QQ-ROOT-ADMISSION-INVARIANT" id
                elif strings admission "witnesses" |> List.exists (declaredDefinition >> not) then fail "QQ-ROOT-ADMISSION-WITNESS" id
                elif (strings admission "independentOracles" |> Set.ofList |> fun actual -> not (Set.isSubset actual requiredOracles)) then fail "QQ-ROOT-ADMISSION-ORACLE" id
                elif (strings admission "projections" |> Set.ofList |> fun actual -> not (Set.isSubset actual knownProjections)) then fail "QQ-ROOT-ADMISSION-PROJECTION" id
                elif not (Set.contains (text admission "ciImpact") allowedCiImpact) then fail "QQ-ROOT-ADMISSION-CI" id
                elif text admission "budgetEffect" <> "within-calibrated-envelope" then fail "QQ-ROOT-ADMISSION-BUDGET-EFFECT" id
                else Ok())
        let selection = document["selection"].AsObject()

        if text document "schema" <> "fsgg.coordination.quint-qualification/1" then fail "QQ-SCHEMA" "unsupported"
        elif text document "sourceSha256" <> sha256 sourceBytes then fail "QQ-SOURCE" "digest"
        else
            unique "QQ-MODULE-DUPLICATE" "modules" moduleIds
            |> bind (fun () ->
                modules
                |> List.tryPick (fun item ->
                    let id = text item "id"
                    strings item "imports"
                    |> List.tryFind (fun dependency -> not (Map.containsKey dependency imports))
                    |> Option.map (fun dependency -> id, dependency))
                |> function None -> Ok() | Some(owner, dependency) -> fail "QQ-MODULE-IMPORT" ($"%s{owner}:%s{dependency}"))
            |> bind (fun () -> modules |> List.fold (fun state item -> state |> bind (fun () -> closure Set.empty (text item "id") |> Result.map ignore)) (Ok()))
            |> bind (fun () -> classificationCheck "state" (declared "^\\s*var\\s+([A-Za-z][A-Za-z0-9_]*)") state)
            |> bind (fun () -> classificationCheck "actions" (declared "^\\s*action\\s+([A-Za-z][A-Za-z0-9_]*)") actions)
            |> bind (fun () -> unique "QQ-ROOT-DUPLICATE" "roots" rootIds)
            |> bind (fun () -> exact "QQ-ROOT-COVERAGE" "modules" (Set moduleIds - Set [ "core" ]) (Set rootModules))
            |> bind (fun () -> roots |> List.fold (fun state item -> state |> bind (fun () -> rootCheck item)) (Ok()))
            |> bind (fun () -> exact "QQ-ORACLE-INVENTORY" "oracles" requiredOracles (strings document "oracleIds" |> Set.ofList))
            |> bind (fun () ->
                if text selection "pullRequest" <> "reverse-dependency-closure" || text selection "protectedPolicy" <> "full-inventory" then
                    fail "QQ-SELECTION-POLICY" "unsupported"
                else exact "QQ-PROTECTED-MODES" "protected" (Set [ "main"; "acceptance"; "freeze"; "release" ]) (strings selection "protected" |> Set.ofList))
            |> bind (fun () -> exact "QQ-ADMISSION" "required" requiredAdmission (strings (document["admission"]) "required" |> Set.ofList))
            |> bind (fun () ->
                roots
                |> List.collect (fun root -> strings (root["admission"]) "independentOracles")
                |> Set.ofList
                |> exact "QQ-ROOT-ORACLE-ADMISSION" "oracles" requiredOracles)
    with error -> fail "QQ-MALFORMED" error.Message

let oracleTests =
    Map [ "claim-exclusion", "oracleClaimExclusion"
          "stale-projection", "oracleStaleProjection"
          "dependency-concurrency", "oracleDependencyConcurrency"
          "partial-operation", "oraclePartialOperation"
          "old-client-fencing", "oracleOldClientFencing"
          "ledger-tamper", "oracleLedgerTamper"
          "exact-head-review", "oracleExactHeadReview"
          "post-merge-verification", "oraclePostMergeVerification"
          "dual-feed-recovery", "oracleDualFeedRecovery"
          "abstraction-equivalence", "oracleAbstractionEquivalence"
          "scale-envelope", "oracleScaleEnvelope" ]

let validateOracles (sourceText: string) expectedIds =
    oracleTests
    |> Map.keys
    |> Set.ofSeq
    |> exact "QQ-ORACLE-COVERAGE" "cases" expectedIds
    |> bind (fun () ->
        oracleTests
        |> Map.toList
        |> List.tryFind (fun (_, testName) ->
            not (Regex.IsMatch(sourceText, $"(?m)^\\s*run\\s+%s{Regex.Escape testName}\\s*=")))
        |> function
            | None -> Ok()
            | Some(id, testName) -> fail "QQ-ORACLE-EXECUTABLE" ($"%s{id}:%s{testName}"))

let reverseSelection (document: JsonObject) changed =
    let modules = objects document "modules"
    let imports = modules |> List.map (fun item -> text item "id", strings item "imports" |> Set.ofList) |> Map.ofList
    let rec expand selected =
        let next =
            imports
            |> Map.toSeq
            |> Seq.choose (fun (id, dependencies) -> if Set.isEmpty (Set.intersect dependencies selected) then None else Some id)
            |> Set.ofSeq
            |> Set.union selected
        if next = selected then selected else expand next
    let affected = expand changed
    objects document "roots"
    |> List.choose (fun root -> if Set.contains (text root "module") affected then Some(text root "id") else None)
    |> Set.ofList

let rootsForOracles (document: JsonObject) oracleIds =
    let modules =
        objects document "roots"
        |> List.choose (fun root ->
        let admitted = strings (root["admission"]) "independentOracles" |> Set.ofList
        if Set.isEmpty (Set.intersect admitted oracleIds) then None else Some(text root "module"))
        |> Set.ofList
    reverseSelection document modules

let validateProposal (document: JsonObject) (proposal: JsonObject) =
    try
        let modules = objects document "modules" |> List.map (fun item -> text item "id") |> Set.ofList
        let oracleIds = strings document "oracleIds" |> Set.ofList
        let projections =
            objects document "roots"
            |> List.collect (fun root -> strings (root["admission"]) "projections")
            |> Set.ofList
        let requiredFields =
            Set [ "schema"; "owner"; "behaviorSha256"; "imports"; "invariants"; "independentOracles"; "root"; "bounds"
                  "witnesses"; "projections"; "ciImpact"; "budgetEffect" ]
        let actualFields = proposal |> Seq.map (fun property -> property.Key) |> Set.ofSeq
        let identifiers field = strings proposal field
        let namesAreValid (values: string list) =
            not (List.isEmpty values)
            && values |> List.forall (fun value -> Regex.IsMatch(value, "^[A-Za-z][A-Za-z0-9_-]*$"))
        let bounds = proposal["bounds"].AsObject()
        let boundFields = Set [ "depth"; "states"; "samples"; "elapsedMs"; "peakMiB"; "artifactBytes" ]
        if actualFields <> requiredFields then fail "QQ-PROPOSAL-FIELDS" ($"%A{actualFields}")
        elif text proposal "schema" <> "fsgg.coordination.quint-proposal/1" then fail "QQ-PROPOSAL-SCHEMA" "unsupported"
        elif not (Regex.IsMatch(text proposal "owner", "^[a-z][a-z0-9-]*$")) || Set.contains (text proposal "owner") modules then fail "QQ-PROPOSAL-OWNER" "invalid-or-existing"
        elif not (Regex.IsMatch(text proposal "behaviorSha256", "^[0-9a-f]{64}$")) then fail "QQ-PROPOSAL-BEHAVIOR" "unbound"
        elif (identifiers "imports" |> Set.ofList |> fun actual -> Set.isEmpty actual || not (Set.isSubset actual modules)) then fail "QQ-PROPOSAL-IMPORTS" "empty-or-unknown"
        elif not (namesAreValid (identifiers "invariants")) || not (namesAreValid (identifiers "witnesses")) then fail "QQ-PROPOSAL-EXECUTABLES" "invalid"
        elif (identifiers "independentOracles" |> Set.ofList |> fun actual -> Set.isEmpty actual || not (Set.isSubset actual oracleIds)) then fail "QQ-PROPOSAL-ORACLES" "unknown"
        elif not (Regex.IsMatch(text proposal "root", "^[A-Za-z][A-Za-z0-9_]*$")) then fail "QQ-PROPOSAL-ROOT" "invalid"
        elif (bounds |> Seq.map (fun property -> property.Key) |> Set.ofSeq) <> boundFields then fail "QQ-PROPOSAL-BOUNDS" "fields"
        elif boundFields |> Set.exists (fun field -> number bounds field <= 0) then fail "QQ-PROPOSAL-BOUNDS" "non-positive"
        elif number bounds "depth" <= (identifiers "imports").Length then fail "QQ-PROPOSAL-BOUNDS" "insufficient-depth"
        elif (identifiers "projections" |> Set.ofList |> fun actual -> Set.isEmpty actual || not (Set.isSubset actual projections)) then fail "QQ-PROPOSAL-PROJECTIONS" "unknown"
        elif not (Set.contains (text proposal "ciImpact") (Set [ "bounded-state-root"; "bounded-test-root" ])) then fail "QQ-PROPOSAL-CI" "unsupported"
        elif text proposal "budgetEffect" <> "within-calibrated-envelope" then fail "QQ-PROPOSAL-BUDGET" "unsupported"
        else Ok()
    with error -> fail "QQ-PROPOSAL-MALFORMED" error.Message

let validateBaseline root configBytes (document: JsonObject) =
    try
        let baselinePath = Path.Combine(root, "eng/quint-qualification-baseline.json")
        let baseline = JsonNode.Parse(File.ReadAllBytes baselinePath).AsObject()
        let sourcePath = Path.Combine(root, text document "source")
        let roots = objects document "roots" |> List.map (fun item -> text item "id", item) |> Map.ofList
        let measurements = objects baseline "measurements"
        let measuredIds = measurements |> List.map (fun item -> text item "root")
        if text baseline "schema" <> "fsgg.coordination.quint-qualification-baseline/1" then fail "QQ-BASELINE-SCHEMA" "unsupported"
        elif text baseline "sourceSha256" <> sha256 (File.ReadAllBytes sourcePath) then fail "QQ-BASELINE-SOURCE" "stale"
        elif text baseline "configurationSha256" <> sha256 configBytes then fail "QQ-BASELINE-CONFIG" "stale"
        elif Set measuredIds <> Set (Map.keys roots) then fail "QQ-BASELINE-COVERAGE" ($"%A{measuredIds}")
        else
            measurements
            |> List.tryPick (fun item ->
                let id = text item "root"
                let budget = ((roots[id])["budget"]).AsObject()
                let fields = [ "dependencyDepth"; "stateCount"; "sampleCount"; "elapsedMs"; "peakMiB"; "artifactBytes" ]
                if fields |> List.exists (fun field -> number item field < 0) || number item "sampleCount" = 0 then Some("QQ-BASELINE-METRIC", id)
                elif number item "dependencyDepth" > number budget "depth" then Some("QQ-BASELINE-DEPTH", id)
                elif number item "stateCount" > number budget "states"
                     || number item "sampleCount" > number budget "samples"
                     || number item "elapsedMs" > number budget "elapsedMs"
                     || number item "peakMiB" > number budget "peakMiB"
                     || number item "artifactBytes" > number budget "artifactBytes" then Some("QQ-BASELINE-BUDGET", id)
                else None)
            |> function None -> Ok() | Some(code, id) -> fail code id
    with error -> fail "QQ-BASELINE-MALFORMED" error.Message

let clone (node: JsonObject) = node.DeepClone().AsObject()

let runSelfTests root original =
    let firstObject (collection: string) (value: JsonObject) = ((value[collection].AsArray())[0]).AsObject()
    let firstArrayField collection (field: string) value = ((firstObject collection value)[field]).AsArray()
    let firstObjectField collection (field: string) value = ((firstObject collection value)[field]).AsObject()
    let nestedArray (owner: string) (field: string) (value: JsonObject) = (value[owner].AsObject()[field]).AsArray()
    let mutations : (string * (JsonObject -> unit)) list =
        [ "source", fun value -> value["sourceSha256"] <- JsonValue.Create(String.replicate 64 "0")
          "state", fun value -> value["state"].AsArray().RemoveAt(0)
          "action", fun value -> value["actions"].AsArray().RemoveAt(0)
          "cycle", fun value -> (firstArrayField "modules" "imports" value).Add(JsonValue.Create("qualification"))
          "closure", fun value -> (firstArrayField "roots" "closure" value).RemoveAt(0)
          "main", fun value -> (firstObject "roots" value)["main"] <- JsonValue.Create("MissingRoot")
          "witness", fun value -> (firstObject "roots" value)["invalid"] <- JsonValue.Create("missingWitness")
          "budget", fun value -> (firstObjectField "roots" "budget" value)["states"] <- JsonValue.Create(0)
          "oracle", fun value -> value["oracleIds"].AsArray().RemoveAt(0)
          "selection", fun value -> value["selection"]["pullRequest"] <- JsonValue.Create("changed-only")
          "protected", fun value -> (nestedArray "selection" "protected" value).RemoveAt(0)
          "admission", fun value -> (nestedArray "admission" "required" value).RemoveAt(0)
          "root-admission-owner", fun value -> (firstObject "roots" value).["admission"].["owner"] <- JsonValue.Create("wrong-owner")
          "root-admission-oracle", fun value -> ((firstObject "roots" value).["admission"].["independentOracles"]).AsArray().RemoveAt(0)
          "root-admission-witness", fun value -> ((firstObject "roots" value).["admission"].["witnesses"]).AsArray().RemoveAt(0)
          "root-admission-invariant", fun value -> ((firstObject "roots" value).["admission"].["invariants"])[0] <- JsonValue.Create("missingInvariant")
          "root-admission-projection", fun value -> ((firstObject "roots" value).["admission"].["projections"])[0] <- JsonValue.Create("unknown-projection")
          "root-admission-ci", fun value -> (firstObject "roots" value).["admission"].["ciImpact"] <- JsonValue.Create("unbounded")
          "root-admission-budget-effect", fun value -> (firstObject "roots" value).["admission"].["budgetEffect"] <- JsonValue.Create("unknown") ]
    for name, mutate in mutations do
        let candidate = clone original
        mutate candidate
        match validateDocument root candidate with
        | Ok _ -> failwith $"QQ-SELF-TEST: mutation %s{name} passed"
        | Error _ -> ()
    mutations.Length

let arguments = fsi.CommandLineArgs |> Array.skip 1 |> Array.filter ((<>) "--") |> Array.toList
let rec parse selfTest root config mode changed changedPaths changedSurfaces protectedMode reuseSource proposalPath planOut remaining =
    match remaining with
    | [] -> selfTest, Path.GetFullPath root, config, mode, changed, changedPaths, changedSurfaces, protectedMode, reuseSource, proposalPath, planOut
    | "--self-test" :: tail -> parse true root config mode changed changedPaths changedSurfaces protectedMode reuseSource proposalPath planOut tail
    | "--root" :: value :: tail -> parse selfTest value config mode changed changedPaths changedSurfaces protectedMode reuseSource proposalPath planOut tail
    | "--config" :: value :: tail -> parse selfTest root value mode changed changedPaths changedSurfaces protectedMode reuseSource proposalPath planOut tail
    | "--mode" :: value :: tail -> parse selfTest root config value changed changedPaths changedSurfaces protectedMode reuseSource proposalPath planOut tail
    | "--changed-module" :: value :: tail -> parse selfTest root config mode (Set.add value changed) changedPaths changedSurfaces protectedMode reuseSource proposalPath planOut tail
    | "--changed-path" :: value :: tail -> parse selfTest root config mode changed (Set.add value changedPaths) changedSurfaces protectedMode reuseSource proposalPath planOut tail
    | "--changed-surface" :: value :: tail -> parse selfTest root config mode changed changedPaths (Set.add value changedSurfaces) protectedMode reuseSource proposalPath planOut tail
    | "--protected-mode" :: value :: tail -> parse selfTest root config mode changed changedPaths changedSurfaces value reuseSource proposalPath planOut tail
    | "--reuse-source-sha256" :: value :: tail -> parse selfTest root config mode changed changedPaths changedSurfaces protectedMode (Some value) proposalPath planOut tail
    | "--proposal" :: value :: tail -> parse selfTest root config mode changed changedPaths changedSurfaces protectedMode reuseSource (Some value) planOut tail
    | "--plan-out" :: value :: tail -> parse selfTest root config mode changed changedPaths changedSurfaces protectedMode reuseSource proposalPath (Some value) tail
    | value :: _ -> eprintfn "QQ-ARGUMENT: %s" value; exit 2

let selfTest, root, config, mode, changed, changedPaths, changedSurfaces, protectedMode, reuseSource, proposalPath, planOut =
    parse false "." "eng/quint-qualification.json" "protected" Set.empty Set.empty Set.empty "main" None None None arguments

let configPath = if Path.IsPathRooted config then config else Path.Combine(root, config)
let bytes = File.ReadAllBytes configPath
let document = JsonNode.Parse(bytes).AsObject()

match validateDocument root document with
| Error error -> eprintfn "%s" error; exit 1
| Ok () ->
    let ids = strings document "oracleIds" |> Set.ofList
    let sourceText = File.ReadAllText(Path.Combine(root, text document "source"))
    match validateOracles sourceText ids |> bind (fun () -> validateBaseline root bytes document) with
    | Error error -> eprintfn "%s" error; exit 1
    | Ok () ->
        let authoritySelection = reverseSelection document (Set [ "authority" ])
        if authoritySelection <> Set [ "authority"; "lifecycle"; "qualification" ] then
            eprintfn "QQ-SELECTION-CLOSURE: %A" authoritySelection
            exit 1
        let allRoots = objects document "roots" |> List.map (fun item -> text item "id") |> Set.ofList
        let moduleIds = objects document "modules" |> List.map (fun item -> text item "id") |> Set.ofList
        let rootsById = objects document "roots" |> List.map (fun item -> text item "id", item) |> Map.ofList
        let rootsFromPaths =
            changedPaths
            |> Set.fold (fun selected path ->
                let normalized = path.Replace('\\', '/')
                if normalized = text document "source"
                   || normalized = config.Replace('\\', '/')
                   || normalized.EndsWith("quint-qualification-baseline.json", StringComparison.Ordinal)
                   || normalized.Contains("validate-quint-qualification", StringComparison.Ordinal)
                   || normalized.Contains("validate-canonical-quint-protocol", StringComparison.Ordinal)
                   || normalized.Contains("qualify-canonical-quint", StringComparison.Ordinal) then Set.union selected allRoots
                else
                    let matched = moduleIds |> Set.filter (fun moduleId -> normalized.Contains(moduleId, StringComparison.Ordinal))
                    if Set.isEmpty matched then eprintfn "QQ-SELECTION-PATH: %s" path; exit 1
                    Set.union selected (reverseSelection document matched)) Set.empty
        let rootsFromSurfaces =
            changedSurfaces
            |> Set.fold (fun selected surface ->
                match surface.Split(':', 2) with
                | [| kind |] when kind = "backend" || kind = "toolchain" -> Set.union selected allRoots
                | [| "oracle"; oracleId |] when strings document "oracleIds" |> List.contains oracleId ->
                    Set.union selected (rootsForOracles document (Set [ oracleId ]))
                | [| kind; owner |] when kind = "bound" || kind = "budget" ->
                    let ownerModule =
                        if Set.contains owner moduleIds then owner
                        elif Map.containsKey owner rootsById then text rootsById[owner] "module"
                        else eprintfn "QQ-SELECTION-SURFACE: %s" surface; exit 1
                    Set.union selected (reverseSelection document (Set [ ownerModule ]))
                | _ -> eprintfn "QQ-SELECTION-SURFACE: %s" surface; exit 1) Set.empty
        let changedSelection =
            Set.unionMany [ reverseSelection document changed; rootsFromPaths; rootsFromSurfaces ]
        let proposal =
            proposalPath
            |> Option.map (fun path ->
                let absolute = if Path.IsPathRooted path then path else Path.Combine(root, path)
                let proposal = JsonNode.Parse(File.ReadAllBytes absolute).AsObject()
                match validateProposal document proposal with
                | Ok () -> proposal
                | Error error -> eprintfn "%s" error; exit 1)
        let protectedModes = strings (document["selection"]) "protected" |> Set.ofList
        let selected =
            match mode with
            | "protected" when Set.contains protectedMode protectedModes -> allRoots
            | "pull-request" when not (Set.isEmpty changedSelection) -> changedSelection
            | "reuse" when reuseSource = Some(text document "sourceSha256") && not (Set.isEmpty changedSelection) -> changedSelection
            | "future-behavior" when Option.isSome proposal -> allRoots
            | _ -> eprintfn "QQ-SELECTION-INPUT: mode=%s changed=%A paths=%A surfaces=%A protected=%s" mode changed changedPaths changedSurfaces protectedMode; exit 1
        if Set.isEmpty selected && mode <> "reuse" then
            eprintfn "QQ-SELECTION-EMPTY: mode=%s" mode
            exit 1
        match planOut with
        | Some path ->
            let output = JsonObject()
            output["schema"] <- JsonValue.Create("fsgg.coordination.quint-selection/1")
            output["mode"] <- JsonValue.Create(mode)
            output["sourceSha256"] <- JsonValue.Create(text document "sourceSha256")
            output["changedModules"] <- JsonArray(changed |> Set.toArray |> Array.map JsonValue.Create |> Array.map (fun value -> value :> JsonNode))
            output["changedPaths"] <- JsonArray(changedPaths |> Set.toArray |> Array.map JsonValue.Create |> Array.map (fun value -> value :> JsonNode))
            output["changedSurfaces"] <- JsonArray(changedSurfaces |> Set.toArray |> Array.map JsonValue.Create |> Array.map (fun value -> value :> JsonNode))
            reuseSource |> Option.iter (fun value -> output["reuseSourceSha256"] <- JsonValue.Create(value))
            proposal |> Option.iter (fun value -> output["proposalSha256"] <- JsonValue.Create(sha256 (Encoding.UTF8.GetBytes(value.ToJsonString()))))
            output["roots"] <- JsonArray(selected |> Set.toArray |> Array.map JsonValue.Create |> Array.map (fun value -> value :> JsonNode))
            File.WriteAllText(Path.GetFullPath path, output.ToJsonString(JsonSerializerOptions(WriteIndented = true)))
        | None -> ()
        let mutationCount = if selfTest then runSelfTests root document else 0
        printfn "QUINT_QUALIFICATION_OK config=%s roots=%d selected=%s oracles=%d negativeControls=%d sha256=%s" config (objects document "roots").Length (String.concat "," selected) oracleTests.Count mutationCount (sha256 bytes)

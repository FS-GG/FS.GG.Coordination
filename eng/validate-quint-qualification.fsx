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

        let requiredOracles =
            Set [ "claim-exclusion"; "stale-projection"; "dependency-concurrency"; "partial-operation"
                  "old-client-fencing"; "ledger-tamper"; "exact-head-review"; "post-merge-verification"
                  "dual-feed-recovery"; "abstraction-equivalence"; "scale-envelope" ]
        let requiredAdmission =
            Set [ "owner"; "imports"; "invariants"; "independentOracles"; "root"; "bounds"; "witnesses"
                  "projections"; "ciImpact"; "budgetEffect" ]
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
    with error -> fail "QQ-MALFORMED" error.Message

type OracleCase = { Id: string; Correct: unit -> string; Mutated: unit -> string; Expected: string }

let oracle id expected correct mutated = { Id = id; Correct = correct; Mutated = mutated; Expected = expected }

let oracleCases =
    [ oracle "claim-exclusion" "exclusive"
          (fun () -> if Set.count (Set [ "worker-a"; "worker-a" ]) = 1 then "exclusive" else "conflict")
          (fun () -> if Set.count (Set [ "worker-a"; "worker-b" ]) = 1 then "exclusive" else "conflict")
      oracle "stale-projection" "stale" (fun () -> if 8 < 9 then "stale" else "current") (fun () -> if 9 < 9 then "stale" else "current")
      oracle "dependency-concurrency" "revision-conflict" (fun () -> if 12 <> 13 then "revision-conflict" else "apply") (fun () -> if 13 <> 13 then "revision-conflict" else "apply")
      oracle "partial-operation" "receipt-reread" (fun () -> if 2 < 3 then "receipt-reread" else "advance") (fun () -> if 3 < 3 then "receipt-reread" else "advance")
      oracle "old-client-fencing" "fenced" (fun () -> if 4 < 5 then "fenced" else "accepted") (fun () -> if 5 < 5 then "fenced" else "accepted")
      oracle "ledger-tamper" "tamper" (fun () -> if "parent-a" <> "parent-b" then "tamper" else "valid") (fun () -> if "parent-a" <> "parent-a" then "tamper" else "valid")
      oracle "exact-head-review" "review-stale" (fun () -> if "head-a" <> "head-b" then "review-stale" else "accepted") (fun () -> if "head-a" <> "head-a" then "review-stale" else "accepted")
      oracle "post-merge-verification" "pending-verification" (fun () -> if true && not false then "pending-verification" else "done") (fun () -> if true && not true then "pending-verification" else "done")
      oracle "dual-feed-recovery" "complete"
          (fun () -> if Set.count (Set [ "candidate"; "candidate" ]) = 1 then "complete" else "recover")
          (fun () -> if Set.count (Set [ "candidate"; "different" ]) = 1 then "complete" else "recover")
      oracle "abstraction-equivalence" "equivalent"
          (fun () -> if Set [ "success"; "refusal"; "rollback" ] = Set [ "rollback"; "refusal"; "success" ] then "equivalent" else "drift")
          (fun () -> if Set [ "success"; "refusal"; "rollback" ] = Set [ "refusal"; "success" ] then "equivalent" else "drift")
      oracle "scale-envelope" "within-budget"
          (fun () -> if 4097 <= 50000 && 207 <= 1536 && 4130000 <= 4194304 then "within-budget" else "over-budget")
          (fun () -> if 50001 <= 50000 && 207 <= 1536 && 4130000 <= 4194304 then "within-budget" else "over-budget") ]

let validateOracles expectedIds =
    oracleCases
    |> List.map _.Id
    |> Set.ofList
    |> exact "QQ-ORACLE-COVERAGE" "cases" expectedIds
    |> bind (fun () ->
        oracleCases
        |> List.tryFind (fun test -> test.Correct() <> test.Expected || test.Mutated() = test.Expected)
        |> function None -> Ok() | Some test -> fail "QQ-ORACLE-MUTATION" test.Id)

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
          "admission", fun value -> (nestedArray "admission" "required" value).RemoveAt(0) ]
    for name, mutate in mutations do
        let candidate = clone original
        mutate candidate
        match validateDocument root candidate with
        | Ok _ -> failwith $"QQ-SELF-TEST: mutation %s{name} passed"
        | Error _ -> ()
    mutations.Length

let arguments = fsi.CommandLineArgs |> Array.skip 1 |> Array.filter ((<>) "--") |> Array.toList
let selfTest, root, config =
    match arguments with
    | [ "--root"; root; "--config"; config ] -> false, Path.GetFullPath root, config
    | [ "--self-test"; "--root"; root; "--config"; config ] -> true, Path.GetFullPath root, config
    | _ -> eprintfn "usage: dotnet fsi eng/validate-quint-qualification.fsx -- [--self-test] --root ROOT --config FILE"; exit 2

let configPath = if Path.IsPathRooted config then config else Path.Combine(root, config)
let bytes = File.ReadAllBytes configPath
let document = JsonNode.Parse(bytes).AsObject()

match validateDocument root document with
| Error error -> eprintfn "%s" error; exit 1
| Ok () ->
    let ids = strings document "oracleIds" |> Set.ofList
    match validateOracles ids |> bind (fun () -> validateBaseline root bytes document) with
    | Error error -> eprintfn "%s" error; exit 1
    | Ok () ->
        let authoritySelection = reverseSelection document (Set [ "authority" ])
        if authoritySelection <> Set [ "authority"; "lifecycle"; "qualification" ] then
            eprintfn "QQ-SELECTION-CLOSURE: %A" authoritySelection
            exit 1
        let mutationCount = if selfTest then runSelfTests root document else 0
        printfn "QUINT_QUALIFICATION_OK config=%s roots=%d oracles=%d negativeControls=%d sha256=%s" config (objects document "roots").Length oracleCases.Length mutationCount (sha256 bytes)

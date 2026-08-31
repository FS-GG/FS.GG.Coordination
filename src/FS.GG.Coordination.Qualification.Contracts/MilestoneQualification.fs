module FS.GG.Coordination.Qualification.Contracts.MilestoneQualification

open System
open System.IO
open System.Security.Cryptography
open System.Text
open System.Text.Json

type Mode = Scoped | Comprehensive

type AcceptanceBinding =
    { ReceiptPath: string
      ReceiptDigest: string }

type ChildBinding =
    { Id: string
      ContractSha256: string
      Acceptance: AcceptanceBinding option }

type State =
    { PolicyVersion: string
      Parent: string
      Mode: Mode
      BoundaryKind: string option
      ExpectedChildren: string list
      Children: ChildBinding list }

type Validation =
    { State: State
      AcceptedPrefixLength: int
      ContractDrift: string list
      SubjectSha256: string }

let private sha256 (bytes: byte array) = SHA256.HashData bytes |> Convert.ToHexString |> _.ToLowerInvariant()
let private isDigest (value: string) =
    not (String.IsNullOrWhiteSpace value) && value.Length = 64
    && value |> Seq.forall (fun c -> Char.IsDigit c || (c >= 'a' && c <= 'f'))
let private isHead (value: string) =
    not (String.IsNullOrWhiteSpace value) && value.Length = 40 && value |> Seq.forall Uri.IsHexDigit
let private safePath (value: string) =
    not (String.IsNullOrWhiteSpace value) && not (Path.IsPathRooted value)
    && not (value.Contains '\\') && value.Split('/') |> Array.forall (fun part -> part <> ".." && part <> "")
let private stringProperty (name: string) (value: JsonElement) =
    let mutable property = Unchecked.defaultof<JsonElement>
    if value.TryGetProperty(name, &property) && property.ValueKind = JsonValueKind.String then property.GetString()
    else null

let parse (bytes: byte array) =
    try
        use document = JsonDocument.Parse bytes
        let root = document.RootElement
        if stringProperty "schema" root <> "fsgg.coordination.milestone-qualification/1" then
            Error "milestone qualification schema is unsupported"
        else
            let policy = stringProperty "policyVersion" root
            let parent = stringProperty "parent" root
            let mode =
                match stringProperty "mode" root with
                | "scoped" -> Scoped
                | "comprehensive" -> Comprehensive
                | _ -> failwith "milestone mode must be scoped or comprehensive"
            let boundary =
                match stringProperty "boundaryKind" root with
                | null | "" -> None
                | value -> Some value
            let expectedChildren =
                root.GetProperty("expectedChildren").EnumerateArray()
                |> Seq.map (fun value -> if value.ValueKind <> JsonValueKind.String then failwith "expected child ids must be strings" else value.GetString())
                |> Seq.toList
            let children =
                root.GetProperty("children").EnumerateArray()
                |> Seq.map (fun child ->
                    let acceptance =
                        let mutable value = Unchecked.defaultof<JsonElement>
                        if child.TryGetProperty("acceptance", &value) && value.ValueKind = JsonValueKind.Object then
                            Some { ReceiptPath = stringProperty "receiptPath" value; ReceiptDigest = stringProperty "receiptDigest" value }
                        elif value.ValueKind = JsonValueKind.Null || value.ValueKind = JsonValueKind.Undefined then None
                        else failwith "child acceptance must be an object or null"
                    { Id = stringProperty "id" child
                      ContractSha256 = stringProperty "contractSha256" child
                      Acceptance = acceptance })
                |> Seq.toList
            if String.IsNullOrWhiteSpace policy || String.IsNullOrWhiteSpace parent then failwith "policyVersion and parent are required"
            if List.isEmpty expectedChildren then failwith "expected milestone children must not be empty"
            if expectedChildren |> List.exists String.IsNullOrWhiteSpace then failwith "expected child ids must not be empty"
            if expectedChildren.Length <> (expectedChildren |> List.distinct |> List.length) then failwith "expected child ids must be distinct"
            if children.Length > expectedChildren.Length || (children |> List.map _.Id) <> (expectedChildren |> List.take children.Length) then
                failwith "registered milestone children must be an ordered prefix of expectedChildren"
            if children |> List.exists (fun child -> String.IsNullOrWhiteSpace child.Id || not (isDigest child.ContractSha256)) then
                failwith "every milestone child requires an id and lowercase contract digest"
            if (children |> List.map _.Id |> List.distinct |> List.length) <> children.Length then failwith "milestone child ids must be distinct"
            for child in children do
                match child.Acceptance with
                | Some acceptance when not (safePath acceptance.ReceiptPath) || not (isDigest acceptance.ReceiptDigest) ->
                    failwith $"invalid acceptance binding for {child.Id}"
                | _ -> ()
            if mode = Comprehensive && boundary.IsNone then failwith "comprehensive mode requires boundaryKind"
            Ok { PolicyVersion = policy; Parent = parent; Mode = mode; BoundaryKind = boundary; ExpectedChildren = expectedChildren; Children = children }
    with exceptionValue -> Error exceptionValue.Message

let private receiptFacts (bytes: byte array) =
    use document = JsonDocument.Parse bytes
    let root = document.RootElement
    if stringProperty "schema" root <> "fsgg.coordination.unit-acceptance/1" then failwith "unit acceptance schema is unsupported"
    let id = stringProperty "unitId" root
    let state = stringProperty "state" root
    let contract = stringProperty "unitContractSha256" root
    let digest = stringProperty "digest" root
    if state <> "accepted" || String.IsNullOrWhiteSpace id || not (isDigest contract) || not (isDigest digest) then
        failwith "unit acceptance receipt is incomplete"
    id, contract, digest

let private payloadBytes (state: State) =
    use stream = new MemoryStream()
    use writer = new Utf8JsonWriter(stream)
    writer.WriteStartObject()
    writer.WriteString("schema", "fsgg.coordination.milestone-subject/1")
    writer.WriteString("policyVersion", state.PolicyVersion)
    writer.WriteString("parent", state.Parent)
    writer.WriteString("mode", if state.Mode = Scoped then "scoped" else "comprehensive")
    match state.BoundaryKind with Some value -> writer.WriteString("boundaryKind", value) | None -> writer.WriteNull("boundaryKind")
    writer.WriteStartArray("expectedChildren")
    for id in state.ExpectedChildren do writer.WriteStringValue id
    writer.WriteEndArray()
    writer.WriteStartArray("children")
    for child in state.Children do
        writer.WriteStartObject()
        writer.WriteString("id", child.Id)
        writer.WriteString("contractSha256", child.ContractSha256)
        match child.Acceptance with
        | None -> writer.WriteNull("acceptance")
        | Some acceptance ->
            writer.WriteStartObject("acceptance")
            writer.WriteString("receiptPath", acceptance.ReceiptPath)
            writer.WriteString("receiptDigest", acceptance.ReceiptDigest)
            writer.WriteEndObject()
        writer.WriteEndObject()
    writer.WriteEndArray()
    writer.WriteEndObject()
    writer.Flush()
    stream.ToArray()

let validate state receiptBytes =
    try
        let mutable sawGap = false
        let mutable accepted = 0
        let drift = ResizeArray<string>()
        for child in state.Children do
            match child.Acceptance with
            | None -> sawGap <- true
            | Some binding ->
                if sawGap then failwith "accepted children must form an ordered prefix"
                let bytes = receiptBytes |> Map.tryFind binding.ReceiptPath |> Option.defaultWith (fun () -> failwith $"missing acceptance receipt: {binding.ReceiptPath}")
                let id, contract, digest = receiptFacts bytes
                if id <> child.Id || digest <> binding.ReceiptDigest then failwith $"acceptance receipt binding differs for {child.Id}"
                if contract <> child.ContractSha256 then drift.Add child.Id
                accepted <- accepted + 1
        if state.Mode = Comprehensive then
            if state.Children.Length <> state.ExpectedChildren.Length then failwith "comprehensive closure requires every expected child registered"
            if accepted <> state.ExpectedChildren.Length then failwith "comprehensive closure requires every child accepted"
            if drift.Count > 0 then
                let drifted = String.concat "," drift
                failwith $"comprehensive closure has contract-drifted children: {drifted}"
        Ok { State = state; AcceptedPrefixLength = accepted; ContractDrift = List.ofSeq drift; SubjectSha256 = payloadBytes state |> sha256 }
    with exceptionValue -> Error exceptionValue.Message

let closureSubject state exactHead treeSha256 =
    if state.Mode <> Comprehensive then invalidArg (nameof state) "closure subject requires comprehensive mode"
    if not (isHead exactHead) then invalidArg (nameof exactHead) "exact head must be 40 hex"
    if not (isDigest treeSha256) then invalidArg (nameof treeSha256) "tree digest must be lowercase SHA-256"
    Array.concat [ payloadBytes state; Encoding.UTF8.GetBytes exactHead; Encoding.UTF8.GetBytes treeSha256 ] |> sha256

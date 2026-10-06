namespace FS.GG.Coordination.Cli

open System
open System.Collections.Generic
open System.Globalization
open System.Security.Cryptography
open System.Text
open System.Text.Json

module internal PortableWorkspaceC2Phase =
    type Diagnostic = { Field: string; Code: string }
    type Request = private Request of JsonElement * byte array
    type ResultDocument = private ResultDocument of JsonElement * byte array
    type BoundDeclaration = private BoundDeclaration of string

    /// Immutable description of caller-declared identity, never captured authority.
    type RequestIdentity =
        { EnrollmentId: string
          ProfileId: string
          ProfileSha256: string
          OperationId: string
          IdempotencyId: string
          RequestSha256: string }

    type InputDeclaration =
        { Id: string
          Role: string
          HostPath: string
          RelativePath: string
          Bytes: int64
          Sha256: string }

    type RolePath = { Role: string; HostPath: string; ContainerPath: string }
    type Translation =
        { ArgumentIndex: int; Role: string; Prefix: string; RelativePath: string }
    type StepDeclaration =
        { StepId: string
          Executable: string
          DeclaredArguments: string list
          ContainerArguments: string list
          WorkingRole: string
          Translations: Translation list
          Environment: Map<string, string> }

    /// The original phase window is supplied once, including caller setup.
    type PhaseWindow =
        { PhaseStartedUtc: string
          NotAfterUtc: string
          MaximumPhaseMs: int64
          MaximumWorkMs: int64
          CleanupReserveMs: int64
          CallerFinishReserveMs: int64 }

    /// Producer-local fixed selection data. Construction confers no authority.
    /// Only the two C2 fixture profiles and their fixed ordered steps are supported.
    /// Validated caller input/window declarations, never physical observations.
    type RequestReadDeclaration =
        { ConsumerRepository: string
          ConsumerSource: string
          Inputs: InputDeclaration list
          InputInventorySha256: string
          OwnedRoots: (string * string) list
          Window: PhaseWindow }

    type DeclaredSelection =
        { EnrollmentId: string
          ProfileId: string
          ProfileSha256: string
          ConsumerRepository: string
          ConsumerSource: string
          Inputs: InputDeclaration list
          RolePaths: RolePath list
          Steps: StepDeclaration list
          Window: PhaseWindow
          MaximumCapturedBytes: int64
          MaximumHandoffBytes: int64
          MaximumHandoffEntries: int }

    let private utf8 = UTF8Encoding(false, true)
    let private invariant = CultureInfo.InvariantCulture
    let private roles =
        [ "archive"; "catalog"; "policy"; "compiler-source"; "restore-state"; "baseline"
          "home"; "cli-home"; "hive"; "packages"; "stage"; "compiler-work"; "handoff" ]
    let private environmentKeys =
        [ "DOTNET_CLI_HOME"; "DOTNET_CLI_TELEMETRY_OPTOUT"; "DOTNET_MULTILEVEL_LOOKUP"
          "DOTNET_SKIP_FIRST_TIME_EXPERIENCE"; "HOME"; "NUGET_PACKAGES" ]
    let private get (node: JsonElement) key =
        if node.ValueKind = JsonValueKind.Object then
            match node.TryGetProperty(key: string) with
            | true, value -> value
            | _ -> Unchecked.defaultof<JsonElement>
        else Unchecked.defaultof<JsonElement>
    let private str (node: JsonElement) =
        if node.ValueKind = JsonValueKind.String then node.GetString() else ""
    let private num (node: JsonElement) =
        if node.ValueKind <> JsonValueKind.Number then 0L
        else match node.TryGetInt64() with true, value -> value | _ -> 0L
    let private items (node: JsonElement) =
        if node.ValueKind = JsonValueKind.Array then node.EnumerateArray() |> Seq.toList else []
    let private strings node = items node |> List.map str
    let private field node key = get node key |> str
    let private value node key = get node key |> num
    let private boolValue node key = (get node key).ValueKind = JsonValueKind.True

    // Each validation owns bounded mutable diagnostics; no state survives a call.
    let private collector () =
        let errors = ResizeArray<Diagnostic>()
        let add (path: string) (code: string) =
            if errors.Count < 31 then
                errors.Add { Field = (if path.Length <= 256 then path else path.Substring(0, 256)); Code = code }
            elif errors.Count = 31 then errors.Add { Field = "$"; Code = "diagnostic-limit" }
        add, (fun () -> List.ofSeq errors)
    let private objectFields add path expected (node: JsonElement) =
        if node.ValueKind <> JsonValueKind.Object then add path "object-required"
        else
            let actual = node.EnumerateObject() |> Seq.map _.Name |> Set.ofSeq
            if actual <> Set.ofList expected then add path "closed-fields-required"
    let private scalarLength (s: string) =
        let mutable length = 0
        for c in s do if not (Char.IsLowSurrogate c) then length <- length + 1
        length
    let private stringValue add path low high (node: JsonElement) =
        if node.ValueKind <> JsonValueKind.String then add path "string-required"
        else
            let s = str node
            let length = scalarLength s
            if length < low || length > high then add path "string-bound"
    let private integer add path low high (node: JsonElement) =
        if node.ValueKind <> JsonValueKind.Number then add path "integer-required"
        else
            match node.TryGetInt64() with
            | true, n when n >= low && n <= high -> ()
            | _ -> add path "integer-bound"
    let private oneOf add path allowed (node: JsonElement) =
        if node.ValueKind <> JsonValueKind.String || not (List.contains (str node) allowed) then add path "value-refused"
    let private constant add path expected node = oneOf add path [ expected ] node
    let private arrayValue add path low high (node: JsonElement) =
        if node.ValueKind <> JsonValueKind.Array then add path "array-required"
        elif node.GetArrayLength() < low || node.GetArrayLength() > high then add path "array-bound"
    let private boolean add path (node: JsonElement) =
        if node.ValueKind <> JsonValueKind.True && node.ValueKind <> JsonValueKind.False then add path "boolean-required"
    let private hex length (s: string) =
        not (isNull s) && s.Length = length && (s |> Seq.forall (fun c -> (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f')))
    let private hashValue add path length (node: JsonElement) =
        if node.ValueKind <> JsonValueKind.String || not (hex length (str node)) then add path "digest-required"
    let private token (s: string) =
        not (isNull s) && s.Length >= 1 && s.Length <= 128
        && (s |> Seq.forall (fun c -> Char.IsAsciiLetterOrDigit c || "._:/-".Contains c))
    let private tokenValue add path (node: JsonElement) =
        if node.ValueKind <> JsonValueKind.String || not (token (str node)) then add path "token-required"
    let private relative (allowEmpty: bool) (s: string) =
        not (isNull s) && ((allowEmpty && s = "") ||
            (s <> "" && not (s.StartsWith("/", StringComparison.Ordinal)) && not (s.Contains '\\')
             && not (s.Contains '\u0000') && (s.Split('/') |> Array.forall (fun part -> part <> "" && part <> "." && part <> ".."))))
    let private absolute (s: string) =
        not (isNull s) && s.StartsWith("/", StringComparison.Ordinal)
        && (s = "/" || relative false (s.Substring 1))
    let private unique add path values =
        if List.length values <> (values |> Set.ofList |> Set.count) then add path "duplicate-identity"
    let private timestamp (s: string) =
        match DateTimeOffset.TryParseExact(s, [| "yyyy-MM-dd'T'HH:mm:ss'Z'"; "yyyy-MM-dd'T'HH:mm:ss.FFFFFFF'Z'" |], invariant,
                                          DateTimeStyles.AssumeUniversal ||| DateTimeStyles.AdjustToUniversal) with
        | true, instant -> Some instant
        | _ -> None

    // The wire contract deliberately uses literal Unicode. A general-purpose JSON
    // encoder may escape additional characters, so canonical quoting is explicit.
    let private canonical (root: JsonElement) =
        let output = StringBuilder()
        let quote (s: string) =
            output.Append('"') |> ignore
            for c in s do
                match c with
                | '"' -> output.Append("\\\"") |> ignore
                | '\\' -> output.Append("\\\\") |> ignore
                | '\b' -> output.Append("\\b") |> ignore
                | '\f' -> output.Append("\\f") |> ignore
                | '\n' -> output.Append("\\n") |> ignore
                | '\r' -> output.Append("\\r") |> ignore
                | '\t' -> output.Append("\\t") |> ignore
                | c when int c < 32 -> output.Append("\\u").Append((int c).ToString("x4", invariant)) |> ignore
                | c -> output.Append(c) |> ignore
            output.Append('"') |> ignore
        let rec write (node: JsonElement) =
            match node.ValueKind with
            | JsonValueKind.Object ->
                output.Append('{') |> ignore
                let names = HashSet<string>(StringComparer.Ordinal)
                let properties = node.EnumerateObject() |> Seq.toArray
                for p in properties do
                    if not (names.Add p.Name) then raise (FormatException "duplicate-property")
                Array.sortInPlaceWith (fun (a: JsonProperty) (b: JsonProperty) -> StringComparer.Ordinal.Compare(a.Name, b.Name)) properties
                for i in 0 .. properties.Length - 1 do
                    if i > 0 then output.Append(',') |> ignore
                    quote (properties[i].Name)
                    output.Append(':') |> ignore
                    write (properties[i].Value)
                output.Append('}') |> ignore
            | JsonValueKind.Array ->
                output.Append('[') |> ignore
                for i in 0 .. node.GetArrayLength() - 1 do
                    if i > 0 then output.Append(',') |> ignore
                    write (node[i])
                output.Append(']') |> ignore
            | JsonValueKind.String -> quote (node.GetString())
            | JsonValueKind.Number ->
                match node.TryGetInt64() with
                | true, n -> output.Append(n.ToString invariant) |> ignore
                | _ -> raise (FormatException "integer-required")
            | JsonValueKind.True -> output.Append("true") |> ignore
            | JsonValueKind.False -> output.Append("false") |> ignore
            | JsonValueKind.Null -> raise (FormatException "null-refused")
            | _ -> raise (FormatException "value-refused")
        write root
        output.Append('\n') |> ignore
        utf8.GetBytes(output.ToString())

    let private decode maximum validate (raw: byte array) =
        if isNull raw || raw.Length > maximum then Error [ { Field = "$"; Code = "document-byte-limit" } ]
        else
            try
                let text = utf8.GetString raw
                use document = JsonDocument.Parse(text, JsonDocumentOptions(MaxDepth = 32))
                let root = document.RootElement
                let encoded = canonical root
                if encoded <> raw then Error [ { Field = "$"; Code = "noncanonical-json" } ]
                else
                    let add, errors = collector ()
                    validate add root
                    match errors () with
                    | [] -> Ok (root.Clone(), encoded)
                    | diagnostics -> Error diagnostics
            with
            | :? FormatException as error ->
                let code =
                    match error.Message with
                    | "duplicate-property" | "integer-required" | "null-refused" | "value-refused" -> error.Message
                    | _ -> "malformed-document"
                Error [ { Field = "$"; Code = code } ]
            | :? DecoderFallbackException -> Error [ { Field = "$"; Code = "invalid-utf8" } ]
            | :? EncoderFallbackException -> Error [ { Field = "$"; Code = "invalid-unicode" } ]
            | :? JsonException | :? InvalidOperationException | :? ArgumentException ->
                Error [ { Field = "$"; Code = "malformed-document" } ]

    let private validateWindow add (budget: JsonElement) =
        objectFields add "$.budget"
            [ "phaseStartedUtc"; "notAfterUtc"; "maximumPhaseMs"; "maximumWorkMs"
              "cleanupReserveMs"; "callerFinishReserveMs"; "maximumCapturedBytes"
              "maximumHandoffBytes"; "maximumHandoffEntries" ] budget
        for key, maximum in
            [ "maximumPhaseMs", 60000L; "maximumWorkMs", 40000L; "cleanupReserveMs", 15000L
              "callerFinishReserveMs", 5000L; "maximumCapturedBytes", 1048576L
              "maximumHandoffBytes", 16777216L; "maximumHandoffEntries", 1024L ] do
            integer add ("$.budget." + key) 1L maximum (get budget key)
        for key in [ "phaseStartedUtc"; "notAfterUtc" ] do
            stringValue add ("$.budget." + key) 20 40 (get budget key)
        if budget.ValueKind = JsonValueKind.Object then
            match timestamp (field budget "phaseStartedUtc"), timestamp (field budget "notAfterUtc") with
            | Some start, Some finish when finish > start ->
                if (finish - start).TotalMilliseconds <> float (value budget "maximumPhaseMs") then
                    add "$.budget" "original-window-mismatch"
            | _ -> add "$.budget" "original-window-required"
            if value budget "maximumWorkMs" + value budget "cleanupReserveMs" + value budget "callerFinishReserveMs"
               > value budget "maximumPhaseMs" then add "$.budget" "phase-reserve-overflow"

    let private validateRequest add root =
        objectFields add "$"
            [ "schema"; "operationId"; "idempotencyId"; "consumer"; "enrollmentId"; "profileId"
              "profileSha256"; "inputs"; "ownedRoots"; "steps"; "budget" ] root
        constant add "$.schema" "fsgg.portable-workspace-c2-phase-request/1" (get root "schema")
        match Guid.TryParseExact(field root "operationId", "D") with
        | true, id when id.ToString("D") = field root "operationId" -> ()
        | _ -> add "$.operationId" "uuid-required"
        for key in [ "idempotencyId"; "enrollmentId" ] do tokenValue add ("$." + key) (get root key)
        oneOf add "$.profileId" [ "c2-dotnet-scaffold-fixture/1"; "c2-dotnet-compiler-fixture/1" ] (get root "profileId")
        hashValue add "$.profileSha256" 64 (get root "profileSha256")
        let consumer = get root "consumer"
        objectFields add "$.consumer" [ "repository"; "source" ] consumer
        oneOf add "$.consumer.repository" [ "FS-GG/FS.GG.SDD"; "FS-GG/FS.GG.Governance" ] (get consumer "repository")
        hashValue add "$.consumer.source" 40 (get consumer "source")
        let inputs = get root "inputs"
        arrayValue add "$.inputs" 0 32 inputs
        for i, row in items inputs |> List.indexed do
            let path = sprintf "$.inputs[%d]" i
            objectFields add path [ "id"; "role"; "hostPath"; "relativePath"; "bytes"; "sha256" ] row
            tokenValue add (path + ".id") (get row "id")
            oneOf add (path + ".role") roles (get row "role")
            stringValue add (path + ".hostPath") 1 4096 (get row "hostPath")
            stringValue add (path + ".relativePath") 1 4096 (get row "relativePath")
            if not (absolute (field row "hostPath")) || not (relative false (field row "relativePath")) then add path "path-refused"
            integer add (path + ".bytes") 0L 16777216L (get row "bytes")
            hashValue add (path + ".sha256") 64 (get row "sha256")
        unique add "$.inputs.id" (items inputs |> List.map (fun row -> field row "id"))
        unique add "$.inputs.path" (items inputs |> List.map (fun row -> field row "hostPath"))
        unique add "$.inputs.role-relative" (items inputs |> List.map (fun row -> field row "role", field row "relativePath"))
        let roots = get root "ownedRoots"
        arrayValue add "$.ownedRoots" 0 8 roots
        for row in items roots do
            objectFields add "$.ownedRoots[]" [ "role"; "hostPath" ] row
            oneOf add "$.ownedRoots[].role" roles (get row "role")
            stringValue add "$.ownedRoots[].hostPath" 1 4096 (get row "hostPath")
            if not (absolute (field row "hostPath")) then add "$.ownedRoots[]" "path-refused"
        unique add "$.ownedRoots.role" (items roots |> List.map (fun row -> field row "role"))
        unique add "$.ownedRoots.path" (items roots |> List.map (fun row -> field row "hostPath"))
        let steps = get root "steps"
        arrayValue add "$.steps" 1 2 steps
        for i, step in items steps |> List.indexed do
            let path = sprintf "$.steps[%d]" i
            objectFields add path
                [ "stepId"; "executable"; "declaredArguments"; "containerArguments"; "workingRole"; "translations"; "environment" ] step
            oneOf add (path + ".stepId") [ "template-install"; "template-create"; "compiler-build" ] (get step "stepId")
            constant add (path + ".executable") "/usr/share/dotnet/dotnet" (get step "executable")
            oneOf add (path + ".workingRole") [ "home"; "compiler-work" ] (get step "workingRole")
            for key in [ "declaredArguments"; "containerArguments" ] do
                arrayValue add (path + "." + key) 0 64 (get step key)
                for argument in items (get step key) do stringValue add (path + "." + key + "[]") 0 4096 argument
            let translations = get step "translations"
            arrayValue add (path + ".translations") 0 8 translations
            for row in items translations do
                objectFields add (path + ".translations[]") [ "argumentIndex"; "role"; "prefix"; "relativePath" ] row
                integer add (path + ".translations[].argumentIndex") 0L 63L (get row "argumentIndex")
                oneOf add (path + ".translations[].role") roles (get row "role")
                oneOf add (path + ".translations[].prefix") [ ""; "-p:OtherFlags=--sig:" ] (get row "prefix")
                stringValue add (path + ".translations[].relativePath") 0 1024 (get row "relativePath")
                if not (relative true (field row "relativePath")) then add path "path-refused"
            unique add (path + ".translations") (items translations |> List.map (fun row -> value row "argumentIndex"))
            let environment = get step "environment"
            objectFields add (path + ".environment") environmentKeys environment
            for key in [ "HOME"; "DOTNET_CLI_HOME"; "NUGET_PACKAGES" ] do
                stringValue add (path + ".environment." + key) 1 4096 (get environment key)
                if not (absolute (field environment key)) then add path "path-refused"
            constant add path "1" (get environment "DOTNET_CLI_TELEMETRY_OPTOUT")
            constant add path "1" (get environment "DOTNET_SKIP_FIRST_TIME_EXPERIENCE")
            constant add path "0" (get environment "DOTNET_MULTILEVEL_LOOKUP")
        let expectedSteps =
            if field root "profileId" = "c2-dotnet-scaffold-fixture/1" then [ "template-install"; "template-create" ]
            else [ "compiler-build" ]
        if (items steps |> List.map (fun step -> field step "stepId")) <> expectedSteps then
            add "$.steps" "fixed-step-order-required"
        validateWindow add (get root "budget")

    let private timing add path node =
        match field node "state" with
        | "known" ->
            objectFields add path [ "state"; "milliseconds" ] node
            integer add (path + ".milliseconds") 0L Int64.MaxValue (get node "milliseconds")
        | "unknown" ->
            objectFields add path [ "state"; "reason" ] node
            tokenValue add (path + ".reason") (get node "reason")
        | _ -> add path "timing-tag-required"
    let private base64 add path node =
        stringValue add path 0 1398104 node
        if node.ValueKind = JsonValueKind.String then
            try
                let encoded = str node
                if Convert.ToBase64String(Convert.FromBase64String encoded) <> encoded then add path "canonical-base64-required"
            with :? FormatException -> add path "canonical-base64-required"
    let private validateResult add root =
        objectFields add "$"
            [ "schema"; "operationId"; "requestSha256"; "profileSha256"; "disposition"; "reason"
              "executionStarted"; "retirement"; "cleanup"; "handoff"; "stdoutBase64"; "stderrBase64"
              "outputComplete"; "steps"; "outputs"; "elapsed" ] root
        constant add "$.schema" "fsgg.portable-workspace-c2-phase-result/1" (get root "schema")
        match Guid.TryParseExact(field root "operationId", "D") with
        | true, id when id.ToString("D") = field root "operationId" -> ()
        | _ -> add "$.operationId" "uuid-required"
        for key in [ "requestSha256"; "profileSha256" ] do hashValue add ("$." + key) 64 (get root key)
        oneOf add "$.disposition" [ "completed"; "refused"; "failed"; "unknown" ] (get root "disposition")
        tokenValue add "$.reason" (get root "reason")
        boolean add "$.executionStarted" (get root "executionStarted")
        boolean add "$.outputComplete" (get root "outputComplete")
        oneOf add "$.cleanup" [ "complete"; "pending"; "unknown" ] (get root "cleanup")
        oneOf add "$.handoff" [ "absent"; "complete"; "unknown" ] (get root "handoff")
        let retirement = get root "retirement"
        objectFields add "$.retirement" [ "helpers"; "containers" ] retirement
        for key in [ "helpers"; "containers" ] do
            oneOf add ("$.retirement." + key) [ "not-started"; "retired"; "unknown" ] (get retirement key)
        for key in [ "stdoutBase64"; "stderrBase64" ] do base64 add ("$." + key) (get root key)
        timing add "$.elapsed" (get root "elapsed")
        let steps = get root "steps"
        arrayValue add "$.steps" 0 2 steps
        for step in items steps do
            objectFields add "$.steps[]" [ "stepId"; "exit"; "duration" ] step
            oneOf add "$.steps[].stepId" [ "template-install"; "template-create"; "compiler-build" ] (get step "stepId")
            timing add "$.steps[].duration" (get step "duration")
            let exit = get step "exit"
            match field exit "state" with
            | "known" ->
                objectFields add "$.steps[].exit" [ "state"; "code" ] exit
                integer add "$.steps[].exit.code" (int64 Int32.MinValue) (int64 Int32.MaxValue) (get exit "code")
            | "unknown" ->
                objectFields add "$.steps[].exit" [ "state"; "reason" ] exit
                tokenValue add "$.steps[].exit.reason" (get exit "reason")
            | _ -> add "$.steps[].exit" "exit-tag-required"
        let outputs = get root "outputs"
        arrayValue add "$.outputs" 0 1024 outputs
        for row in items outputs do
            objectFields add "$.outputs[]" [ "relativePath"; "bytes"; "sha256" ] row
            stringValue add "$.outputs[].relativePath" 1 4096 (get row "relativePath")
            if not (relative false (field row "relativePath")) then add "$.outputs[]" "path-refused"
            integer add "$.outputs[].bytes" 0L 16777216L (get row "bytes")
            hashValue add "$.outputs[].sha256" 64 (get row "sha256")
        unique add "$.outputs[]" (items outputs |> List.map (fun row -> field row "relativePath"))
        if field root "disposition" = "completed" then
            if not (boolValue root "executionStarted") || not (boolValue root "outputComplete")
               || field root "cleanup" <> "complete" || field root "handoff" <> "complete"
               || field retirement "helpers" <> "retired" || field retirement "containers" <> "retired"
               || (items outputs).IsEmpty || (items steps).IsEmpty then
                add "$" "completed-observations-required"

    let decodeRequest bytes = decode 1048576 validateRequest bytes |> Result.map Request
    let encodeRequest request =
        let (Request (_, raw)) = request
        Array.copy raw
    let requestSha256 request =
        let (Request (_, raw)) = request
        SHA256.HashData raw |> Convert.ToHexString |> fun s -> s.ToLowerInvariant()
    let describeRequest request : RequestIdentity =
        let (Request (root, _)) = request
        { EnrollmentId = field root "enrollmentId"
          ProfileId = field root "profileId"
          ProfileSha256 = field root "profileSha256"
          OperationId = field root "operationId"
          IdempotencyId = field root "idempotencyId"
          RequestSha256 = requestSha256 request }
    let describeReads (Request(root, _)) : RequestReadDeclaration =
        let consumer = get root "consumer"
        let budget = get root "budget"
        { ConsumerRepository = field consumer "repository"
          ConsumerSource = field consumer "source"
          Inputs =
            items (get root "inputs") |> List.map (fun row ->
                { Id = field row "id"; Role = field row "role"; HostPath = field row "hostPath"
                  RelativePath = field row "relativePath"; Bytes = num (get row "bytes")
                  Sha256 = field row "sha256" })
          InputInventorySha256 = canonical (get root "inputs") |> SHA256.HashData |> Convert.ToHexString |> fun value -> value.ToLowerInvariant()
          OwnedRoots = items (get root "ownedRoots") |> List.map (fun row -> field row "role", field row "hostPath")
          Window =
            { PhaseStartedUtc = field budget "phaseStartedUtc"; NotAfterUtc = field budget "notAfterUtc"
              MaximumPhaseMs = num (get budget "maximumPhaseMs"); MaximumWorkMs = num (get budget "maximumWorkMs")
              CleanupReserveMs = num (get budget "cleanupReserveMs"); CallerFinishReserveMs = num (get budget "callerFinishReserveMs") } }
    let decodeResult bytes = decode 2097152 validateResult bytes |> Result.map ResultDocument
    let encodeResult document =
        let (ResultDocument (_, raw)) = document
        Array.copy raw

    let private boundedString maximum (s: string) =
        not (isNull s) && s.Length <= maximum * 2 && scalarLength s <= maximum
    let private boundedList maximum xs = xs |> List.truncate (maximum + 1) |> List.length <= maximum
    let private selectionBounded (s: DeclaredSelection) =
        let stringsBounded values = boundedList 64 values && List.forall (boundedString 4096) values
        not (isNull (box s)) && token s.EnrollmentId && hex 64 s.ProfileSha256 && hex 40 s.ConsumerSource
        && boundedString 128 s.ProfileId && boundedString 128 s.ConsumerRepository
        && boundedString 40 s.Window.PhaseStartedUtc && boundedString 40 s.Window.NotAfterUtc
        && boundedList 32 s.Inputs && boundedList 13 s.RolePaths && boundedList 2 s.Steps
        && (s.Inputs |> List.forall (fun row ->
            token row.Id && List.contains row.Role roles && boundedString 4096 row.HostPath
            && boundedString 4096 row.RelativePath && row.Bytes >= 0L && row.Bytes <= 16777216L && hex 64 row.Sha256))
        && (s.RolePaths |> List.forall (fun row ->
            List.contains row.Role roles && boundedString 4096 row.HostPath && boundedString 4096 row.ContainerPath))
        && (s.Steps |> List.forall (fun step ->
            boundedString 128 step.StepId && boundedString 4096 step.Executable && boundedString 128 step.WorkingRole
            && stringsBounded step.DeclaredArguments && stringsBounded step.ContainerArguments
            && boundedList 8 step.Translations && step.Environment.Count <= 6
            && (step.Environment |> Map.forall (fun k v -> boundedString 128 k && boundedString 4096 v))
            && (step.Translations |> List.forall (fun t ->
                t.ArgumentIndex >= 0 && t.ArgumentIndex <= 63 && boundedString 128 t.Role
                && boundedString 128 t.Prefix && boundedString 1024 t.RelativePath))))

    let private joinPath (root: string) (suffix: string) = if suffix = "" then root else root.TrimEnd('/') + "/" + suffix
    let private environmentFor (paths: Map<string, RolePath>) =
        let rolePath role = match Map.tryFind role paths with Some row -> row.ContainerPath | None -> ""
        Map.ofList
            [ "HOME", rolePath "home"; "DOTNET_CLI_HOME", rolePath "cli-home"; "NUGET_PACKAGES", rolePath "packages"
              "DOTNET_SKIP_FIRST_TIME_EXPERIENCE", "1"; "DOTNET_CLI_TELEMETRY_OPTOUT", "1"; "DOTNET_MULTILEVEL_LOOKUP", "0" ]
    let private translationsFor stepId : Translation list =
        let row index role prefix suffix = { ArgumentIndex = index; Role = role; Prefix = prefix; RelativePath = suffix }
        match stepId with
        | "template-install" -> [ row 2 "archive" "" ""; row 4 "hive" "" "" ]
        | "template-create" -> [ row 3 "hive" "" ""; row 5 "stage" "" "" ]
        | "compiler-build" ->
            [ row 1 "compiler-work" "" "Fixture.fsproj"; row 7 "stage" "-p:OtherFlags=--sig:" "public-surface.fsi" ]
        | _ -> []
    let private fixedArguments stepId (arguments: string list) =
        let at index expected = List.tryItem index arguments = Some expected
        match stepId with
        | "template-install" -> arguments.Length = 5 && at 0 "new" && at 1 "install" && at 3 "--debug:custom-hive"
        | "template-create" ->
            arguments.Length = 15 && at 0 "new" && at 1 "sdd-c22-local-fixture" && at 2 "--debug:custom-hive"
            && at 4 "--output" && at 6 "--no-restore" && at 7 "--raw" && at 9 "--package"
            && at 11 "--code" && at 13 "--mode" && at 14 "safe"
        | "compiler-build" ->
            arguments.Length = 8 && at 0 "build" && at 2 "--no-restore" && at 3 "--disable-build-servers"
            && at 4 "-m:1" && at 5 "-c" && at 6 "Release"
        | _ -> false
    let private containerRoles scaffold =
        if scaffold then
            Map.ofList [ "archive", "/inputs/archive.nupkg"; "catalog", "/inputs/catalog.json"; "policy", "/inputs/policy.json"
                         "home", "/work/home"; "cli-home", "/work/cli-home"; "hive", "/work/hive"
                         "packages", "/work/packages"; "stage", "/output/project"; "handoff", "/handoff" ]
        else
            Map.ofList [ "compiler-source", "/inputs/compiler"; "restore-state", "/inputs/restore-state"
                         "baseline", "/inputs/public-surface.baseline"; "home", "/work/home"; "cli-home", "/work/cli-home"
                         "packages", "/work/packages"; "compiler-work", "/work/compiler"; "stage", "/output"; "handoff", "/handoff" ]

    let bindDeclared selection request =
        let (Request (root, _)) = request
        try
            if not (selectionBounded selection) then Error [ { Field = "$.selection"; Code = "selection-bound" } ]
            else
                let add, errors = collector ()
                let scaffold = selection.ProfileId = "c2-dotnet-scaffold-fixture/1"
                let compiler = selection.ProfileId = "c2-dotnet-compiler-fixture/1"
                if not (scaffold || compiler) then add "$.selection.profileId" "profile-refused"
                let expectedSteps = if scaffold then [ "template-install"; "template-create" ] else [ "compiler-build" ]
                let expectedConsumer = if scaffold then "FS-GG/FS.GG.SDD" else "FS-GG/FS.GG.Governance"
                if selection.ConsumerRepository <> expectedConsumer then add "$.selection.consumer" "consumer-refused"
                for key, expected in
                    [ "enrollmentId", selection.EnrollmentId; "profileId", selection.ProfileId; "profileSha256", selection.ProfileSha256 ] do
                    if field root key <> expected then add ("$." + key) "declaration-mismatch"
                let consumer = get root "consumer"
                if field consumer "repository" <> selection.ConsumerRepository || field consumer "source" <> selection.ConsumerSource then
                    add "$.consumer" "declaration-mismatch"
                let decodedInputs : InputDeclaration list =
                    items (get root "inputs") |> List.map (fun row ->
                        { Id = field row "id"; Role = field row "role"; HostPath = field row "hostPath"
                          RelativePath = field row "relativePath"; Bytes = value row "bytes"; Sha256 = field row "sha256" })
                if decodedInputs <> selection.Inputs then add "$.inputs" "declaration-mismatch"
                unique add "$.selection.roles" (selection.RolePaths |> List.map _.Role)
                unique add "$.selection.hostPaths" (selection.RolePaths |> List.map _.HostPath)
                for left in selection.RolePaths do
                    for right in selection.RolePaths do
                        if left.Role <> right.Role && right.HostPath.StartsWith(left.HostPath.TrimEnd('/') + "/", StringComparison.Ordinal) then
                            add "$.selection.roles" "overlapping-role-paths"
                let paths = selection.RolePaths |> List.map (fun row -> row.Role, row) |> Map.ofList
                let selectedContainerRoles = containerRoles scaffold
                // The source-only compiler fixture explicitly lacks restore state.
                // Every other fixed role is required; supplied restore inputs require
                // their mapping, but an unused mapping cannot invent readiness.
                let requiresRestore = decodedInputs |> List.exists (fun input -> input.Role = "restore-state")
                let requiredRoles =
                    selectedContainerRoles |> Map.toList |> List.map fst
                    |> List.filter (fun role -> role <> "restore-state" || requiresRestore) |> Set.ofList
                if (paths |> Map.toList |> List.map fst |> Set.ofList) <> requiredRoles then
                    add "$.selection.roles" "fixed-role-set-required"
                for row in selection.RolePaths do
                    if not (absolute row.HostPath) || Map.tryFind row.Role selectedContainerRoles <> Some row.ContainerPath then
                        add "$.selection.roles" "role-path-refused"
                let writable = [ "home"; "cli-home"; "hive"; "packages"; "stage"; "compiler-work"; "handoff" ]
                let selectedRoots = selection.RolePaths |> List.filter (fun row -> List.contains row.Role writable)
                let actualRoots = items (get root "ownedRoots") |> List.map (fun row -> field row "role", field row "hostPath")
                if actualRoots <> (selectedRoots |> List.map (fun row -> row.Role, row.HostPath)) then
                    add "$.ownedRoots" "declaration-mismatch"
                for input in decodedInputs do
                    match Map.tryFind input.Role paths with
                    | None -> add "$.inputs" "role-path-required"
                    | Some row ->
                        let expectedHost =
                            if List.contains input.Role [ "compiler-source"; "restore-state" ] then joinPath row.HostPath input.RelativePath
                            else row.HostPath
                        if expectedHost <> input.HostPath then add "$.inputs" "input-role-path-mismatch"
                if scaffold then
                    if (decodedInputs |> List.map _.Role |> List.sort) <> [ "archive"; "catalog"; "policy" ] then
                        add "$.inputs" "scaffold-input-set"
                else
                    let sourceNames = decodedInputs |> List.filter (fun row -> row.Role = "compiler-source") |> List.map _.RelativePath |> List.sort
                    if sourceNames <> [ "Fixture.fsproj"; "Library.fs"; "Library.fsi"; "global.json" ] then add "$.inputs" "compiler-source-set"
                    if (decodedInputs |> List.filter (fun row -> row.Role = "baseline")).Length <> 1
                       || (decodedInputs |> List.exists (fun row -> not (List.contains row.Role [ "compiler-source"; "baseline"; "restore-state" ]))) then
                        add "$.inputs" "compiler-input-set"
                let actualSteps = items (get root "steps")
                if List.map (fun step -> field step "stepId") actualSteps <> expectedSteps
                   || List.map (fun (step: StepDeclaration) -> step.StepId) selection.Steps <> expectedSteps then
                    add "$.steps" "fixed-step-order-required"
                for i, declared in selection.Steps |> List.indexed do
                    let path = sprintf "$.steps[%d]" i
                    let workingRole = if scaffold then "home" else "compiler-work"
                    if declared.Executable <> "/usr/share/dotnet/dotnet" || declared.WorkingRole <> workingRole
                       || not (Map.containsKey workingRole paths) || declared.Environment <> environmentFor paths then
                        add path "fixed-command-refused"
                    if not (fixedArguments declared.StepId declared.DeclaredArguments)
                       || not (fixedArguments declared.StepId declared.ContainerArguments)
                       || declared.Translations <> translationsFor declared.StepId then add path "fixed-arguments-refused"
                    if declared.DeclaredArguments.Length <> declared.ContainerArguments.Length then add path "argument-count-mismatch"
                    for index, argument in declared.DeclaredArguments |> List.indexed do
                        match List.tryFind (fun t -> t.ArgumentIndex = index) declared.Translations with
                        | None ->
                            if List.tryItem index declared.ContainerArguments <> Some argument then add path "untranslated-argument-drift"
                        | Some translation ->
                            match Map.tryFind translation.Role paths with
                            | None -> add path "translation-role-required"
                            | Some role ->
                                if argument <> translation.Prefix + joinPath role.HostPath translation.RelativePath
                                   || List.tryItem index declared.ContainerArguments <> Some (translation.Prefix + joinPath role.ContainerPath translation.RelativePath) then
                                    add path "translation-mismatch"
                    match List.tryItem i actualSteps with
                    | None -> add path "selected-step-missing"
                    | Some step ->
                        let actualTranslations : Translation list =
                            items (get step "translations") |> List.map (fun row ->
                                { ArgumentIndex = int (value row "argumentIndex"); Role = field row "role"
                                  Prefix = field row "prefix"; RelativePath = field row "relativePath" })
                        let actualEnvironment =
                            (get step "environment").EnumerateObject() |> Seq.map (fun p -> p.Name, str p.Value) |> Map.ofSeq
                        if field step "stepId" <> declared.StepId || field step "executable" <> declared.Executable
                           || field step "workingRole" <> declared.WorkingRole
                           || strings (get step "declaredArguments") <> declared.DeclaredArguments
                           || strings (get step "containerArguments") <> declared.ContainerArguments
                           || actualTranslations <> declared.Translations || actualEnvironment <> declared.Environment then
                            add path "declaration-mismatch"
                let budget = get root "budget"
                let w = selection.Window
                if field budget "phaseStartedUtc" <> w.PhaseStartedUtc || field budget "notAfterUtc" <> w.NotAfterUtc then
                    add "$.budget" "original-window-mismatch"
                for key, expected in
                    [ "maximumPhaseMs", w.MaximumPhaseMs; "maximumWorkMs", w.MaximumWorkMs
                      "cleanupReserveMs", w.CleanupReserveMs; "callerFinishReserveMs", w.CallerFinishReserveMs
                      "maximumCapturedBytes", selection.MaximumCapturedBytes; "maximumHandoffBytes", selection.MaximumHandoffBytes
                      "maximumHandoffEntries", int64 selection.MaximumHandoffEntries ] do
                    if value budget key <> expected then add ("$.budget." + key) "declaration-mismatch"
                match errors () with [] -> Ok (BoundDeclaration (requestSha256 request)) | diagnostics -> Error diagnostics
        with
        | :? NullReferenceException | :? ArgumentException ->
            Error [ { Field = "$.selection"; Code = "invalid-selection" } ]

    let validateResultJoin request document =
        let (Request (requestRoot, _)) = request
        let (ResultDocument (documentRoot, _)) = document
        let add, errors = collector ()
        if field documentRoot "operationId" <> field requestRoot "operationId" then add "$.operationId" "request-join-mismatch"
        if field documentRoot "profileSha256" <> field requestRoot "profileSha256" then add "$.profileSha256" "request-join-mismatch"
        if field documentRoot "requestSha256" <> requestSha256 request then add "$.requestSha256" "request-join-mismatch"
        let budget = get requestRoot "budget"
        let captureBytes =
            [ "stdoutBase64"; "stderrBase64" ] |> List.sumBy (fun key -> int64 ((Convert.FromBase64String(field documentRoot key)).Length))
        if captureBytes > value budget "maximumCapturedBytes" then add "$.streams" "aggregate-capture-bound"
        let outputs = items (get documentRoot "outputs")
        if int64 outputs.Length > value budget "maximumHandoffEntries" then add "$.outputs" "aggregate-handoff-entries"
        if (outputs |> List.sumBy (fun row -> value row "bytes")) > value budget "maximumHandoffBytes" then
            add "$.outputs" "aggregate-handoff-bytes"
        let selectedSteps = items (get requestRoot "steps") |> List.map (fun step -> field step "stepId")
        let reportedSteps = items (get documentRoot "steps")
        let reportedIds = reportedSteps |> List.map (fun step -> field step "stepId")
        if reportedIds <> (selectedSteps |> List.truncate reportedSteps.Length) then add "$.steps" "ordered-prefix-required"
        let knownZero step = field (get step "exit") "state" = "known" && value (get step "exit") "code" = 0L
        for index, step in reportedSteps |> List.indexed do
            if not (knownZero step) && index < reportedSteps.Length - 1 then add "$.steps" "continued-after-stop"
        if reportedSteps.Length > 0 && not (boolValue documentRoot "executionStarted") then add "$.executionStarted" "execution-state-mismatch"
        if field documentRoot "disposition" = "completed" then
            if reportedIds <> selectedSteps || not (List.forall knownZero reportedSteps) then add "$.steps" "completed-sequence-required"
            if not (boolValue documentRoot "executionStarted") || not (boolValue documentRoot "outputComplete")
               || field documentRoot "cleanup" <> "complete" || field documentRoot "handoff" <> "complete" || outputs.IsEmpty
               || field (get documentRoot "retirement") "helpers" <> "retired"
               || field (get documentRoot "retirement") "containers" <> "retired" then
                add "$" "completed-observations-required"
            let elapsed = get documentRoot "elapsed"
            let cutoff = value budget "maximumPhaseMs" - value budget "callerFinishReserveMs"
            if field elapsed "state" <> "known" || value elapsed "milliseconds" > cutoff then add "$.elapsed" "completed-cutoff"
            let durations = reportedSteps |> List.map (fun step -> get step "duration")
            if durations |> List.exists (fun duration -> field duration "state" <> "known") then add "$.steps.duration" "completed-timing-required"
            else
                // Subtraction avoids overflow in honest signed64-bit observations.
                let mutable remaining = value elapsed "milliseconds"
                let mutable remainingWork = value budget "maximumWorkMs"
                for duration in durations do
                    let milliseconds = value duration "milliseconds"
                    if milliseconds > remaining then add "$.steps.duration" "completed-timing-mismatch"
                    else remaining <- remaining - milliseconds
                    if milliseconds > remainingWork then add "$.steps.duration" "completed-work-bound"
                    else remainingWork <- remainingWork - milliseconds
        match errors () with [] -> Ok () | diagnostics -> Error diagnostics

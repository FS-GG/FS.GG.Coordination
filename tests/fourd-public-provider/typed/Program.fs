open System
open FsQuint
open FS.GG.FourD.Typed
open System.Text.Json.Nodes

let fail message = raise (InvalidOperationException message)
let policyOk (result: Result<'a, string>) = match result with Ok value -> value | Error reason -> fail reason
let replayOk (result: Result<'a, QuintReplayDiagnostic list>) = match result with Ok value -> value | Error reason -> fail $"%A{reason}"
let source (name: string) = { Path = "eng/fourd-public-provider/typed/Policy.fs"; Line = 1; Column = name.Length }
let text value = QuintReplayValue.Text value
let boolean value = QuintReplayValue.Boolean value
let integer value = QuintReplayValue.Integer(string value)

let identityNumber = function None -> 0 | Some "id-a" -> 1 | Some "id-b" -> 2 | Some _ -> 3
let phaseText = function
    | Idle -> "idle" | Acquired -> "acquired" | Validated -> "validated" | Admitted -> "admitted"
    | Effect -> "effect" | Cleanup -> "cleanup" | Finished -> "finished" | CleanupFailed -> "cleanup-failed"
let outcomeText = function NoneObserved -> "none" | Unknown -> "unknown" | Success -> "success" | Refused -> "refused"

let project state =
    let draft = {
        Identity = String.replicate 64 "0"
        Bindings = [
            "state", QuintReplayValue.Record [
                "acquiredIdentity", integer (identityNumber state.AcquiredIdentity)
                "admittedIdentity", integer (identityNumber state.AdmittedIdentity)
                "budget", integer state.BudgetRemaining
                "cancelled", boolean state.Cancelled
                "closed", integer state.Closed.Count
                "currentIdentity", integer (identityNumber state.CurrentIdentity)
                "effectAcknowledged", boolean state.EffectAcknowledged
                "outcome", text (outcomeText state.Outcome)
                "owned", integer state.Owned.Count
                "phase", text (phaseText state.Phase)
                "validatedIdentity", integer (identityNumber state.ValidatedIdentity)
            ]
        ]
    }
    { draft with Identity = QuintReplay.stateFingerprint draft |> replayOk }

let apply (name: string) observation (states: State list, observations: QuintReplayObservation list) =
    let next = Policy.reduce (List.last states) observation |> policyOk
    let index = observations.Length + 1
    let actual = project next
    let observed = { Index = index; Action = name; Source = source name; Actual = actual }
    states @ [next], observations @ [observed]

let start = ([Policy.initial 12], [])
let success = start |> apply "acquire" (Acquire("id-a","plaintext",1)) |> apply "validate" (Validate("id-a",1))
              |> apply "admit" (Admit("id-a",1)) |> apply "beginEffect" (BeginEffect("effect",false,1))
              |> apply "observeSuccess" (ObserveSuccess 1) |> apply "beginCleanup" (BeginCleanup(false,1))
              |> apply "closePlaintext" (Close("plaintext",1)) |> apply "closeEffect" (Close("effect",1)) |> apply "finish" (Finish 1)
let scenario = Environment.GetEnvironmentVariable "FSGG_FOURD_QUINT_SCENARIO"
let states, observations =
    match scenario with
    | "success" -> success
    | "stale" -> start |> apply "acquire" (Acquire("id-a","plaintext",1)) |> apply "validate" (Validate("id-a",1))
                       |> apply "admit" (Admit("id-a",1)) |> apply "invalidate" (Invalidate("id-b",1))
    | "cancelled" -> start |> apply "acquire" (Acquire("id-a","plaintext",1)) |> apply "cancel" (BeginCleanup(true,1))
                           |> apply "closePlaintext" (Close("plaintext",1)) |> apply "finish" (Finish 1)
    | "unknown" -> start |> apply "acquire" (Acquire("id-a","plaintext",1)) |> apply "validate" (Validate("id-a",1))
                         |> apply "admit" (Admit("id-a",1)) |> apply "beginEffect" (BeginEffect("effect",false,1))
                         |> apply "beginCleanup" (BeginCleanup(false,1)) |> apply "closePlaintext" (Close("plaintext",1))
                         |> apply "closeEffect" (Close("effect",1)) |> apply "finish" (Finish 1)
    | "cleanup-failure" -> start |> apply "acquire" (Acquire("id-a","plaintext",1)) |> apply "validate" (Validate("id-a",1))
                                 |> apply "admit" (Admit("id-a",1)) |> apply "beginEffect" (BeginEffect("effect",false,1))
                                 |> apply "beginCleanup" (BeginCleanup(false,1)) |> apply "closePlaintext" (Close("plaintext",1))
                                 |> apply "finish" (Finish 1)
    | _ -> fail "FSGG_FOURD_QUINT_SCENARIO is required"

let fingerprint name =
    let value = Environment.GetEnvironmentVariable name
    if String.IsNullOrWhiteSpace value || value.Length <> 64
       || value |> Seq.exists (fun c -> not (Char.IsDigit c || c >= 'a' && c <= 'f')) then
        fail $"{name} must contain an observed SHA-256 fingerprint"
    value
let environment = {
    Seed = "20261001"; Bounds = ["steps", 12L]
    ToolFingerprint = fingerprint "FSGG_FOURD_QUINT_TOOL_SHA256"
    ProfileFingerprint = fingerprint "FSGG_FOURD_QUINT_PROFILE_SHA256"
    ContractFingerprint = fingerprint "FSGG_FOURD_QUINT_CONTRACT_SHA256"
    AdapterFingerprint = fingerprint "FSGG_FOURD_QUINT_ADAPTER_SHA256"
    ImplementationFingerprint = fingerprint "FSGG_FOURD_QUINT_IMPLEMENTATION_SHA256"
}
let bindings = observations |> List.map (fun item -> { Index=item.Index; Action=item.Action; Source=item.Source })
let trace =
    let path = Environment.GetEnvironmentVariable "FSGG_FOURD_QUINT_ITF"
    if String.IsNullOrWhiteSpace path then fail "FSGG_FOURD_QUINT_ITF is required"
    let context = { Environment=environment; Steps=bindings }
    let root = JsonNode.Parse(IO.File.ReadAllText(path)).AsObject()
    let metadata = root["#meta"].AsObject()
    metadata.Remove("description") |> ignore
    metadata.Remove("timestamp") |> ignore
    root.ToJsonString() |> QuintReplay.decodeItf context |> replayOk

match QuintReplay.compare trace observations with
| Ok QuintReplayResult.Equivalent -> ()
| value -> fail $"FsQuint correspondence failed: %A{value}"

if scenario="success" && not (Policy.successful (List.last states)) then fail "acknowledged effect with complete cleanup was not successful"

let successStates,_ = success
let admitted = successStates[3]
match Policy.reduce admitted (Invalidate("id-b", 1)) with
| Ok stale when Policy.effectEligible stale -> fail "stale identity remained eligible"
| Ok _ -> ()
| Error reason -> fail reason

let unacknowledged = Policy.reduce admitted (BeginEffect("effect", false, 1)) |> policyOk
let cleaning = Policy.reduce unacknowledged (Cancel 1) |> policyOk
let onlyPlaintext = Policy.reduce cleaning (Close("plaintext", 1)) |> policyOk
let incomplete = Policy.reduce onlyPlaintext (Finish 1) |> policyOk
if Policy.cleanupComplete incomplete || Policy.successful incomplete then fail "false cleanup or success accepted"

let mutated = observations |> List.mapi (fun index item ->
    if index = 2 then { item with Actual = project { states[3] with CurrentIdentity = Some "stale" } } else item)
match QuintReplay.compare trace mutated with
| Ok (QuintReplayResult.Diverged _) -> ()
| value -> fail $"negative correspondence control did not diverge: %A{value}"

printfn "FourD typed reducer and FsQuint correspondence: PASS (%d transitions)" observations.Length

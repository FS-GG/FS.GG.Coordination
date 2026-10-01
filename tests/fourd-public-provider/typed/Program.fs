open System
open FsQuint
open FS.GG.FourD.Typed

let fail message = raise (InvalidOperationException message)
let policyOk (result: Result<'a, string>) = match result with Ok value -> value | Error reason -> fail reason
let replayOk (result: Result<'a, QuintReplayDiagnostic list>) = match result with Ok value -> value | Error reason -> fail $"%A{reason}"
let source (name: string) = { Path = "eng/fourd-public-provider/typed/Policy.fs"; Line = 1; Column = name.Length }
let text value = QuintReplayValue.Text value
let boolean value = QuintReplayValue.Boolean value
let integer value = QuintReplayValue.Integer(string value)

let project state =
    let draft = {
        Identity = String.replicate 64 "0"
        Bindings = [
            "phase", text (string state.Phase)
            "identity", text (state.CurrentIdentity |> Option.defaultValue "")
            "effectAcknowledged", boolean state.EffectAcknowledged
            "outcome", text (string state.Outcome)
            "owned", integer state.Owned.Count
            "closed", integer state.Closed.Count
            "cancelled", boolean state.Cancelled
            "budget", integer state.BudgetRemaining
        ]
    }
    { draft with Identity = QuintReplay.stateFingerprint draft |> replayOk }

let apply (name: string) observation (states: State list, observations: QuintReplayObservation list) =
    let next = Policy.reduce (List.last states) observation |> policyOk
    let index = observations.Length + 1
    let actual = project next
    let observed = { Index = index; Action = name; Source = source name; Actual = actual }
    states @ [next], observations @ [observed]

let states, observations =
    ([Policy.initial 12], [])
    |> apply "acquire" (Acquire("id-a", "plaintext", 1))
    |> apply "validate" (Validate("id-a", 1))
    |> apply "admit" (Admit("id-a", 1))
    |> apply "beginEffect" (BeginEffect("effect", false, 1))
    |> apply "observeSuccess" (ObserveSuccess 1)
    |> apply "cancel" (Cancel 1)
    |> apply "closePlaintext" (Close("plaintext", 1))
    |> apply "closeEffect" (Close("effect", 1))
    |> apply "finish" (Finish 1)

let steps = observations |> List.map (fun item -> {
    Index = item.Index; Action = item.Action; Source = item.Source; Expected = item.Actual })
let environment = {
    Seed = "20261001"; Bounds = ["steps", 12L]; ToolFingerprint = String.replicate 64 "1"
    ProfileFingerprint = String.replicate 64 "2"; ContractFingerprint = String.replicate 64 "3"
    AdapterFingerprint = String.replicate 64 "4"; ImplementationFingerprint = String.replicate 64 "5"
}
let initial = project states.Head
let draft = { SchemaVersion = 1; TraceIdentity = String.replicate 64 "0"; Environment = environment; Initial = initial; Steps = steps }
let trace = { draft with TraceIdentity = QuintReplay.traceFingerprint draft |> replayOk }

match QuintReplay.compare trace observations with
| Ok QuintReplayResult.Equivalent -> ()
| value -> fail $"FsQuint correspondence failed: %A{value}"

if not (Policy.successful (List.last states)) then fail "acknowledged effect with complete cleanup was not successful"

let admitted = states[3]
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

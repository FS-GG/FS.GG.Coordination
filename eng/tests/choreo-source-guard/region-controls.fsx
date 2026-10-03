#load "../../validate-choreo-trace-source.fsx"

open System
open ``Validate-choreo-trace-source``

let refuses name action =
    let mutable refused = false

    try
        action ()
    with _ ->
        refused <- true

    if not refused then
        failwith (name + " unexpectedly accepted")

validateRegions historical current

for name, original, replacement in
    [
        "choreo-effect",
        "type EffectKind = Claim | ProcessWork | Candidate | Branch | PullRequest | Merge | NativeReadback",
        "type EffectKind = Claim | ProcessWork | Candidate | Branch | PullRequest | Merge | Drift"
        "legacy", "module O2HostedWriterModel {", "module O2HostedWriterModel {\n  // drift"
        "pinned-c3-region",
        "// BEGIN PINNED quint-co/choreo spells/basicSpells.qnt",
        "// BEGIN PINNED quint-co/choreo spells/basicSpells.qnt\n// drift"
    ] do
    if not (current.Contains original) then
        failwith (name + " mutation target absent")

    let changed = current.Replace(original, replacement)

    if changed = current then
        failwith (name + " mutation did not change bytes")

    refuses name (fun () -> validateRegions historical changed)

refuses "duplicate-module" (fun () -> validateRegions historical (current + "\nmodule ChoreoSourcePinSmoke {\n}\n"))

refuses "historical-region-drift" (fun () ->
    validateRegions (historical.Replace("hostCompleted(Claim) and safety", "hostCompleted(Claim) and true")) current)

printfn "CHOREO_REGION_CONTROLS_OK positive=1 negative=5"

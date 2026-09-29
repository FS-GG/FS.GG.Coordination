namespace FS.GG.Coordination.Orchestration.Host.Tests

open System
open System.IO
open System.Security.Cryptography
open System.Text.Json.Nodes

type LearningTraceSnapshot =
    {
        Generation: int
        TreatmentDigest: int
        TreatmentOwner: int
        PreparationVersion: int
        Disposition: int
        PlannerPresent: bool
        ContextPresent: bool
        RootManifest: int
        RootBindingIdentity: int
        ChildBound: bool
        ChildManifest: int
        ChildBindingIdentity: int
        ChildParentIdentity: int
        ChildTreatmentDigest: int
        ExecutionBound: bool
        ExecutionBindingIdentity: int
        ExecutionSubjectIdentity: int
        ExecutionTreatmentDigest: int
        ExecutionReplayAccepted: bool
        CapabilityStatus: int
        CapabilityFresh: bool
        CapabilityExact: bool
        LaunchIntent: bool
        LaunchCount: int
        ActiveCount: int
        BudgetRemaining: int
        ResponseLost: bool
        ReplayAccepted: bool
        Restarted: bool
        DuplicateRejected: bool
        StaleRejected: bool
        MissingContextRejected: bool
        CapabilityRejected: bool
        CapacityRejected: bool
        BudgetRejected: bool
        ShadowAttempted: bool
        ShadowEffectCount: int
        OutcomeUnknown: bool
    }

type LearningTrace =
    {
        Id: string
        Sha256: string
        States: LearningTraceSnapshot list
    }

[<RequireQualifiedAccess>]
module LearningTrace =
    let private source =
        "src/FS.GG.Coordination.Protocol/Protocol.md#LearningAdmissionTests"

    let private fixtureRoot () =
        Path.Combine(AppContext.BaseDirectory, "Fixtures", "Learning")

    let private sha256 (bytes: byte array) =
        SHA256.HashData bytes |> Convert.ToHexString |> _.ToLowerInvariant()

    let private text (value: JsonObject) (name: string) = value[name].GetValue<string>()
    let private boolean (value: JsonObject) (name: string) = value[name].GetValue<bool>()

    let private integer (value: JsonObject) (name: string) =
        let encoded = value[name].AsObject()
        Int32.Parse(encoded["#bigint"].GetValue<string>())

    let private snapshot (value: JsonObject) =
        {
            Generation = integer value "generation"
            TreatmentDigest = integer value "treatmentDigest"
            TreatmentOwner = integer value "treatmentOwner"
            PreparationVersion = integer value "preparationVersion"
            Disposition = integer value "disposition"
            PlannerPresent = boolean value "plannerPresent"
            ContextPresent = boolean value "contextPresent"
            RootManifest = integer value "rootManifest"
            RootBindingIdentity = integer value "rootBindingIdentity"
            ChildBound = boolean value "childBound"
            ChildManifest = integer value "childManifest"
            ChildBindingIdentity = integer value "childBindingIdentity"
            ChildParentIdentity = integer value "childParentIdentity"
            ChildTreatmentDigest = integer value "childTreatmentDigest"
            ExecutionBound = boolean value "executionBound"
            ExecutionBindingIdentity = integer value "executionBindingIdentity"
            ExecutionSubjectIdentity = integer value "executionSubjectIdentity"
            ExecutionTreatmentDigest = integer value "executionTreatmentDigest"
            ExecutionReplayAccepted = boolean value "executionReplayAccepted"
            CapabilityStatus = integer value "capabilityStatus"
            CapabilityFresh = boolean value "capabilityFresh"
            CapabilityExact = boolean value "capabilityExact"
            LaunchIntent = boolean value "launchIntent"
            LaunchCount = integer value "launchCount"
            ActiveCount = integer value "activeCount"
            BudgetRemaining = integer value "budgetRemaining"
            ResponseLost = boolean value "responseLost"
            ReplayAccepted = boolean value "replayAccepted"
            Restarted = boolean value "restarted"
            DuplicateRejected = boolean value "duplicateRejected"
            StaleRejected = boolean value "staleRejected"
            MissingContextRejected = boolean value "missingContextRejected"
            CapabilityRejected = boolean value "capabilityRejected"
            CapacityRejected = boolean value "capacityRejected"
            BudgetRejected = boolean value "budgetRejected"
            ShadowAttempted = boolean value "shadowAttempted"
            ShadowEffectCount = integer value "shadowEffectCount"
            OutcomeUnknown = boolean value "outcomeUnknown"
        }

    let safety (snapshot: LearningTraceSnapshot) =
        [
            if snapshot.TreatmentDigest <> 0 then
                let validDisposition =
                    (snapshot.PreparationVersion = 1
                     && snapshot.Disposition = 1
                     && not snapshot.PlannerPresent
                     && snapshot.ContextPresent)
                    || (snapshot.PreparationVersion = 1
                        && snapshot.Disposition = 2
                        && snapshot.PlannerPresent
                        && snapshot.ContextPresent)
                    || (snapshot.PreparationVersion = 1
                        && snapshot.Disposition = 3
                        && not snapshot.PlannerPresent
                        && snapshot.ContextPresent)

                if not validDisposition then
                    "disposition"
            if snapshot.TreatmentDigest <> 0 && snapshot.RootBindingIdentity = 0 then
                "root-binding"
            if
                snapshot.ChildBound
                && (snapshot.TreatmentDigest = 0
                    || snapshot.ChildBindingIdentity = 0
                    || snapshot.ChildBindingIdentity = snapshot.RootBindingIdentity
                    || snapshot.ChildParentIdentity <> snapshot.RootBindingIdentity
                    || snapshot.ChildParentIdentity = snapshot.ChildBindingIdentity
                    || snapshot.ChildTreatmentDigest <> snapshot.TreatmentDigest)
            then
                "child-binding"
            if snapshot.ExecutionBound then
                let subjectValid =
                    (snapshot.ExecutionSubjectIdentity = snapshot.RootBindingIdentity
                     && snapshot.ExecutionBindingIdentity = 3)
                    || (snapshot.ChildBound
                        && snapshot.ExecutionSubjectIdentity = snapshot.ChildBindingIdentity
                        && snapshot.ExecutionBindingIdentity = 4)

                if
                    snapshot.ExecutionBindingIdentity = 0
                    || snapshot.ExecutionBindingIdentity = snapshot.ExecutionSubjectIdentity
                    || snapshot.ExecutionBindingIdentity = snapshot.RootBindingIdentity
                    || snapshot.ExecutionBindingIdentity = snapshot.ChildBindingIdentity
                    || not subjectValid
                    || snapshot.ExecutionTreatmentDigest <> snapshot.TreatmentDigest
                then
                    "execution-binding"
            if
                snapshot.LaunchCount > 0
                && (not snapshot.ExecutionBound
                    || snapshot.ExecutionTreatmentDigest <> snapshot.TreatmentDigest
                    || snapshot.CapabilityStatus <> 1
                    || not snapshot.CapabilityFresh
                    || not snapshot.CapabilityExact
                    || snapshot.BudgetRemaining <> 0)
            then
                "launch-guard"
            if snapshot.LaunchCount > 1 then
                "duplicate-launch"
            if snapshot.ActiveCount > 1 then
                "capacity"
            if snapshot.BudgetRemaining < 0 then
                "budget"
            if snapshot.ShadowEffectCount <> 0 then
                "shadow-effect"
            if snapshot.OutcomeUnknown && snapshot.ActiveCount <> 0 then
                "unknown-effect-fence"
        ]

    let firstDivergence trace =
        trace.States |> List.tryFindIndex (safety >> List.isEmpty >> not)

    let loadAll () =
        let manifestPath = Path.Combine(fixtureRoot (), "manifest.json")
        let manifest = JsonNode.Parse(File.ReadAllBytes manifestPath).AsObject()

        if
            text manifest "schema" <> "fsgg.coordination.learning-quint-traces/1"
            || text manifest "source" <> source
        then
            failwith "learning trace manifest identity differs"

        manifest["traces"].AsArray()
        |> Seq.map (fun item ->
            let entry = item.AsObject()
            let file = text entry "file"
            let path = Path.Combine(fixtureRoot (), file)
            let bytes = File.ReadAllBytes path

            if sha256 bytes <> text entry "sha256" then
                failwith $"learning trace digest differs: {file}"

            let root = JsonNode.Parse(bytes).AsObject()

            if text (root["#meta"].AsObject()) "source" <> source then
                failwith $"learning trace source differs: {file}"

            let vars = root["vars"].AsArray()

            if vars.Count <> 1 || vars[0].GetValue<string>() <> "learning" then
                failwith $"learning trace variable differs: {file}"

            let states =
                root["states"].AsArray()
                |> Seq.map (fun state ->
                    let stateObject = state.AsObject()
                    snapshot (stateObject["learning"].AsObject()))
                |> Seq.toList

            if states.Length <> entry["states"].GetValue<int>() then
                failwith $"learning trace state count differs: {file}"

            {
                Id = Path.GetFileNameWithoutExtension(Path.GetFileNameWithoutExtension(file))
                Sha256 = sha256 bytes
                States = states
            })
        |> Seq.toList

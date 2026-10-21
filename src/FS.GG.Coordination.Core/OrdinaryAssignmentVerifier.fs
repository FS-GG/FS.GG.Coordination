namespace FS.GG.Coordination.Core

open System
open OrdinaryAssignment
open Orchestration

module OrdinaryAssignmentVerifier =
    // Deliberately no selector or selector feasibility helper dependency. Shared trusted
    // primitives: domain validation, canonical encoding, named units and path comparison.
    let private boundedDecision (decision: OrdinaryAssignment.Decision) =
        let within maximum items = (items |> List.truncate (maximum+1) |> List.length)<=maximum
        try
            if not (within 8 decision.Selected && within 256 decision.Eligible && within 256 decision.Excluded && within 128 decision.Reasons) then
                Error(NeedsDecomposition ["decision-population-bound"])
            else
                let bytes,nodes=canonicalMeasure decision
                if bytes>maximumResultBytes then Error(NeedsDecomposition ["decision-payload-bound"])
                elif int64 nodes*20L>int64 maximumEvaluations then Error(NeedsDecomposition ["decision-evaluation-bound"])
                else Ok(int64 nodes*20L)
        with :? ArgumentException as ex -> Error(NeedsDecomposition [ex.Message])

    let verify (authority: OrdinaryAssignment.Authority) (snapshot: OrdinaryAssignment.Snapshot) (decision: OrdinaryAssignment.Decision) =
        match boundedDecision decision with
        | Error error -> Error error
        | Ok decisionCost ->
            match validateAuthority authority,validateSnapshot snapshot with
            | Error error,_ | _,Error error -> Error error
            | Ok authority,Ok snapshot ->
                let authoritative = authority.Works |> List.map (fun w -> w.Key,w) |> Map.ofList
                let frozen = snapshot.Works |> List.map (fun w -> w.Key,w) |> Map.ofList
                let selectedKeys=decision.Selected |> List.map (fun a -> a.Specification.Requirements.Key)
                let inputMismatch =
                    snapshot.AuthoritySha256<>authorityDigest authority || authoritative<>frozen
                    || (Set.ofList selectedKeys).Count<>selectedKeys.Length
                    || (decision.Selected |> List.exists (fun a ->
                        Map.tryFind a.Specification.Requirements.Key authoritative<>Some a.Specification.Requirements
                        || not (snapshot.Profiles |> List.contains a.Specification.Profile)))
                let _,authorityNodes=canonicalMeasure authority
                let totalWork=snapshotEvaluationBound snapshot + decisionCost + int64 authorityNodes*10L
                if inputMismatch then Error(ConflictingEvidence ["authoritative-snapshot-or-candidate-mismatch"])
                elif totalWork>int64 maximumEvaluations then Error(NeedsDecomposition ["combined-verification-evaluation-bound"])
                else
                    try
                        let errors = ResizeArray<string>()
                        let require condition error = if not condition then errors.Add error
                        let authorityHash = authorityDigest authority
                        require (snapshot.AuthoritySha256=authorityHash) "snapshot-authority-binding"
                        let authoritative = authority.Works |> List.map (fun w -> w.Key,w) |> Map.ofList
                        let frozen = snapshot.Works |> List.map (fun w -> w.Key,w) |> Map.ofList
                        require (authoritative=frozen) "snapshot-authoritative-obligation-completeness"
                        require (not decision.Selected.IsEmpty && decision.Selected.Length<=8 && decision.Eligible.Length<=256) "selection-bound"
                        let selectedKeys = decision.Selected |> List.map (fun a -> a.Specification.Requirements.Key)
                        require ((Set.ofList selectedKeys).Count=selectedKeys.Length) "duplicate-work-selection"
                        let mutable preceding = snapshot.SatisfiedDependencies
                        let totals = System.Collections.Generic.Dictionary<string,int64>()
                        let accumulate (resources: Resources) =
                            for KeyValue(key,value) in resources do
                                match value with
                                | Some n when n>=0L ->
                                    let prior = if totals.ContainsKey key then totals.[key] else 0L
                                    if prior>Int64.MaxValue-n then errors.Add "resource-overflow" else totals.[key] <- prior+n
                                | _ -> errors.Add "resource-unknown"
                        accumulate snapshot.RecoveryHeadroom
                        for reservation in snapshot.Reservations do accumulate reservation.Demand
                        for a in decision.Selected do
                            let spec = a.Specification
                            let key = spec.Requirements.Key
                            match Map.tryFind key authoritative with
                            | None -> errors.Add "selected-work-not-authoritative"
                            | Some w ->
                                require (w=spec.Requirements) "candidate-authoritative-obligations"
                                require (Set.isSubset w.Dependencies preceding) "dependency-precedence"
                                preceding <- Set.add key preceding
                                require (w.Deadline |> Option.exists (fun d -> snapshot.Now<d && snapshot.Now>=w.SelectedAt)) "original-deadline"
                                require (a.PolicyVersion=policyVersion && a.AuthoritySha256=authorityHash && a.SnapshotSha256=snapshotDigest snapshot && a.AssignmentSha256=assignmentDigest a) "assignment-identity-or-replay"
                                require (decision.Eligible |> List.contains a) "selection-not-eligible"
                                match snapshot.Profiles |> List.tryFind (fun p -> p.Id=spec.Profile.Id) with
                                | None -> errors.Add "profile-unknown"
                                | Some p ->
                                    require (p=spec.Profile && p.Requested=spec.Requested && p.Resolved=spec.Resolved && p.Observed=spec.Observed) "profile-or-configuration-mutated"
                                    for capability in w.Capabilities do
                                        let observed = match Map.tryFind capability p.Capabilities with Some o -> o.Available && o.Evidence.IsSome | _ -> false
                                        require observed ("capability:"+capability)
                                    require (Set.isSubset w.Tools p.Tools && Set.isSubset w.Skills p.Skills && Set.isSubset w.Context p.Context) "tools-skills-context"
                                    require (Set.isSubset w.AuthorizedEffects p.Effects) "forbidden-effect"
                                    require (Set.isSubset w.RequiredOutputs p.Outputs && Set.isSubset w.RequiredReviews p.Reviews) "mandatory-output-review"
                                    let commands = w.Workspace.Components |> List.collect (fun c -> Set.toList c.Commands) |> Set.ofList
                                    require (Set.isSubset w.RequiredChecks commands) "required-check-source"
                                    require p.RecoverySupported "recovery-capability"
                                    // Independently recompute the per-assignment sum, with overflow checks.
                                    let keys = Set.union (w.Demand |> Map.keys |> Set.ofSeq) (p.AdditionalDemand |> Map.keys |> Set.ofSeq)
                                    for dimension in keys do
                                        let x = Map.tryFind dimension w.Demand |> Option.defaultValue (Some 0L)
                                        let y = Map.tryFind dimension p.AdditionalDemand |> Option.defaultValue (Some 0L)
                                        match x,y with
                                        | Some x,Some y when x>=0L && y>=0L && x<=Int64.MaxValue-y -> require (Map.tryFind dimension spec.Demand=Some(Some(x+y))) "candidate-resource-total"
                                        | _ -> errors.Add "candidate-resource-unknown-overflow"
                                    require ((spec.Demand |> Map.keys |> Set.ofSeq)=keys) "candidate-extra-resource"
                                for r in snapshot.Reservations do require (not (pathsConflict r.Paths w.AllowedPaths)) "held-write-conflict"
                            accumulate spec.Demand
                        for i in 0..decision.Selected.Length-1 do
                            for j in i+1..decision.Selected.Length-1 do
                                require (not (pathsConflict decision.Selected.[i].Specification.Requirements.AllowedPaths decision.Selected.[j].Specification.Requirements.AllowedPaths)) "selected-write-conflict"
                                require (decision.Selected.[i].Specification.Reservation<>decision.Selected.[j].Specification.Reservation) "duplicate-candidate-reservation"
                        for KeyValue(dimension,total) in totals do
                            require (Map.tryFind dimension snapshot.Capacity |> Option.exists (function Some capacity -> total<=capacity | _ -> false)) "aggregate-capacity"
                        require (decision.Selected.Length <= SchedulerReservation.available snapshot.Project - snapshot.Project.RecoveryCapacity) "scheduler-recovery-slots"
                        for a in decision.Selected do require (not (Map.containsKey a.Specification.Reservation snapshot.Project.Reservations)) "reservation-replay"
                        let economics = snapshot.Profiles |> List.map _.Economics
                        let comparable = not economics.IsEmpty && (economics |> List.forall Option.isSome) && (economics |> List.choose id |> List.map (fun e -> e.Currency,e.ProvenanceClass) |> Set.ofList |> Set.count)=1
                        match decision.Preference with
                        | NonEconomicFallback -> require (not comparable) "preference-fallback-unexplained"
                        | Economic(currency,provenance,cost,duration,rework) ->
                            require comparable "preference-incomparable"
                            let sum getter =
                                let mutable total=0L
                                for a in decision.Selected do
                                    match a.Specification.Profile.Economics with
                                    | Some e -> let n=getter e in if n<0L || total>Int64.MaxValue-n then errors.Add "preference-overflow" else total<-total+n
                                    | None -> errors.Add "preference-evidence-missing"
                                total
                            require (cost=sum (fun e -> e.CostMicroUnits) && duration=sum (fun e -> e.DurationMilliseconds) && rework=sum (fun e -> e.ReworkMilliseconds)) "preference-totals"
                            for e in economics |> List.choose id do require (e.Currency=currency && e.ProvenanceClass=provenance) "preference-units"
                        if errors.Count=0 then Ok() else Error(ConflictingEvidence (List.ofSeq errors))
                    with :? ArgumentException as ex -> Error(NeedsDecomposition [ex.Message])

namespace FS.GG.Coordination.Core

open System
open Orchestration
open OrdinaryAssignment

module OrdinaryAssignmentPolicy =
    let private reasons (snapshot: OrdinaryAssignment.Snapshot) (w: Requirements) (p: ExecutionProfile) =
        [ if w.Deadline |> Option.exists (fun d -> snapshot.Now>=d) then "original-deadline-exhausted"
          if snapshot.Now<w.SelectedAt then "selection-time-in-future"
          for capability in w.Capabilities do
              match Map.tryFind capability p.Capabilities with
              | Some observation when observation.Available && observation.Evidence.IsSome -> ()
              | _ -> "capability-not-observed:"+capability
          if not (Set.isSubset w.Tools p.Tools) then "tools-missing"
          if not (Set.isSubset w.Skills p.Skills) then "skills-missing"
          if not (Set.isSubset w.Context p.Context) then "mandatory-context-missing"
          if not (Set.isSubset w.AuthorizedEffects p.Effects) then "effects-unsupported"
          if not (Set.isSubset w.RequiredOutputs p.Outputs) then "outputs-missing"
          if not (Set.isSubset w.RequiredReviews p.Reviews) then "reviews-missing"
          if not p.RecoverySupported then "recovery-unsupported"
          let commands = w.Workspace.Components |> List.collect (fun c -> Set.toList c.Commands) |> Set.ofList
          if not (Set.isSubset w.RequiredChecks commands) then "required-check-not-source-bound" ]

    let private fits (snapshot: OrdinaryAssignment.Snapshot) (selected: OrdinaryAssignment.Assignment list) (assignment: OrdinaryAssignment.Assignment) =
        let specifications = assignment.Specification :: (selected |> List.map _.Specification)
        let demand =
            (snapshot.RecoveryHeadroom, (snapshot.Reservations |> List.map _.Demand) @ (specifications |> List.map _.Demand))
            ||> List.fold addResources
        let resourcesFit = knownNonnegative demand && (demand |> Map.forall (fun key requested ->
            match requested,Map.tryFind key snapshot.Capacity with Some n,Some(Some capacity) -> n<=capacity | _ -> false))
        let slots = SchedulerReservation.available snapshot.Project - snapshot.Project.RecoveryCapacity
        resourcesFit && specifications.Length<=slots
        && not (snapshot.Reservations |> List.exists (fun r -> pathsConflict r.Paths assignment.Specification.Requirements.AllowedPaths))
        && not (selected |> List.exists (fun a -> pathsConflict a.Specification.Requirements.AllowedPaths assignment.Specification.Requirements.AllowedPaths))

    let private make (snapshot: OrdinaryAssignment.Snapshot) snapshotHash (work: Requirements) (profile: ExecutionProfile) =
        let seed = digest (work.Key, work.Attempt, work.Generation, profile.Id, profile.Version)
        bindAssignment {
            PolicyVersion=policyVersion; AuthoritySha256=snapshot.AuthoritySha256; SnapshotSha256=snapshotHash; AssignmentSha256=""
            Specification={ Requirements=work; Profile=profile; Demand=addResources work.Demand profile.AdditionalDemand
                            Reservation=Id.reservation (Guid.ParseExact(seed.Substring(0,32), "N"))
                            Requested=profile.Requested; Resolved=profile.Resolved; Observed=profile.Observed }
            ExperimentEnrollment=None }

    let private comparable (profiles: ExecutionProfile list) =
        let values = profiles |> List.map _.Economics
        if values.IsEmpty || values |> List.exists Option.isNone then false
        else values |> List.choose id |> List.map (fun e -> e.Currency,e.ProvenanceClass) |> Set.ofList |> Set.count = 1

    let select (snapshot: OrdinaryAssignment.Snapshot) : Result<OrdinaryAssignment.Decision, OrdinaryAssignment.Refusal> =
        match validateSnapshot snapshot with
        | Error error -> Error error
        | Ok snapshot ->
            try
                let snapshotHash=snapshotDigest snapshot
                let mutable excluded = []
                let eligible =
                    [ for w in snapshot.Works |> List.sortBy _.Key do
                          for p in snapshot.Profiles |> List.sortBy _.Id do
                              let errors = reasons snapshot w p
                              let assignment = make snapshot snapshotHash w p
                              let errors = if fits snapshot [] assignment then errors else "capacity-or-held-write-conflict"::errors
                              if errors.IsEmpty then yield assignment
                              else excluded <- {WorkKey=w.Key;ProfileId=p.Id;Reasons=errors}::excluded ]
                let economic = comparable snapshot.Profiles
                let ordered = eligible |> List.sortBy (fun a ->
                    let w,p = a.Specification.Requirements,a.Specification.Profile
                    let cost,duration,rework = if economic then let e=p.Economics.Value in e.CostMicroUnits,e.DurationMilliseconds,e.ReworkMilliseconds else 0L,0L,0L
                    -(int64 w.Priority),w.Deadline,cost,duration,rework,w.Key,p.Id)
                let mutable selected = []
                let mutable remaining = ordered
                // Each pass accepts at least one previously unselected work or terminates.
                for _ in 1..8 do
                    let mutable progressed = false
                    let mutable next = []
                    for a in remaining do
                        let w = a.Specification.Requirements
                        let keys = selected |> List.map (fun x -> x.Specification.Requirements.Key) |> Set.ofList
                        if not (Set.contains w.Key keys) && selected.Length<8 then
                            if Set.isSubset w.Dependencies (Set.union keys snapshot.SatisfiedDependencies) && fits snapshot selected a then
                                selected <- selected @ [a]; progressed <- true
                            else next <- a::next
                    remaining <- if progressed then List.rev next else []
                let preference =
                    if not economic || selected.IsEmpty then NonEconomicFallback
                    else
                        let totals = selected |> List.map (fun a -> let e=a.Specification.Profile.Economics.Value in Map.ofList ["cost",Some e.CostMicroUnits;"duration",Some e.DurationMilliseconds;"rework",Some e.ReworkMilliseconds]) |> List.fold addResources Map.empty
                        if not (totals |> Map.forall (fun _ value -> value |> Option.exists (fun n -> n>=0L))) then invalidArg "economics" "preference-total-overflow"
                        let first = selected.Head.Specification.Profile.Economics.Value
                        Economic(first.Currency,first.ProvenanceClass,totals.["cost"].Value,totals.["duration"].Value,totals.["rework"].Value)
                let decision = {Eligible=eligible;Excluded=List.rev excluded;Selected=selected;Preference=preference;Reasons=[if economic then "hard-feasibility;priority-desc;deadline-asc;comparable-economic-vector;stable-identity" else "hard-feasibility;priority-desc;deadline-asc;non-economic-stable-identity-fallback"]}
                // Measure the result using the same bounded encoder without exposing a dispatch type.
                if canonicalSize decision > maximumResultBytes then Error(NeedsDecomposition ["result-bound"])
                elif eligible.IsEmpty then Error(WaitingForCapability ["no-hard-feasible-profile"])
                elif selected.IsEmpty then Error(NeedsEvidence ["dependencies-or-joint-capacity-blocked"])
                else Ok decision
            with :? ArgumentException as ex -> Error(NeedsDecomposition [ex.Message])

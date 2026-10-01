namespace FS.GG.FourD.Typed

type Phase = Idle | Acquired | Validated | Admitted | Effect | Cleanup | Finished | CleanupFailed
type Outcome = NoneObserved | Unknown | Success | Refused

type State = {
    Phase: Phase
    AcquiredIdentity: string option
    ValidatedIdentity: string option
    AdmittedIdentity: string option
    CurrentIdentity: string option
    EffectAcknowledged: bool
    Outcome: Outcome
    Owned: Set<string>
    Closed: Set<string>
    Cancelled: bool
    BudgetRemaining: int
}

type Observation =
    | Acquire of identity: string * resource: string * cost: int
    | Validate of identity: string * cost: int
    | Admit of identity: string * cost: int
    | Invalidate of identity: string * cost: int
    | BeginEffect of resource: string * acknowledged: bool * cost: int
    | ObserveSuccess of cost: int
    | Cancel of cost: int
    | Close of resource: string * cost: int
    | Finish of cost: int

module Policy =
    let initial budget = {
        Phase = Idle; AcquiredIdentity = None; ValidatedIdentity = None
        AdmittedIdentity = None; CurrentIdentity = None; EffectAcknowledged = false
        Outcome = NoneObserved; Owned = Set.empty; Closed = Set.empty
        Cancelled = false; BudgetRemaining = budget
    }

    let private validToken value =
        not (System.String.IsNullOrWhiteSpace value) && value.Length <= 256
        && value |> Seq.forall (fun c -> c >= '!' && c <= '~')

    let private spend cost state =
        if cost <= 0 || cost > state.BudgetRemaining then Error "aggregate-budget-refused"
        else Ok { state with BudgetRemaining = state.BudgetRemaining - cost }

    let reduce state observation =
        let cost = match observation with
                   | Acquire(_,_,c) | Validate(_,c) | Admit(_,c) | Invalidate(_,c)
                   | BeginEffect(_,_,c) | ObserveSuccess c | Cancel c | Close(_,c) | Finish c -> c
        match spend cost state with
        | Error reason -> Error reason
        | Ok next ->
            match observation with
            | Acquire(identity, resource, _) when state.Phase = Idle && validToken identity && validToken resource ->
                Ok { next with Phase = Acquired; AcquiredIdentity = Some identity;
                                    CurrentIdentity = Some identity; Owned = state.Owned.Add resource }
            | Validate(identity, _) when state.Phase = Acquired && Some identity = state.CurrentIdentity ->
                Ok { next with Phase = Validated; ValidatedIdentity = Some identity }
            | Admit(identity, _) when state.Phase = Validated && Some identity = state.ValidatedIdentity
                                                   && Some identity = state.CurrentIdentity ->
                Ok { next with Phase = Admitted; AdmittedIdentity = Some identity }
            | Invalidate(identity, _) when (state.Phase = Validated || state.Phase = Admitted)
                                           && validToken identity && Some identity <> state.CurrentIdentity ->
                Ok { next with Phase = Validated; CurrentIdentity = Some identity; AdmittedIdentity = None }
            | BeginEffect(resource, acknowledged, _) when state.Phase = Admitted
                                                           && state.AdmittedIdentity = state.CurrentIdentity
                                                           && validToken resource ->
                Ok { next with Phase = Effect; EffectAcknowledged = acknowledged;
                                    Outcome = (if acknowledged then Success else Unknown);
                                    Owned = state.Owned.Add resource }
            | ObserveSuccess _ when state.Phase = Effect && state.Outcome = Unknown ->
                Ok { next with EffectAcknowledged = true; Outcome = Success }
            | Cancel _ when state.Phase <> Finished && state.Phase <> CleanupFailed ->
                Ok { next with Phase = Cleanup; Cancelled = true;
                                    Outcome = (if state.Outcome = Success then Success else Unknown) }
            | Close(resource, _) when state.Phase = Cleanup && state.Owned.Contains resource ->
                Ok { next with Closed = state.Closed.Add resource }
            | Finish _ when state.Phase = Cleanup ->
                Ok { next with Phase = (if state.Closed = state.Owned then Finished else CleanupFailed) }
            | _ -> Error "transition-refused"

    let cleanupComplete state = state.Phase = Finished && state.Closed = state.Owned
    let effectEligible state = state.Phase = Admitted && state.AdmittedIdentity = state.CurrentIdentity
    let successful state = state.Outcome = Success && state.EffectAcknowledged && cleanupComplete state

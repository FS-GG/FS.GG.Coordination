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
    | BeginCleanup of cancelled: bool * cost: int
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

    let validToken value =
        not (System.String.IsNullOrWhiteSpace value) && value.Length <= 256
        && value |> Seq.forall (fun c -> c >= '!' && c <= '~')

    let private spend cost state =
        // A measured monotonic interval may be shorter than one whole second.  Zero is
        // therefore a truthful charge; negative costs and renewal remain impossible.
        if cost < 0 || cost > state.BudgetRemaining then Error "aggregate-budget-refused"
        else Ok { state with BudgetRemaining = state.BudgetRemaining - cost }

    /// Composition seam for protocols that share this operation's single aggregate budget.
    let charge cost state = spend cost state

    /// A typed child protocol may close an already-owned lifecycle resource after proving its
    /// own closure. The child event has already been charged through `charge`.
    let closeComposedResource resource state =
        if state.Phase = Cleanup && state.Owned.Contains resource then
            Ok { state with Closed = state.Closed.Add resource }
        else Error "composed-close-refused"

    /// A typed child protocol may acknowledge success only after its result proof is accepted.
    /// The proof event has already been charged through `charge`.
    let acknowledgeComposedSuccess state =
        if (state.Phase = Effect || state.Phase = Cleanup) && state.Outcome = Unknown then
            Ok { state with EffectAcknowledged = true; Outcome = Success }
        else Error "composed-success-refused"

    let beginComposedCleanup cancelled state =
        if state.Phase <> Cleanup && state.Phase <> Finished && state.Phase <> CleanupFailed then
            Ok { state with Phase = Cleanup; Cancelled = cancelled
                            Outcome = (if state.Outcome = Success then Success else Unknown) }
        elif state.Phase = Cleanup then Ok state
        else Error "composed-cleanup-refused"

    /// Signals are sticky lifecycle evidence even when they arrive after cleanup began.
    let markComposedCancellation state =
        if state.Phase = Finished || state.Phase = CleanupFailed then Error "composed-cancel-refused"
        else Ok { state with Cancelled = true; Outcome = (if state.Outcome = Success then Unknown else state.Outcome) }

    let reduce state observation =
        let cost = match observation with
                   | Acquire(_,_,c) | Validate(_,c) | Admit(_,c) | Invalidate(_,c)
                   | BeginEffect(_,_,c) | ObserveSuccess c | BeginCleanup(_,c) | Cancel c | Close(_,c) | Finish c -> c
        match spend cost state with
        | Error reason -> Error reason
        | Ok next ->
            match observation with
            | Acquire(identity, resource, _) when state.Phase = Idle && validToken identity && validToken resource ->
                Ok { next with Phase = Acquired; AcquiredIdentity = Some identity;
                                    CurrentIdentity = Some identity; Owned = state.Owned.Add resource }
            | Validate(identity, _) when state.Phase = Acquired && Some identity = state.CurrentIdentity ->
                Ok { next with Phase = Validated; ValidatedIdentity = Some identity }
            | Admit(identity, _) when state.Phase = Validated && validToken identity && Some identity = state.ValidatedIdentity
                                                   && Some identity = state.CurrentIdentity ->
                Ok { next with Phase = Admitted; AdmittedIdentity = Some identity }
            | Invalidate(identity, _) when (state.Phase = Validated || state.Phase = Admitted)
                                           && validToken identity && Some identity <> state.CurrentIdentity ->
                Ok { next with Phase = Validated; CurrentIdentity = Some identity; AdmittedIdentity = None }
            | BeginEffect(resource, acknowledged, _) when state.Phase = Admitted
                                                           && state.AdmittedIdentity.IsSome
                                                           && state.CurrentIdentity.IsSome
                                                           && state.AdmittedIdentity = state.CurrentIdentity
                                                           && validToken resource ->
                Ok { next with Phase = Effect; EffectAcknowledged = acknowledged;
                                    Outcome = (if acknowledged then Success else Unknown);
                                    Owned = state.Owned.Add resource }
            | ObserveSuccess _ when state.Phase = Effect && state.Outcome = Unknown ->
                Ok { next with EffectAcknowledged = true; Outcome = Success }
            | BeginCleanup(cancelled, _) when state.Phase <> Cleanup && state.Phase <> Finished && state.Phase <> CleanupFailed ->
                Ok { next with Phase = Cleanup; Cancelled = cancelled;
                                    Outcome = (if state.Outcome = Success then Success else Unknown) }
            | Cancel _ when state.Phase = Finished ->
                Ok { next with Cancelled = true }
            | Cancel _ when state.Phase <> CleanupFailed ->
                Ok { next with Phase = Cleanup; Cancelled = true;
                                    Outcome = (if state.Outcome = Success then Success else Unknown) }
            | Close(resource, _) when state.Phase = Cleanup && state.Owned.Contains resource ->
                Ok { next with Closed = state.Closed.Add resource }
            | Finish _ when state.Phase = Cleanup ->
                Ok { next with Phase = (if state.Closed = state.Owned then Finished else CleanupFailed) }
            | _ -> Error "transition-refused"

    let cleanupComplete state = state.Phase = Finished && state.Closed = state.Owned
    let effectEligible state = state.Phase = Admitted && state.AdmittedIdentity.IsSome
                               && state.CurrentIdentity.IsSome && state.AdmittedIdentity = state.CurrentIdentity
    let successful state = not state.Cancelled && state.Outcome = Success && state.EffectAcknowledged && cleanupComplete state

    let validateState state =
        let identitiesValid = [state.AcquiredIdentity;state.ValidatedIdentity;state.AdmittedIdentity;state.CurrentIdentity]
                              |> List.forall (Option.forall validToken)
        let allEqual = state.AcquiredIdentity = state.ValidatedIdentity
                       && state.ValidatedIdentity = state.AdmittedIdentity
                       && state.AdmittedIdentity = state.CurrentIdentity
        let identityChain =
            match state.Phase with
            | Idle -> [state.AcquiredIdentity; state.ValidatedIdentity; state.AdmittedIdentity; state.CurrentIdentity]
                      |> List.forall Option.isNone
            | Acquired -> state.AcquiredIdentity.IsSome && state.AcquiredIdentity = state.CurrentIdentity
                          && state.ValidatedIdentity.IsNone && state.AdmittedIdentity.IsNone
            | Validated -> state.AcquiredIdentity.IsSome && state.ValidatedIdentity.IsSome && state.CurrentIdentity.IsSome
                           && state.AdmittedIdentity.IsNone
            | Admitted | Effect -> state.AcquiredIdentity.IsSome && allEqual
            | Cleanup | Finished | CleanupFailed ->
                state.AcquiredIdentity.IsSome && state.CurrentIdentity.IsSome
                && (match state.AdmittedIdentity with
                    | Some _ -> allEqual
                    | None -> state.Outcome <> Success)
        identitiesValid && identityChain && state.BudgetRemaining >= 0 && state.BudgetRemaining <= 2700
        && Set.isSubset state.Closed state.Owned
        && (state.Outcome <> Success || state.EffectAcknowledged)
        && (not state.Cancelled || not (successful state))
        && (state.Phase <> Finished || state.Closed = state.Owned)

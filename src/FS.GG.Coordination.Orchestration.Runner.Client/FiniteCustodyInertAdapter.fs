namespace FS.GG.Coordination.Orchestration.Runner.Client

open FS.GG.Coordination.Orchestration.Execution

/// The fixture supplies the prepared context directly. There is deliberately no
/// command-line descriptor loader, native provisioner or credential construction.
module FiniteCustodyInertAdapter =
    let productionAvailable = false
    let run (session: FiniteCustodySessionAdapter) = session.Run()

    /// The CLI has no independently supplied fixture context. Refuse before I/O.
    let unavailable () = Error "finite-custody-inert-source-context-unavailable"

    /// No descriptor loader, native launch or sentinel provider is selected here.
    let nativeUnavailable () = Error "finite-custody-native-inert-unavailable"

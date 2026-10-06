namespace FS.GG.Coordination.Cli

/// Pure C2 option syntax. No file read, dispatch, enrollment or admission.
module internal PortableWorkspaceC2RuntimeCommand =
    type Verb = ExecutePhase | RecoverPhase
    type Invocation
    type Diagnostic = { Field: string; Code: string }

    /// Accepts exactly the tail execute-c2-phase/recover-c2-phase --request FILE
    /// after portable-workspace. Verb/option tokens are exact; FILE must not be
    /// null, empty, entirely whitespace or a repeated --request flag.
    /// Does not normalize or open FILE.
    val parse: arguments: string array -> Result<Invocation, Diagnostic list>
    val verb: invocation: Invocation -> Verb
    /// Returns the selected literal token, including significant whitespace.
    val requestPath: invocation: Invocation -> string

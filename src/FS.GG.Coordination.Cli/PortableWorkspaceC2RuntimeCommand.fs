namespace FS.GG.Coordination.Cli

open System

module internal PortableWorkspaceC2RuntimeCommand =
    type Verb = ExecutePhase | RecoverPhase
    type Invocation = private Invocation of Verb * string
    type Diagnostic = { Field: string; Code: string }

    let private refuse field code = Error [ { Field = field; Code = code } ]

    let parse (arguments: string array) : Result<Invocation, Diagnostic list> =
        if isNull arguments || arguments.Length <> 3 then
            refuse "arguments" "exact-tail-required"
        elif arguments[1] <> "--request" then
            refuse "arguments[1]" "request-option-required"
        elif String.IsNullOrWhiteSpace(arguments[2]) || arguments[2] = "--request" then
            refuse "arguments[2]" "literal-request-path-required"
        else
            match arguments[0] with
            | "execute-c2-phase" -> Ok (Invocation (ExecutePhase, arguments[2]))
            | "recover-c2-phase" -> Ok (Invocation (RecoverPhase, arguments[2]))
            | _ -> refuse "arguments[0]" "fixed-phase-verb-required"

    let verb (Invocation (selected, _)) = selected
    let requestPath (Invocation (_, path)) = path

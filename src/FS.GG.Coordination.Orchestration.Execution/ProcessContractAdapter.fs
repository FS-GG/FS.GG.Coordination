namespace FS.GG.Coordination.Orchestration.Execution
open System
open FS.GG.Execution.Contracts

[<RequireQualifiedAccess>]
module ProcessContractAdapter =
    let toBinding (key: ExecutionKey): ExecutionBinding =
        { AssignmentId = key.AssignmentId; AttemptId = key.AttemptId; Generation = key.Generation }
    let ofBinding (binding: ExecutionBinding): ExecutionKey =
        { AssignmentId = binding.AssignmentId; AttemptId = binding.AttemptId; Generation = binding.Generation }
    let bindInvocation (intent: LaunchIntent) invocation ordinal source profile profileDigest =
        let identity =
            { InvocationId = invocation; Execution = Some(toBinding intent.Key); LaunchOrdinal = ordinal
              SourceDigest = source; ProfileId = profile; ProfileDigest = profileDigest }
        let inputValid =
            not (isNull intent.InputDigest) && intent.InputDigest.Length = 64
            && (intent.InputDigest |> Seq.forall Uri.IsHexDigit)
        let errors =
            Validation.identity identity
            @ [ if intent.Schema <> ExecutionProtocol.launchSchema then { Code = "launch-schema-refused"; Field = "intent.schema" }
                if not inputValid then { Code = "input-digest-refused"; Field = "intent.inputDigest" } ]
        // InputDigest stays in the original intent/journal. It is never substituted for SourceDigest.
        // This mapping neither authenticates supplied digests nor creates budget or launch authority.
        if errors.IsEmpty then Ok identity else Error errors

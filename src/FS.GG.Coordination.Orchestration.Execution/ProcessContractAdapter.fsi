namespace FS.GG.Coordination.Orchestration.Execution
open System
open FS.GG.Execution.Contracts
[<RequireQualifiedAccess>]
module ProcessContractAdapter =
    val toBinding: ExecutionKey -> ExecutionBinding
    val ofBinding: ExecutionBinding -> ExecutionKey
    val bindInvocation: LaunchIntent -> Guid -> uint64 -> string -> string -> string -> Result<OperationIdentity,ValidationError list>

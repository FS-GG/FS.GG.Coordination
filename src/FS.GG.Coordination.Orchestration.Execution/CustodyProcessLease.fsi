namespace FS.GG.Coordination.Orchestration.Execution

open System
open System.Threading.Tasks

[<RequireQualifiedAccess>]
type internal CustodyProcessStatus =
    | Live
    | Terminated
    | Unknown

/// Owns an acquired process pidfd. This primitive does not confer execution admission.
type internal CustodyProcessLease =
    member Status: CustodyProcessStatus
    member HasTerminated: bool
    member Terminate: unit -> bool
    member WaitTerminatedAsync: milliseconds: int -> Task<bool>
    interface IDisposable
    static member internal BindHeld: pid: int -> Result<CustodyProcessLease, string>

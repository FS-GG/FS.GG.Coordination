namespace FS.GG.Coordination.Orchestration.Execution

open System
open System.Diagnostics
open System.Threading.Tasks
open System.Runtime.InteropServices
open Microsoft.Win32.SafeHandles

[<Struct; StructLayout(LayoutKind.Sequential)>]
type private CustodyPoll =
    val mutable Descriptor: int
    val mutable Events: int16
    val mutable Returned: int16

module private CustodyProcessNative =
    [<DllImport("libc", SetLastError = true, EntryPoint = "pidfd_open")>]
    extern int pidfd(int pid, uint32 flags)

    [<DllImport("libc", SetLastError = true, EntryPoint = "pidfd_send_signal")>]
    extern int signal(int descriptor, int signal, nativeint info, uint32 flags)

    [<DllImport("libc", SetLastError = true, EntryPoint = "poll")>]
    extern int poll(CustodyPoll& descriptor, unativeint count, int milliseconds)

[<RequireQualifiedAccess>]
type internal CustodyProcessStatus =
    | Live
    | Terminated
    | Unknown

/// An actual process pidfd: readability is the last-thread boundary, never leader exit alone.
type internal CustodyProcessLease private (handle: SafeFileHandle) =
    member _.Status =
        try
            let mutable descriptor = Unchecked.defaultof<CustodyPoll>
            descriptor.Descriptor <- handle.DangerousGetHandle().ToInt32()
            descriptor.Events <- 1s

            match CustodyProcessNative.poll (&descriptor, 1un, 0) with
            | 0 -> CustodyProcessStatus.Live
            | 1 when (descriptor.Returned &&& 1s) <> 0s -> CustodyProcessStatus.Terminated
            | _ -> CustodyProcessStatus.Unknown
        with _ ->
            CustodyProcessStatus.Unknown

    member this.HasTerminated = this.Status = CustodyProcessStatus.Terminated

    member this.Terminate() =
        try
            this.HasTerminated
            || CustodyProcessNative.signal (handle.DangerousGetHandle().ToInt32(), 9, 0n, 0u) = 0
        with _ ->
            false

    member this.WaitTerminatedAsync(milliseconds: int) =
        task {
            let watch = Stopwatch.StartNew()

            while not this.HasTerminated && watch.ElapsedMilliseconds < int64 milliseconds do
                do! Task.Delay 10

            return this.HasTerminated
        }

    interface IDisposable with
        member _.Dispose() = handle.Dispose()

    static member internal BindHeld(pid: int) =
        let descriptor = CustodyProcessNative.pidfd (pid, 0u)

        if descriptor < 0 then
            Error "preparation-custody-pidfd-unavailable"
        else
            let lease = new CustodyProcessLease(new SafeFileHandle(nativeint descriptor, true))

            if lease.Status <> CustodyProcessStatus.Live then
                (lease :> IDisposable).Dispose()
                Error "preparation-custody-bootstrap-ended"
            else
                Ok lease


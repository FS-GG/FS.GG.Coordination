namespace FS.GG.Coordination.Orchestration.Execution

open System
open System.Diagnostics
open System.Threading
open System.Threading.Tasks
open System.IO
open System.Reflection
open System.Runtime.InteropServices
open System.Security.Cryptography
open Microsoft.Win32.SafeHandles

[<Struct; StructLayout(LayoutKind.Sequential)>]
type private CustodyPoll =
    val mutable Descriptor: int
    val mutable Events: int16
    val mutable Returned: int16

module private CustodyNative =
    [<DllImport("libc", SetLastError = true, EntryPoint = "pidfd_open")>]
    extern int pidfd(int pid, uint32 flags)

    [<DllImport("libc", SetLastError = true, EntryPoint = "pidfd_send_signal")>]
    extern int signal(int descriptor, int signal, nativeint info, uint32 flags)

    [<DllImport("libc", SetLastError = true, EntryPoint = "poll")>]
    extern int poll(CustodyPoll& descriptor, unativeint count, int milliseconds)

    [<DllImport("libc", SetLastError = true, EntryPoint = "memfd_create")>]
    extern int create(string name, uint32 flags)

    [<DllImport("libc", SetLastError = true, EntryPoint = "fcntl")>]
    extern int control(int descriptor, int command, int argument)

    [<DllImport("libc", SetLastError = true, EntryPoint = "fchmod")>]
    extern int mode(int descriptor, uint32 mode)

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

            match CustodyNative.poll (&descriptor, 1un, 0) with
            | 0 -> CustodyProcessStatus.Live
            | 1 when (descriptor.Returned &&& 1s) <> 0s -> CustodyProcessStatus.Terminated
            | _ -> CustodyProcessStatus.Unknown
        with _ ->
            CustodyProcessStatus.Unknown

    member this.HasTerminated = this.Status = CustodyProcessStatus.Terminated

    member this.Terminate() =
        try
            this.HasTerminated
            || CustodyNative.signal (handle.DangerousGetHandle().ToInt32(), 9, 0n, 0u) = 0
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
        let descriptor = CustodyNative.pidfd (pid, 0u)

        if descriptor < 0 then
            Error "preparation-custody-pidfd-unavailable"
        else
            let lease = new CustodyProcessLease(new SafeFileHandle(nativeint descriptor, true))

            if lease.Status <> CustodyProcessStatus.Live then
                (lease :> IDisposable).Dispose()
                Error "preparation-custody-bootstrap-ended"
            else
                Ok lease

/// Assembly-owned immutable bytes. This lease alone never grants checker admission.
type internal CustodyResourceLease private (executable: SafeFileHandle, filter: SafeFileHandle, source: SafeFileHandle)
    =
    member _.ExecutablePath =
        $"/proc/{Environment.ProcessId}/fd/{executable.DangerousGetHandle().ToInt32()}"

    member _.Revalidate() =
        CustodyNative.control (executable.DangerousGetHandle().ToInt32(), 1034, 0) = 15
        && CustodyNative.control (filter.DangerousGetHandle().ToInt32(), 1034, 0) = 15
        && CustodyNative.control (source.DangerousGetHandle().ToInt32(), 1034, 0) = 15

    interface IDisposable with
        member _.Dispose() =
            source.Dispose()
            filter.Dispose()
            executable.Dispose()

    static member internal Create(executable, filter, source) =
        new CustodyResourceLease(executable, filter, source)

[<RequireQualifiedAccess>]
module internal CustodyBootstrap =
    let ProfileId = "linux-x64-no-process-descendants/1"

    let BootstrapSha256 =
        "28c5c28bb6279f994606584e9fe930213c8848b7a0cf9366c03e150520974716"

    let FilterSha256 =
        "53281c62a5db55e8b407aa45f11484bd564bc1907b6ffc6bd65acb950cedbbe1"

    let SourceSha256 =
        "4d2105f8223b2eceb89d8fee675b9ee41759c50a36a9d9c86d59a4c5978cd610"

    let private read name expected =
        use resource =
            Assembly.GetExecutingAssembly().GetManifestResourceStream("FSGG.Custody." + name)

        if isNull resource then
            invalidOp "custody-resource-missing"

        use content = new MemoryStream()
        resource.CopyTo content
        let bytes = content.ToArray()

        if Convert.ToHexStringLower(SHA256.HashData bytes) <> expected then
            invalidOp "custody-resource-changed"

        bytes

    let private seal name executable (bytes: byte array) =
        // CLOEXEC | ALLOW_SEALING | EXEC. Unsupported kernels refuse; no pathname fallback.
        let descriptor = CustodyNative.create (name, if executable then 19u else 3u)

        if descriptor < 0 then
            invalidOp "custody-seals-unavailable"

        let handle = new SafeFileHandle(nativeint descriptor, true)

        try
            use stream = new FileStream(handle, FileAccess.ReadWrite, 4096, false)
            stream.Write(bytes, 0, bytes.Length)
            stream.Flush()

            if executable && CustodyNative.mode (descriptor, 320u) <> 0 then
                invalidOp "custody-executable-unavailable"
            // SEAL_SEAL | SHRINK | GROW | WRITE; no writable mapping or descriptor can modify bytes.
            if
                CustodyNative.control (descriptor, 1033, 15) <> 0
                || CustodyNative.control (descriptor, 1034, 0) <> 15
            then
                invalidOp "custody-seals-unavailable"

            stream.Position <- 0L
            let heldHash = SHA256.HashData stream
            let expectedHash = SHA256.HashData bytes

            if
                not (
                    CryptographicOperations.FixedTimeEquals(
                        ReadOnlySpan<byte>(heldHash),
                        ReadOnlySpan<byte>(expectedHash)
                    )
                )
            then
                invalidOp "custody-resource-changed"
            // FileStream owns its supplied handle: retain a duplicate for the lease before disposal.
            let retained = CustodyNative.control (descriptor, 1030, 3)

            if retained < 0 then
                invalidOp "custody-descriptor-unavailable"

            new SafeFileHandle(nativeint retained, true)
        with _ ->
            handle.Dispose()
            reraise ()

    let tryOpen () =
        if
            not (OperatingSystem.IsLinux())
            || RuntimeInformation.ProcessArchitecture <> Architecture.X64
        then
            Error "preparation-custody-unsupported-host"
        else
            try
                let source = read "bootstrap.c" SourceSha256
                let bootstrap = read "bootstrap.linux-x64.bin" BootstrapSha256
                let filter = read "filter.linux-x64.bpf" FilterSha256
                let executable = seal "fsgg-custody-bootstrap" true bootstrap

                try
                    let fixedFilter = seal "fsgg-custody-filter" false filter

                    try
                        let fixedSource = seal "fsgg-custody-source" false source
                        Ok(CustodyResourceLease.Create(executable, fixedFilter, fixedSource))
                    with _ ->
                        fixedFilter.Dispose()
                        reraise ()
                with _ ->
                    executable.Dispose()
                    reraise ()
            with _ ->
                Error "preparation-custody-resource-unavailable"

#nowarn "9" // Documented fixed-layout Linux statx UAPI identity projection.

namespace FS.GG.Coordination.Orchestration.Execution

open System
open System.IO
open System.ComponentModel
open System.Runtime.InteropServices
open Microsoft.Win32.SafeHandles

/// Private Linux source-record I/O. This is syscall ordering, not a qualification capability.
module internal PortableSourceJournalIO =
    // Linux UAPI include/uapi/linux/stat.h: fixed 256-byte statx structure, independent of
    // libc's architecture-dependent struct stat. Only documented returned identity fields
    // are projected here. linux/fcntl.h and asm-generic/fcntl.h define the flags below.
    [<Struct; StructLayout(LayoutKind.Explicit, Size = 256)>]
    type private Statx =
        [<FieldOffset(0)>] val mutable Mask: uint32
        [<FieldOffset(28)>] val mutable Mode: uint16
        [<FieldOffset(32)>] val mutable Inode: uint64
        [<FieldOffset(136)>] val mutable DeviceMajor: uint32
        [<FieldOffset(140)>] val mutable DeviceMinor: uint32

    [<DllImport("libc", SetLastError = true, EntryPoint = "open")>]
    extern int private openDirectory(string path, int flags)
    [<DllImport("libc", SetLastError = true)>]
    extern int private openat(int directory, string name, int flags, uint32 mode)
    [<DllImport("libc", SetLastError = true)>]
    extern int private renameat(int oldDirectory, string oldName, int newDirectory, string newName)
    [<DllImport("libc", SetLastError = true)>]
    extern int private unlinkat(int directory, string name, int flags)
    [<DllImport("libc", SetLastError = true)>]
    extern int private fsync(int descriptor)
    [<DllImport("libc", SetLastError = true)>]
    extern int private statx(int directory, string name, int flags, uint32 mask, Statx& result)

    let private unavailable () = raise (IOException "portable-source-journal-io-unavailable")
    let private syscallFailure () =
        let error = Marshal.GetLastPInvokeError()
        raise (IOException("portable-source-journal-io-unavailable", Win32Exception error))
    let private checkedDescriptor descriptor =
        if descriptor < 0 then syscallFailure ()
        new SafeFileHandle(nativeint descriptor, true)
    let private descriptor (handle: SafeFileHandle) = handle.DangerousGetHandle().ToInt32()
    let private identity directory name flags =
        let mutable value = Unchecked.defaultof<Statx>
        if statx(directory, name, flags, 0x101u, &value) <> 0 then syscallFailure ()
        if value.Mask &&& 0x101u <> 0x101u then unavailable ()
        if value.Mode &&& 0xF000us <> 0x8000us then unavailable ()
        value.DeviceMajor, value.DeviceMinor, value.Inode

    /// Caller keeps the original FileShare.None owner alive throughout this object's lifetime.
    /// No timeout/cancellation detaches an in-flight fsync from that owner.
    type Context(journalRoot: string, path: string, owner: SafeFileHandle) =
        do
            // The open-flag literals below are the x86-64 Linux values (arm64 numbers O_DIRECTORY and
            // O_NOFOLLOW differently), so every other architecture refuses rather than reinterpret them.
            if not (OperatingSystem.IsLinux())
               || RuntimeInformation.ProcessArchitecture <> Architecture.X64 then unavailable ()
        // O_RDONLY | O_DIRECTORY | O_CLOEXEC | O_NOFOLLOW; O_PATH cannot be fsynced.
        let directory = openDirectory(journalRoot, 0x90000 ||| 0x20000) |> checkedDescriptor
        let name = Path.GetFileName path
        let directoryDescriptor = descriptor directory
        let validateOwner () =
            let held = identity (descriptor owner) "" 0x1000 // AT_EMPTY_PATH
            let entry = identity directoryDescriptor (Path.ChangeExtension(name, ".lock")) 0x100 // AT_SYMLINK_NOFOLLOW
            if held <> entry then unavailable ()
        do
            // Construction failure must not leak the already-open directory descriptor.
            try validateOwner ()
            with _ -> directory.Dispose(); reraise ()

        member _.OpenRead() =
            validateOwner ()
            let fd = openat(directoryDescriptor, name, 0xA0800, 0u) // O_RDONLY|CLOEXEC|NOFOLLOW|NONBLOCK
            if fd < 0 && Marshal.GetLastPInvokeError() = 2 then None // ENOENT only
            else
                let handle = checkedDescriptor fd
                try
                    identity fd "" 0x1000 |> ignore
                    Some(new FileStream(handle, FileAccess.Read))
                with _ -> handle.Dispose(); reraise ()

        member _.Write(candidate: byte array, beforeWrite, beforeFlush, beforeRename, beforeSync, afterSync) =
            // Candidate encoding/aggregate bounds are completed by the existing codec first.
            validateOwner ()
            beforeWrite ()
            let temporary = name + "." + Guid.NewGuid().ToString("N") + ".tmp"
            let mutable created = false
            let mutable replaced = false
            let mutable stream: FileStream option = None
            let mutable firstFailure = false
            try
                try
                    // O_WRONLY|O_CREAT|O_EXCL|O_CLOEXEC|O_NOFOLLOW, private0600.
                    let handle = openat(directoryDescriptor, temporary, 0xA00C1, 0x180u) |> checkedDescriptor
                    created <- true
                    let output =
                        try new FileStream(handle, FileAccess.Write)
                        with _ -> handle.Dispose(); reraise ()
                    stream <- Some output
                    output.Write(candidate.AsSpan())
                    beforeFlush ()
                    output.Flush true
                    if output.Length <> int64 candidate.Length then unavailable ()
                    beforeRename ()
                    validateOwner ()
                    if renameat(directoryDescriptor, temporary, directoryDescriptor, name) <> 0 then syscallFailure ()
                    replaced <- true
                    beforeSync ()
                    // Synchronous syscall under the SAME held directory and managed owner lock.
                    // An outer process owner must retain Unknown custody if this syscall blocks.
                    if fsync directoryDescriptor <> 0 then syscallFailure ()
                    afterSync ()
                with _ ->
                    firstFailure <- true
                    reraise ()
            finally
                try
                    match stream with
                    | Some output when firstFailure ->
                        try output.Dispose() with _ -> ()
                    | Some output -> output.Dispose()
                    | None -> ()
                finally
                    // Under stable exclusive private-parent custody, remove only this attempt's
                    // generated name. Arbitrary same-user entry substitution is outside that assumption.
                    // Cleanup never masks the first failure or removes the target/owner lock.
                    if created && not replaced then unlinkat(directoryDescriptor, temporary, 0) |> ignore

        // SafeFileHandle retires the descriptor; underlying close errno is not independently
        // projected. No observed-close or storage-durability qualification is claimed here.
        interface IDisposable with
            member _.Dispose() = directory.Dispose()

namespace FS.GG.FourD.Typed

open System
open System.IO
open System.Security.Cryptography
open System.Text
open System.Runtime.InteropServices
open Microsoft.Win32.SafeHandles

/// An exact, reconstructed source snapshot. Git observations originate in the acquisition
/// adapter; the constructor independently binds the complete acquired file bytes.
type NativeSourceSnapshot = private {
    Identity: string
    Revision: string
    Tree: string
    InventorySha256: string
    FileCount: int
    TotalBytes: int64
}

module private NativeSourceFiles =
    [<Struct; StructLayout(LayoutKind.Sequential, Size=256)>]
    type LinuxStatx =
        val mutable Mask: uint32
        val mutable BlockSize: uint32
        val mutable Attributes: uint64
        val mutable LinkCount: uint32
        val mutable Uid: uint32
        val mutable Gid: uint32
        val mutable Mode: uint16
        val mutable Spare: uint16
        val mutable Inode: uint64
        val mutable Size: uint64

    [<DllImport("libc", EntryPoint="open", SetLastError=true)>]
    extern int openReadOnly(string path, int flags)
    [<DllImport("libc", EntryPoint="statx", SetLastError=true)>]
    extern int statx(SafeFileHandle fd, string path, int flags, uint32 mask, LinuxStatx& result)

    let openRegular path =
        // Linux O_CLOEXEC | O_NOFOLLOW | O_NONBLOCK. A FIFO must refuse without
        // waiting for a writer; fstat via statx binds the type to the held inode.
        let descriptor = openReadOnly(path, 0xA0800)
        if descriptor < 0 then invalidArg "source" "native-source-open-refused"
        let handle = new SafeFileHandle(nativeint descriptor, true)
        try
            let mutable status = Unchecked.defaultof<LinuxStatx>
            if statx(handle, "", 0x1000, 0x201u, &status) <> 0 || status.Mask &&& 0x201u <> 0x201u
               || status.Mode &&& 0xF000us <> 0x8000us then
                invalidArg "source" "native-source-regular-file-refused"
            new FileStream(handle, FileAccess.Read), status.Size
        with _ -> handle.Dispose(); reraise()

module NativeSource =
    open NativeSourceFiles

    let private utf8 = UTF8Encoding(false, true)
    let private maximumBytes = 64L * 1024L * 1024L
    let private digest (bytes: byte array) = Convert.ToHexString(SHA256.HashData bytes).ToLowerInvariant()
    let private hex length (value: string) =
        not (isNull value) && value.Length = length
        && value |> Seq.forall (fun c -> c >= '0' && c <= '9' || c >= 'a' && c <= 'f')

    let private ordinary (path: string) =
        if File.GetAttributes(path).HasFlag FileAttributes.ReparsePoint then
            invalidArg "source" "native-source-link-refused"

    let private regularRoot (root: string) =
        if not (OperatingSystem.IsLinux()) || not (Path.IsPathFullyQualified root)
           || Path.GetFullPath root <> root || root = "/" || not (Directory.Exists root) then
            invalidArg "source" "native-source-root-refused"
        let mutable current = root
        while current <> "/" do
            ordinary current
            current <- Path.GetDirectoryName current

    let private files root =
        let mutable visited = 0
        let rec visit depth directory = seq {
            if depth > 128 then invalidArg "source" "native-source-depth-refused"
            for path in Directory.EnumerateFileSystemEntries directory do
                visited <- visited + 1
                if visited > 16384 then invalidArg "source" "native-source-entry-count-refused"
                ordinary path
                if Directory.Exists path then yield! visit (depth + 1) path
                else
                    let relative = Path.GetRelativePath(root, path)
                    // Linux source-inventory.py hashes UTF-8 names in Unicode scalar order.
                    // Sorting strict UTF-8 bytes preserves that order, including non-BMP names.
                    yield relative, utf8.GetBytes relative, path
        }
        let entries = visit 0 root |> Seq.truncate 8193 |> Seq.toArray
        if entries.Length = 0 || entries.Length > 8192 then invalidArg "source" "native-source-count-refused"
        entries |> Array.sortWith (fun (_, left, _) (_, right, _) ->
            let mutable index, order = 0, 0
            while index < min left.Length right.Length && order = 0 do
                order <- compare left[index] right[index]
                index <- index + 1
            if order = 0 then compare left.Length right.Length else order)

    /// Expected identities must come from the authenticated final packet. This constructor
    /// does not authenticate that packet, establish Git custody, or authorize any effect.
    let acquire expectedRevision expectedTree expectedInventory observedRevision observedTree root =
        if not (hex 40 expectedRevision && hex 40 expectedTree && hex 64 expectedInventory)
           || observedRevision <> expectedRevision || observedTree <> expectedTree then
            invalidArg "source" "native-source-binding-refused"
        regularRoot root
        let entries = files root
        use hash = IncrementalHash.CreateHash HashAlgorithmName.SHA256
        let mutable total = 0L
        let buffer = Array.zeroCreate<byte> 65536
        for _, name, path in entries do
            hash.AppendData name
            hash.AppendData [| 0uy |]
            ordinary path
            let opened, length = openRegular path
            use stream = opened
            if length > uint64 (maximumBytes - total) then invalidArg "source" "native-source-size-refused"
            let mutable count = stream.Read(buffer, 0, buffer.Length)
            while count > 0 do
                total <- total + int64 count
                if total > maximumBytes then invalidArg "source" "native-source-size-refused"
                hash.AppendData(buffer, 0, count)
                count <- stream.Read(buffer, 0, buffer.Length)
            hash.AppendData [| 0uy |]
        let inventory = Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant()
        if inventory <> expectedInventory then invalidArg "source" "native-source-inventory-refused"
        let identity = digest (utf8.GetBytes(String.concat "\000" [expectedRevision; expectedTree; inventory]))
        { Identity=identity; Revision=expectedRevision; Tree=expectedTree; InventorySha256=inventory
          FileCount=entries.Length; TotalBytes=total }

    let projection snapshot =
        {| schema="fsgg.fourd.native-source/1"; accepted=true; identity=snapshot.Identity
           sourceRevision=snapshot.Revision; sourceTree=snapshot.Tree
           inventorySha256=snapshot.InventorySha256; fileCount=snapshot.FileCount; totalBytes=snapshot.TotalBytes |}

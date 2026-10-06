namespace FS.GG.Coordination.Cli

/// Observation/admission source. No member is native-qualified; image proof absent.
/// Internal to the existing CLI; products consume the existing request/result bytes.
module internal PortableWorkspaceC2Admission =
    type Diagnostic = { Field: string; Code: string }
    /// Pure decoded enrollment declaration, never a protected grant.
    type EnrollmentDeclaration
    type EntryContext
    type CapturedRequest
    type TrustedEnrollment
    type CapturedRoles
    type Admission
    type RetiredHandoff

    /// Declared identity only. It cannot be converted into any opaque authority type.
    type RequestIdentity = PortableWorkspaceC2Phase.RequestIdentity

    /// Closed bounded decoder; success confers no protected enrollment/image authority.
    val decodeEnrollmentDeclaration: byte array -> Result<EnrollmentDeclaration, Diagnostic list>

    /// Capture actual entry UTC/monotonic/boot identity once; retain any independently
    /// trusted outer bound. Never starts/replaces the independently enrolled phase.
    /// No workload authority, preparation, output mutation or helper process.
    val beginEntry: unit -> Result<EntryContext, Diagnostic list>

    /// UID1000 first-fixture request spool is exactly
    /// /var/lib/fsgg/portable-workspaces/c2/1000/requests. Physical regular/no-follow,
    /// nlink1, complete bounded read and held-object/current-path rechecks required.
    /// Fixed request spool, no-follow physical read, <=1MiB, <=5s clipped entry
    /// read ceiling; decoded phase elapsed remains charged, never renewed.
    val readRequest:
        EntryContext -> PortableWorkspaceC2RuntimeCommand.Invocation ->
            Result<CapturedRequest, Diagnostic list>

    val requestIdentity: CapturedRequest -> RequestIdentity
    /// Return a defensive copy of the exact verified canonical bytes.
    val requestBytes: CapturedRequest -> byte array

    /// Only local-c2-dotnet-scaffold-fixture-v1 and local-c2-dotnet-compiler-fixture-v1:
    /// respectively /etc/fsgg/portable-workspaces/c2-dotnet-scaffold-v1.json and
    /// /etc/fsgg/portable-workspaces/c2-dotnet-compiler-v1.json, root UID0 parents/file.
    /// Actual effective runtime UID1000; 64KiB enrollment bound. Fixed profile/consumer
    /// triple, original operation fence, normal installed CLI/host and SDK image joins.
    /// Compiled two-ID mapping, root-owned fixed enrollment paths, no env/argv path.
    /// No mkdir, profile preparation, helper process, image load or runtime launch.
    val resolveEnrollment:
        EntryContext -> CapturedRequest -> Result<TrustedEnrollment, Diagnostic list>

    /// Exact independently enrolled role inventory, <=32 files/16MiB captured bytes;
    /// no ambient cache/imports, alias/overlap, link, special or omitted restore member.
    /// Exact closed input inventory captured under the independently enrolled roots.
    /// This type proves only verified input capture, never runtime retirement.
    val captureRoles:
        EntryContext -> TrustedEnrollment -> CapturedRequest ->
            Result<CapturedRoles, Diagnostic list>

    /// Cross-join the independently read enrollment, original operation window,
    /// installed producer identity and captured bytes. No execution in this module.
    val admit:
        EntryContext -> TrustedEnrollment -> CapturedRequest -> CapturedRoles ->
            Result<Admission, Diagnostic list>

    /// Admission is bound to the exact EntryContext generation and independent tuple;
    /// original60s=40work/15cleanup/5callerfinish, all entry/read/setup charged.
    /// Retired handoff requires both helper and container custody, then exact <=16MiB/
    /// 1024-entry inventory and no-replace transfer before deletion; request/result flags
    /// cannot construct it. Result2MiB and combined captured streams1MiB stay unchanged.
    /// No public constructor, FromJson, success boolean or caller-fabricated receipt.
    /// The later existing runtime/custody owner alone can create RetiredHandoff.
    /// No launch method/port or completed-result factory belongs in this window.

namespace FS.GG.Coordination.GitHub

open System

type LedgerCaptureContinuity = Uninitialized | Matched | Drift

type LedgerProtectionCapture =
    { CapturePass: int
      CapturedAt: DateTimeOffset
      Continuity: LedgerCaptureContinuity
      PreviousEvidenceSha256: string option
      RawSetSha256: string
      NormalizedSetSha256: string
      Gaps: string list
      Observation: LedgerProviderObservation }

module LedgerProtectionProviderCodec =
    val decode: bytes: ReadOnlyMemory<byte> -> Result<LedgerProtectionCapture, string list>
    val qualifiedObservation: capture: LedgerProtectionCapture -> Result<LedgerProviderObservation, string list>

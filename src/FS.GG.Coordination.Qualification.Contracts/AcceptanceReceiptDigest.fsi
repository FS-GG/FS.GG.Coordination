namespace FS.GG.Coordination.Qualification.Contracts

open System
open System.Text.Json

type AcceptanceReceiptDigestVerification =
    { StoredDigest: string
      CanonicalDigest: string
      CompatibilityApplied: bool }

[<RequireQualifiedAccess>]
module AcceptanceReceiptDigest =
    val Gs2083UnitId: string
    val Gs2083RawReceiptSha256: string
    val Gs2083LegacyDigest: string
    val Gs2083CanonicalDigest: string

    val canonicalBytesOmitting: omittedRootMember: string -> element: JsonElement -> byte array

    val verify:
        receiptBytes: ReadOnlyMemory<byte> ->
        unitId: string ->
        storedDigest: string ->
        root: JsonElement ->
            Result<AcceptanceReceiptDigestVerification, string>

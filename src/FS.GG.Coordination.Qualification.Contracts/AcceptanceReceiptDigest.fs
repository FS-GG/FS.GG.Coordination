namespace FS.GG.Coordination.Qualification.Contracts

open System
open System.IO
open System.Security.Cryptography
open System.Text.Json

type AcceptanceReceiptDigestVerification =
    { StoredDigest: string
      CanonicalDigest: string
      CompatibilityApplied: bool }

[<RequireQualifiedAccess>]
module AcceptanceReceiptDigest =
    let Gs2083UnitId = "GS2-08.3"

    let Gs2083RawReceiptSha256 =
        "567958c1e1f4805f113f2ac1c020dc9a47e315d9aecbcd2f30061edda2fb5511"

    let Gs2083LegacyDigest =
        "d58c5fa9a6e51731e49df84ec50f471275283e7570867e66488d4ed912fdac15"

    let Gs2083CanonicalDigest =
        "e1b5815b43f5c3cddcfb71858ef1241ee8b90ea5d0e06dc1fdb6d73efbf615e1"

    let private sha256 (bytes: ReadOnlyMemory<byte>) =
        SHA256.HashData(bytes.Span) |> Convert.ToHexString |> _.ToLowerInvariant()

    let canonicalBytesOmitting omittedRootMember (element: JsonElement) =
        use stream = new MemoryStream()
        use writer = new Utf8JsonWriter(stream, JsonWriterOptions(Indented = false))

        let rec write isRoot (value: JsonElement) =
            match value.ValueKind with
            | JsonValueKind.Object ->
                writer.WriteStartObject()

                value.EnumerateObject()
                |> Seq.filter (fun memberValue -> not (isRoot && memberValue.Name = omittedRootMember))
                |> Seq.sortBy _.Name
                |> Seq.iter (fun memberValue ->
                    writer.WritePropertyName(memberValue.Name)
                    write false memberValue.Value)

                writer.WriteEndObject()
            | JsonValueKind.Array ->
                writer.WriteStartArray()
                value.EnumerateArray() |> Seq.iter (write false)
                writer.WriteEndArray()
            | JsonValueKind.String -> writer.WriteStringValue(value.GetString())
            | JsonValueKind.Number -> writer.WriteRawValue(value.GetRawText(), true)
            | JsonValueKind.True -> writer.WriteBooleanValue(true)
            | JsonValueKind.False -> writer.WriteBooleanValue(false)
            | JsonValueKind.Null -> writer.WriteNullValue()
            | _ -> invalidOp "unsupported JSON token"

        write true element
        writer.Flush()
        stream.ToArray()

    let verify (receiptBytes: ReadOnlyMemory<byte>) unitId storedDigest (root: JsonElement) =
        let canonicalDigest =
            canonicalBytesOmitting "digest" root |> ReadOnlyMemory<byte> |> sha256

        let compatibilityApplied =
            unitId = Gs2083UnitId
            && sha256 receiptBytes = Gs2083RawReceiptSha256
            && storedDigest = Gs2083LegacyDigest
            && canonicalDigest = Gs2083CanonicalDigest

        if storedDigest = canonicalDigest || compatibilityApplied then
            Ok
                { StoredDigest = storedDigest
                  CanonicalDigest = canonicalDigest
                  CompatibilityApplied = compatibilityApplied }
        else
            Error $"expected canonical digest {canonicalDigest}"

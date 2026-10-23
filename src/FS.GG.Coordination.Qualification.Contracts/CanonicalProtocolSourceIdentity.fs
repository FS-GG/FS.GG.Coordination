module FS.GG.Coordination.Qualification.Contracts.CanonicalProtocolSourceIdentity

open System
open System.IO
open System.Security.Cryptography
open System.Text.Json

[<Literal>]
let CurrentSha256 =
    "edebdec2a4f80ea8ab94dc18cadb02894efe4b3e989d7e3f5732ed7d5c8ba0f1"

let isCurrent root =
    try
        let protocol = Path.Combine(root, "src/FS.GG.Coordination.Protocol/Protocol.md")

        let actual =
            File.ReadAllBytes protocol
            |> SHA256.HashData
            |> Convert.ToHexString
            |> _.ToLowerInvariant()

        use configuration =
            JsonDocument.Parse(File.ReadAllBytes(Path.Combine(root, "eng/quint-qualification.json")))

        use manifest =
            JsonDocument.Parse(
                File.ReadAllBytes(
                    Path.Combine(root, "src/FS.GG.Coordination.Protocol/Generated/compiled-outputs/manifest.json")
                )
            )

        actual = CurrentSha256
        && configuration.RootElement.GetProperty("source").GetString() = "src/FS.GG.Coordination.Protocol/Protocol.md"
        && configuration.RootElement.GetProperty("sourceSha256").GetString() = CurrentSha256
        && manifest.RootElement.GetProperty("sourceSha256").GetString() = CurrentSha256
    with _ ->
        false

let requireCurrent root =
    if not (isCurrent root) then
        invalidOp "CANONICAL-PROTOCOL-SOURCE: accepted whole source, configuration and compiled manifest must agree"

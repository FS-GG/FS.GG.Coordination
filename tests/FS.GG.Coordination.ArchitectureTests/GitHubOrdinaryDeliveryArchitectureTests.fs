module FS.GG.Coordination.GitHubOrdinaryDeliveryArchitectureTests

open System
open System.IO
open System.Security.Cryptography
open System.Text
open System.Text.Json
open FS.GG.Coordination.Qualification.Contracts
open Xunit

let private root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../.."))
let private read relative = File.ReadAllBytes(Path.Combine(root, relative))

let private commandDigest (command: JsonElement) =
    seq {
        command.GetProperty("executable").GetString()
        yield! command.GetProperty("args").EnumerateArray() |> Seq.map _.GetString()
    }
    |> String.concat "\u0000"
    |> Encoding.UTF8.GetBytes
    |> SHA256.HashData
    |> Convert.ToHexString
    |> _.ToLowerInvariant()

[<Fact>]
let ``GS2-09-9 amendment catalog and unit contract bind exact reviewed authority`` () =
    use amendment = JsonDocument.Parse(read "evidence/github-substrate-v2/roadmap-amendments/GS2-09.9.json")
    let baseRoadmap = amendment.RootElement.GetProperty("baseRoadmap")
    Assert.Equal("7d2db1c32c47b6f9c445a77f9a91510c61a17281", baseRoadmap.GetProperty("revision").GetString())
    Assert.Equal("9c49a0efd1440d8a71130758be39394ae4cdd67f3d10b9cb6cb71998154c1a17", baseRoadmap.GetProperty("sha256").GetString())

    use index = JsonDocument.Parse(read "eng/github-substrate-v2-units.json")
    let unit =
        index.RootElement.GetProperty("units").EnumerateArray()
        |> Seq.find (fun value -> value.GetProperty("id").GetString() = "GS2-09.9")

    let canonical: byte array = AcceptanceReceiptDigest.canonicalBytesOmitting "contractSha256" unit
    let digest = SHA256.HashData(canonical) |> Convert.ToHexString |> _.ToLowerInvariant()
    Assert.Equal(unit.GetProperty("contractSha256").GetString(), digest)
    Assert.Equal<string>([| "Q3"; "Q6" |], unit.GetProperty("qGates").EnumerateArray() |> Seq.map _.GetString() |> Seq.toArray)

    use catalog = JsonDocument.Parse(read "eng/github-substrate-v2-gates.json")
    let commands = catalog.RootElement.GetProperty("commands").EnumerateArray() |> Seq.toList
    for contract in unit.GetProperty("gateContracts").EnumerateArray() do
        let command = commands |> List.find (fun value -> value.GetProperty("id").GetString() = contract.GetProperty("id").GetString())
        Assert.Equal(contract.GetProperty("qGate").GetString(), command.GetProperty("qGate").GetString())
        Assert.Equal(contract.GetProperty("commandSha256").GetString(), commandDigest command)

[<Fact>]
let ``ordinary model correspondence preserves accepted facts without legacy authority`` () =
    use correspondence = JsonDocument.Parse(read "evidence/github-substrate-v2/gs2-09-9/model-correspondence.json")
    let text = correspondence.RootElement.GetRawText()
    Assert.Contains("6ae56a7c9dce52f3ac25e39145b275ed5e8127a1020ee8c65a392b976661c298", text)
    Assert.Contains("4c6a18a3c8cca8ebd59ce040f63f0192c07f9468ad155e7941f676b0b611719c", text)
    Assert.Contains("no legacy review or Done authority", text)

    let source = File.ReadAllText(Path.Combine(root, "src/FS.GG.Coordination.GitHub/OrdinaryDelivery.fs"))
    Assert.DoesNotContain("ReviewPass", source)
    Assert.DoesNotContain("DoneReceipt", source)
    Assert.Contains("OrdinaryIntentPersisted", source)
    Assert.Contains("OrdinaryEffectPending", source)
    Assert.Contains("OrdinarySettled", source)

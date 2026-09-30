namespace FS.GG.Coordination.PortableWorkspace.UnitTests

open System.IO
open FS.GG.Coordination.Cli
open Xunit

type PortableWorkspaceCommandTests() =
    [<Fact>]
    member _.``installed contract artifacts are byte-identical to tracked v1 files``() =
        let rec findRoot (directory: DirectoryInfo) =
            if Directory.Exists(Path.Combine(directory.FullName, "contracts", "portable-workspace", "v1")) then
                directory.FullName
            elif isNull directory.Parent then
                failwith "contracts/portable-workspace/v1 was not found"
            else
                findRoot directory.Parent

        let root = findRoot (DirectoryInfo System.AppContext.BaseDirectory)

        let artifacts =
            [
                "schemas/toolchain-profile.schema.json", "toolchain-profile.schema.json"
                "schemas/command.schema.json", "command.schema.json"
                "schemas/result.schema.json", "result.schema.json"
                "examples/python.json", "examples/python.json"
                "examples/typescript-python.json", "examples/typescript-python.json"
                "examples/result-unknown.json", "examples/result-unknown.json"
            ]

        for embeddedName, trackedName in artifacts do
            let expected =
                Path.Combine(root, "contracts", "portable-workspace", "v1", trackedName)
                |> File.ReadAllBytes

            let actual =
                PortableWorkspaceCommand.embeddedArtifact embeddedName
                |> Option.defaultWith (fun () -> failwith $"missing embedded artifact: {embeddedName}")

            Assert.Equal<byte>(expected, actual)

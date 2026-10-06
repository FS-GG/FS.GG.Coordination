namespace FS.GG.Coordination.PortableWorkspace.UnitTests

open Xunit
open FS.GG.Coordination.Cli

module PortableWorkspaceC2RuntimeCommandTests =
    module C2 = PortableWorkspaceC2RuntimeCommand

    let private get (arguments: string array) =
        match C2.parse arguments with
        | Ok invocation -> invocation
        | Error diagnostics -> failwithf "Unexpected refusal: %A" diagnostics

    let private refuses (arguments: string array) =
        match C2.parse arguments with
        | Ok _ -> failwith "Expected refusal"
        | Error diagnostics ->
            Assert.Equal(1, diagnostics.Length)
            Assert.False(System.String.IsNullOrWhiteSpace diagnostics.Head.Field)
            Assert.False(System.String.IsNullOrWhiteSpace diagnostics.Head.Code)

    let private changed (original: string array) (mutation: string array) =
        Assert.False((original = mutation), "Negative mutation must change the selected tokens")
        mutation

    [<Theory>]
    [<InlineData("execute-c2-phase", false)>]
    [<InlineData("recover-c2-phase", true)>]
    let ``two fixed verbs expose their literal request token`` (name: string) (recover: bool) =
        let invocation = get [| name; "--request"; "request.json" |]
        Assert.Equal((if recover then C2.RecoverPhase else C2.ExecutePhase), C2.verb invocation)
        Assert.Equal("request.json", C2.requestPath invocation)

    [<Theory>]
    [<InlineData("a path/request.json")>]
    [<InlineData("资料/é.json")>]
    [<InlineData("$(untouched);request.json")>]
    [<InlineData("  significant path  ")>]
    [<InlineData("--literal-path-token")>]
    let ``literal path projection performs no normalization or interpretation`` (path: string) =
        let invocation = get [| "execute-c2-phase"; "--request"; path |]
        Assert.Equal(path, C2.requestPath invocation)

    [<Fact>]
    let ``missing extra duplicate and reordered tokens refuse for either verb`` () =
        for name in [ "execute-c2-phase"; "recover-c2-phase" ] do
            let original = [| name; "--request"; "request.json" |]
            let mutations =
                [ [||]; [| name |]; [| name; "--request" |]
                  [| name; "request.json"; "--request" |]
                  [| name; "--request"; "request.json"; "extra" |]
                  [| name; "--request"; "request.json"; "--request"; "other.json" |]
                  [| name; "--request"; "--request"; "request.json" |]
                  [| name; "--request"; "--request" |]
                  [| name; "--request"; "request.json"; name |]
                  [| name; "--"; "--request"; "request.json" |] ]
            for mutation in mutations do
                mutation |> changed original |> refuses

    [<Fact>]
    let ``unknown null and whitespace syntax tokens refuse without trimming`` () =
        let original = [| "execute-c2-phase"; "--request"; "request.json" |]
        for name in [ null; ""; " "; "execute"; "Execute-c2-phase"; " execute-c2-phase"; "recover-c2-phase " ] do
            [| name; "--request"; "request.json" |] |> changed original |> refuses
        for flag in [ null; ""; " "; "--unknown"; " --request"; "--request "; "--request=request.json" ] do
            [| "execute-c2-phase"; flag; "request.json" |] |> changed original |> refuses
        for path in [ null; ""; " "; "\t\r\n" ] do
            [| "execute-c2-phase"; "--request"; path |] |> changed original |> refuses
        refuses null

    [<Fact>]
    let ``caller array mutation cannot change an already parsed invocation`` () =
        let arguments = [| "execute-c2-phase"; "--request"; "original.json" |]
        let invocation = get arguments
        arguments[0] <- "recover-c2-phase"
        arguments[2] <- "changed.json"
        Assert.Equal(C2.ExecutePhase, C2.verb invocation)
        Assert.Equal("original.json", C2.requestPath invocation)

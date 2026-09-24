namespace FS.GG.Coordination.Cli

open System.Text.Json
open FS.GG.Coordination.GitHub

[<RequireQualifiedAccess>]
module OrdinarySettlementCommand =
    let private usage = "ordinary-settlement execute"

    /// Milestone 03 installs the provider in the trusted two-job workflow. Keeping
    /// provider injection internal prevents command-line plan or credential substitution.
    let runWith (provider: IOrdinarySettlementCommandProvider option) arguments =
        match arguments |> Array.toList with
        | [ "execute" ] ->
            match OrdinarySettlementCommandContract.executeOneAttempt provider with
            | Ok outcome ->
                printfn "{\"result\":%s}" (JsonSerializer.Serialize(string outcome))
                0
            | Error failures ->
                eprintfn "ordinary-settlement-refused:%s" (failures |> List.map string |> String.concat ",")
                3
        | _ ->
            eprintfn "%s" usage
            2

    let run arguments = runWith None arguments

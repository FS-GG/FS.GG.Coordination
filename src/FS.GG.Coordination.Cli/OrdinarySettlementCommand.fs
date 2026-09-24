namespace FS.GG.Coordination.Cli

open System.Text.Json
open FS.GG.Coordination.GitHub

[<RequireQualifiedAccess>]
module OrdinarySettlementCommand =
    let private usage = "ordinary-settlement <execute|rehearse>"

    let outcomeExitCode =
        function
        | SettlementSucceeded _
        | SettlementAlreadyComplete _ -> 0
        | SettlementPending _
        | SettlementInterrupted _ -> 3

    let private execute provider =
        match OrdinarySettlementCommandContract.executeOneAttempt provider with
        | Ok outcome ->
            let exitCode = outcomeExitCode outcome
            if exitCode = 0 then printfn "{\"result\":%s}" (JsonSerializer.Serialize(string outcome))
            else eprintfn "ordinary-settlement-incomplete:%s" (string outcome)
            exitCode
        | Error failures ->
            eprintfn "ordinary-settlement-refused:%s" (failures |> List.map string |> String.concat ",")
            3

    /// Milestone 03 installs the provider in the trusted two-job workflow. Keeping
    /// provider injection internal prevents command-line plan or credential substitution.
    let runWith (provider: IOrdinarySettlementCommandProvider option) arguments =
        match arguments |> Array.toList with
        | [ "execute" ] -> execute provider
        | _ ->
            eprintfn "%s" usage
            2

    let run arguments =
        match arguments |> Array.toList with
        | [ "execute" ] -> execute (InstalledOrdinarySettlementProvider.tryCreate ())
        | [ "rehearse" ] -> execute (InstalledOrdinarySettlementProvider.tryCreateRehearsal ())
        | _ ->
            eprintfn "%s" usage
            2

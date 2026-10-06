module ResponsesInstallationTests

open System
open System.IO

// Native execution belongs to the parent-selected qualifier. These cases require
// no credentials or successful installation and must refuse before any effects.
[<EntryPoint>]
let main _ =
    let previous = Console.Error
    use captured = new StringWriter()
    Console.SetError captured
    try
        let forbidden =
            [ "--executable"; "--codex-home"; "--provider"; "--model"; "--effort";
              "--count-endpoint"; "--generation-endpoint"; "--input-token-limit";
              "--output-token-limit"; "--whole-milliseconds"; "--installation-version" ]
        for option in forbidden do
            captured.GetStringBuilder().Clear() |> ignore
            let code = TelemetryHostManager.main [| "install-responses-collector"; option; "caller-override" |]
            if code <> 3 || not (captured.ToString().Contains("unknown option " + option, StringComparison.Ordinal)) then
                failwith ("closed Responses writer accepted override " + option)
        captured.GetStringBuilder().Clear() |> ignore
        let duplicate = TelemetryHostManager.main [| "install-responses-collector"; "--host-config"; "/unselected"; "--host-config"; "/other" |]
        if duplicate <> 3 || not (captured.ToString().Contains("distinct --name value", StringComparison.Ordinal)) then
            failwith "duplicate installation option was not refused"
        printfn "PASS 12 closed Responses installation argument cases; positive installation qualification is separate"
        0
    finally
        Console.SetError previous

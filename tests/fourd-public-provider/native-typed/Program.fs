module FourDNativeSourceTests

[<EntryPoint>]
let main argv =
    if argv.Length <> 0 then 2
    else
        try
            FS.GG.FourD.Tests.NativeProtocolTests.run()
            0
        with error ->
            eprintfn "native correspondence failed: %s" error.Message
            2

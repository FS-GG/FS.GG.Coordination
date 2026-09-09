namespace FS.GG.Coordination.Cli

open System
open System.Diagnostics
open System.IO
open System.Text.Json
open FS.GG.Coordination.GitHub

[<RequireQualifiedAccess>]
module LedgerProtectionCommand =
    let private options (values:string list) =
        let rec loop (state:Map<string,string>) (remaining:string list) =
            match remaining with
            | [] -> Ok state
            | key :: value :: tail when key.StartsWith("--") && not (Map.containsKey key state) -> loop (Map.add key value state) tail
            | value :: _ -> Error("unknown or repeated argument: " + value)
        loop Map.empty values

    let private required (name:string) (values:Map<string,string>) = match Map.tryFind name values with Some value -> Ok value | None -> Error("missing "+name)
    let private text (name:string) (root: JsonElement) = root.GetProperty(name).GetString()

    let private readInput path =
        use document = JsonDocument.Parse(File.ReadAllBytes path)
        let root = document.RootElement
        { RepositoryId = root.GetProperty("repositoryId").GetInt64()
          Repository = text "repository" root
          FleetId = text "fleetId" root
          Ref = text "ref" root
          Tag = text "tag" root
          ManifestSha256 = text "manifestSha256" root
          TrustAnchorSha256 = text "trustAnchorSha256" root
          SourceSha256 = text "sourceSha256" root
          DesiredPolicySha256 = text "desiredPolicySha256" root
          FirstCaptureSha256 = text "firstCaptureSha256" root
          SecondCaptureSha256 = text "secondCaptureSha256" root
          AuthorizationKeyId = text "authorizationKeyId" root
          AuthorizationKeySha256 = text "authorizationKeySha256" root
          CutoverAppId = root.GetProperty("cutoverAppId").GetInt64()
          CutoverInstallationId = root.GetProperty("cutoverInstallationId").GetInt64()
          ControlIssueNumber = root.GetProperty("controlIssueNumber").GetInt64()
          ExpectedRef = ExpectedAbsent
          CreatedAt = root.GetProperty("createdAt").GetDateTimeOffset()
          AuthorName = text "authorName" root
          AuthorEmail = text "authorEmail" root }

    let private readAuthority path keyPath =
        use document = JsonDocument.Parse(File.ReadAllBytes path)
        let root = document.RootElement
        { KeyId = text "keyId" root
          PublicKeyPem = File.ReadAllText keyPath
          PublicKeySha256 = text "publicKeySha256" root
          Payload = Convert.FromBase64String(text "payloadBase64" root)
          Signature = Convert.FromBase64String(text "signatureBase64" root)
          AuthorizedAt = root.GetProperty("authorizedAt").GetDateTimeOffset()
          ExpiresAt = root.GetProperty("expiresAt").GetDateTimeOffset() }

    let private planCommand values =
        match required "--input" values, required "--authorization" values, required "--public-key" values, required "--output" values with
        | Ok inputPath, Ok authorityPath, Ok keyPath, Ok outputPath ->
            try
                let input = readInput inputPath
                match LedgerInitializationAdapter.plan DateTimeOffset.UtcNow (readAuthority authorityPath keyPath) input with
                | Error findings -> eprintfn "%s" (String.concat "," findings); 3
                | Ok plan ->
                    let objects =
                        [ plan.Event; plan.Head; plan.Tree; plan.Commit ]
                        |> List.map (fun value -> {| kind = value.Kind; oid = value.Oid; bytesBase64 = Convert.ToBase64String value.Bytes |})
                    let envelope =
                        {| schema = "fsgg.github-ledger-initialization-plan/1"; inputSha256 = plan.InputSha256
                           authorizationSha256 = plan.AuthorizationSha256; ref = plan.Ref; tag = plan.Tag; expectedRef = "absent"
                           commitOid = plan.Commit.Oid; operationOrder = plan.OperationOrder; seal = plan.Seal; objects = objects |}
                    File.WriteAllBytes(outputPath, JsonSerializer.SerializeToUtf8Bytes envelope)
                    0
            with error -> eprintfn "%s" error.Message; 3
        | _ -> eprintfn "plan requires --input --authorization --public-key --output"; 2

    let private delegated mode values credentialFd =
        match required "--plan" values, required "--transport" values with
        | Ok planPath, Ok transport when File.Exists planPath ->
            match credentialFd with
            | Some fd when fd < 3 -> eprintfn "credential fd must be >=3"; 2
            | _ ->
                let start = ProcessStartInfo(transport)
                start.UseShellExecute <- false
                start.ArgumentList.Add mode
                start.ArgumentList.Add "--plan"
                start.ArgumentList.Add(Path.GetFullPath planPath)
                credentialFd |> Option.iter (fun fd -> start.ArgumentList.Add "--credential-fd"; start.ArgumentList.Add(string fd))
                use child = Process.Start start
                child.WaitForExit()
                child.ExitCode
        | _ -> eprintfn "%s requires existing --plan and --transport" mode; 2

    let run arguments =
        match arguments |> Array.toList with
        | "initialize" :: "plan" :: rest -> match options rest with Ok values -> planCommand values | Error error -> eprintfn "%s" error; 2
        | "initialize" :: "apply" :: rest ->
            match options rest with
            | Error error -> eprintfn "%s" error; 2
            | Ok values -> match required "--credential-fd" values with Ok value -> delegated "apply" values (Some(Int32.Parse value)) | Error error -> eprintfn "%s" error; 2
        | "initialize" :: "verify" :: rest -> match options rest with Ok values -> delegated "verify" values None | Error error -> eprintfn "%s" error; 2
        | _ -> eprintfn "ledger-protection initialize <plan|apply|verify>"; 2

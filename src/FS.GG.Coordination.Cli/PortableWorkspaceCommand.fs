namespace FS.GG.Coordination.Cli

open System
open System.Globalization
open System.IO
open System.IO.Compression
open System.Text.Json
open FS.GG.Coordination.Orchestration.Execution

[<RequireQualifiedAccess>]
module PortableWorkspaceCommand =
    let private inflate value =
        use input = new MemoryStream(Convert.FromBase64String value)
        use gzip = new GZipStream(input, CompressionMode.Decompress)
        use output = new MemoryStream()
        gzip.CopyTo output
        output.ToArray()

    // These deterministic gzip literals are generated from the tracked v1 files with `gzip -n -9`.
    // Keeping them in the assembly makes schema and example export independent of a source checkout.
    let private artifacts =
        Map
            [
                "schemas/toolchain-profile.schema.json",
                "H4sIAAAAAAACA7VW23LaMBB95ytcl4eQYgvTJm14SdNbJjOZaSbkqYRkFFs2Sm3JkWRaSvj3SrJ8wXGAzqTAYGt9dHb37KJl2bEsu8v9GUqgPbLsmRApHwFwzylxcrNLWQQCBkMBhoPhwPGGwOD7ejMO6htD7kYR8CkRDPqCg5QyAe9i5Pyi7CdPoY/A3AOC0tifQUyclNEQx8g1vpTfnFdgESPF/G3snp5aBY9V8lgliWVIrLln9i5SvZXe3SNf5DYYBFhgSmB8wWiKmMCIS0wIY440gKGHDDOkspnYRYaWbbjPArVgaI45ViFadhnI2Jd8ysJpxnx0WcM8ZDDGIUbBWQIjjfFpklKCiOCGPMhUhJYd4wRL41THktZDXEqLtJVFWioWwoXKMORR5JaRuE90BZ5trfo5QZWJ5ugyFCqK16AboJADWccSWubZivRpRgRiFbwhxVb6hlAaX9SMC4ZJpMWBQnpRj+2byYnzAzp/Bs7RrTNden1v+GHVrQgbOj9DmMDf54hEYibNw4PDhgv3zUc+g9I+mkg30Amny8N3dSe10hVFqfUaZAwutBdMzgRKFMjLnRbLt8N+sQsbU0HT0rTbW9ZsXGtcrPs0hiTKTMep4kgBvkiILyjTMZZ9ohYyI7a4oJgU7Wd4W5qwiH57CxlkGciO+CfB7tAbe8evQE9+uft7x6ObR9C7vnblRy7AY7eXPwEGcS1fvckNmLrLQX94cFCrb1GGUpr1pP+9Qq2VmSOme37aX+deF3ubxBXPc62OSdnqXqP15W9HWggV+WZEskQHGEOBuE7r/OTq6/hK3e3bU2ul3rVo1/Wqd88LK3aX4ViLpuPapliO3iia5tmIiGUemxFQugzlaNuAWler07zLr7VTWY+AliPlJVST13t52hK0qAT8P9IVbrbqUmRuJt6LJS57HCdZcimHE07QGMkhGXBz7qsH3zORZuLTQqayQYt2ltakUsplcHP0uZqHrc523FzI0zES2RpY/QEojoRdJ+W0unVvR0DOzYGcm+/XR1rufJdTdvA48RSpYlZMR6tejamZzi6RNugUm8q9s+r8BSMdRy+XCgAA"
                "schemas/command.schema.json",
                "H4sIAAAAAAACA5VUwY7TMBC99yui0ANITdxECHZ740LFDbFISNsNK9ceZ10SO9hOl1Ly72s7aZIWFsIp0/G8mXnv2T3OgiCca/IAJQ5XQfhgTKVXCO20FFGbjqXKEVWYGZQu02WUpKirX3gwp2Mg03GeIyKFUZgYjSqpDN4WED1K9U1XmADaJ/a8LLGgcTfBTWu7GW4KcP3e38TrdXBCBz066KDBPukQh8oD5HYHxLQ5TCk3XApcfFSyAmU4aFvDcKHBFyj4XnMFbvNNeGIThF3vD9T94BTKShoQ5NAm+h1uiG3qMpWSjBfQHnc/PsGea+74BKGWtSJnGfhR2S2BfrG9WCEfx2fMjoI1CFDYdCkKmBZc+GGOSHuQeQ7VmNrRZmyuN/LoyAhtnDJM53ncLx93JFESBs2ihQ28PfIkqTaKi9yvJlWJfbO65i1bbAwo4VJfN8voGkcsO141UR+/nhAnaTMftjgX3G8yV8DciBdoToFpZGf35Rd2/LN+MGtqaW/OHwFE1sJKMKAu3H5GyjPh3kW3OPpp5biPrBqLJL0aC/LsZZm2z+V9moYiuNa+fopQ/f2cQtbS7O3PjukQfD4Fq9+Cu7u4Dd80tyNlhrfgB4OoS/+UtzUv/PU0YO++/drl/Bfbh8Iw8fHO+iTgEGbj+19JAcL8lbOtbdq/PJcbHh2n/+d1NoTx/QpZ55fW+bdj50+uTOj7cvlrk7imXibb6bp5Ne+2nTWzJ0YXkC3hBQAA"
                "schemas/result.schema.json",
                "H4sIAAAAAAACA9VWUW/TMBB+36+IQh9AWpImsMH6NiGYkJBAg6d1BXnJJfNI7GA73UbJf8fnuEmapqJMQ4iX9mzdd/fd57tTVgeO405kfA0FcWeOe61UKWdBcCM585prn4ssSARJVRBNo6kXRoH1PzRgmvSBqfSzLIg5U4LESgYlF4pc5eDdcvFNliSGYBkGAmSVK98mwGRNMEVVDhju7Sf/7MxZg50W7DRIZxlawH1p/PnVDcSquSNJQhXljOQfBS9BKApS+6Qkl2AcBHyvqADkPXfXtThuzIuCsORdggfMmOb89hyWVFLk57gpsBjOgIEgyl5pTJmDguRU4RHuqHrNE0Cb6MSp1uAcUhCIdBcme9kntdI3+q59gBWGZFJhTanMMr+t3G8qD0LXqQ8bVEfYANdaSCUoywxhLgpiYlUVNWWVRCkQDK++zKfeCfHSxepV7bX2iz3sMKonHYstpQyZiYAUszwJJgmkUndExXTmDjYUcz9UX+9RhKIFSEWKssO0jzIKoDpBBuLNkibmkVrY9vuN4hu1t+EgBBftC4+06m8addCmse0qXZ0kmTEFKHGP44GHBBShuWx6zOQb6TMr4U4pEqTf5djRVQVl74Fl6lpfh3gmd+35KIxMiI7bRpArznMgrFXJEFpT3/DsVNLhN+R5Hu2W7mF8ndqSaf7xt252G8rSTSndOWgbY3XqXRDvhx6WRWf6X2eBt1hND8PoZX941g2+R9yn05/zEINiZIx0Uj/rReoafx+OJoad6cUq6ozPa2O2ZVxe+o15XF/0Eg8HyKTnDD5gb83H3/RPOl8XpUyLL0le4RIdtrZjXTa35zfGb1nTzw1wQxZL2sXHR5+RaSiolIPJHnOrmMnUc3MWrTaD5fBfSLNjcP6yUsMYs0dURwDBL4x95bFUXLvIDPYBqyU6OraaNRUOy/+HFVoqj1YhLsyD+uAXjvvYak0KAAA="
                "examples/python.json",
                "H4sIAAAAAAACA6VSy07DMBC88xk+N4++S04IcekJ1B4RB9fZOAuObfxoqar+O3YcpZU4kpN3xzOz482FWNZCR0lFGst5flLmy2rKIHdKCdZSlJk2qkEBxZRMyHDe1oGgz65VMmtBCJUdI2rgiBaVDGAsR7E9Uxp6i4zzIhESO9yyyhsGuxu1nM7mi+VqvXmkB1ZDE+58eyqwQai3HeVRibfM5KiKpJi0nmxLZ8tVRf/5BT+mOq0kSGdJ9X4hGONSrQMiqOQ+zTAmiDlR8hc0wJwy54BZw4pEGN+RVIMQG4lHMEPmeT5d5CW5TkgwNec3hb33hRw8irvHTmVQBetu3b4KswXSrdlXE0KNw4ayO+DUAghyvX7066x9xO6MUuuP09AerD7D0iTEpGmb/dQ6Th0zCOwwjd/RH+x8t/PSYQd7YErWNuYty6AyoK/eae+ez0E7/jnlYrNcr8KAD7+Uh3Y0ngIAAA=="
                "examples/typescript-python.json",
                "H4sIAAAAAAACA6VSu3LbMBDs8xmoRYp6Wlbl8aRxlYxdZlKA4BG8mHgED8kcDf89RwqUZHuSJqx4u9i9XQxOzIsGFGd7Vnsp86Nxr95yAXkwphUNR51ZZ2psYb5gM5b+nyoSHKHMPLgDCsgOA+nggB6NJm5F48XrRRgL44ZMyjm8cWVbyKxpO9maQCe9iU7A81VeLJar9WZ7t7vnpaigpjO/I2+xRqieFJeDm2yEy9HMz66T24Nv+HKz3fP//GijMMoaDTp4tv9xYjh0rp3RAXRFdMu1jOcoobPghUMbUm3U8is6EMG4jnjvxPxGeblatk+22lRAxAFcuoDlOt/lBetnjPa77rvBMcaJlRHb2yDZGSBT8OEWH2dKScJbeJxnjLuANRfvqAvW97OUq+Ti9VNb24WGQv616VX0uai4iK9dV/li/c+uyfBj1Ql+33RCx7Hvf45vtopD1atjgj46TnBy/EXPUsNQq3TmSE894xazCe2HlQrPURV/QxXVc9QBFbyAMLoigt0XBRkl9lsMNobHjuyJWhTr3eZuSxm//AExmmGWhgMAAA=="
                "examples/result-unknown.json",
                "H4sIAAAAAAACA02RMU/DMBCFd35F5LkOTdICzYYYEGvVic2xL6lVx1ed7RRU5b9zbgv0Tdbdu+fv7LMIeg+jEq3owzCUJ6RDOCoNJUFILj5WYiE0jqPy5sOwq7pJ1izZsOSKJdd34pGc0zs8bWGywaLnyVWO6sFreAcPpOK1vLlecHQQwbxGrtTL+kkuN7JZ7qq6bVbt+ql8ftlUdfPJXviy8Q0NiPYsQlSRDyL5g8eT5y6BCpfYI6GGEKTDEGUHPRJIbppO6YOYF0JRtL3ScQs9UIa6zxttCNYP93ke5QRkewtG/s5K7ALQBCYHAhFSDtEXOOYEnfKOElPkBUH+U45Mpobs2u2h+HMWN2ehMTlTeIxFx22m6pwNezBFTzgWJhEXuDFZk8nLC2ek71wVbaQEC2EgKutCBuKnyE6GEzag4xWNpOT5D8Q8zw8/KW8xyAMCAAA="
            ]
        |> Map.map (fun _ value -> inflate value)

    let embeddedArtifact name = Map.tryFind name artifacts

    let private usage () =
        eprintfn "workspace-contract schema export {toolchain-profile|command|result}"
        eprintfn "workspace-contract example export {python|typescript-python|result-unknown}"
        eprintfn "workspace-contract {validate|digest} --schema SCHEMA --file FILE"

        eprintfn
            "workspace-contract operation prepare --profile FILE --command FILE --workspace-scope SCOPE --workflow-revision N --fence-generation N --observed-at UTC --now UTC"

        2

    let private artifactPath category name =
        match category, name with
        | "schema", "toolchain-profile" -> Some "schemas/toolchain-profile.schema.json"
        | "schema", "command" -> Some "schemas/command.schema.json"
        | "schema", "result" -> Some "schemas/result.schema.json"
        | "example", "python" -> Some "examples/python.json"
        | "example", "typescript-python" -> Some "examples/typescript-python.json"
        | "example", "result-unknown" -> Some "examples/result-unknown.json"
        | _ -> None

    let private writeArtifact category name =
        match artifactPath category name |> Option.bind embeddedArtifact with
        | None -> usage ()
        | Some bytes ->
            let length =
                if category = "example" && bytes.Length > 0 && bytes[bytes.Length - 1] = byte '\n' then
                    bytes.Length - 1
                else
                    bytes.Length

            Console.OpenStandardOutput().Write(bytes, 0, length)
            0

    let private commonOptions arguments =
        let rec loop schema file remaining =
            match remaining with
            | [] -> Ok(schema, file)
            | "--schema" :: value :: tail when Option.isNone schema -> loop (Some value) file tail
            | "--file" :: value :: tail when Option.isNone file -> loop schema (Some value) tail
            | token :: _ -> Error $"unknown or repeated argument: %s{token}"

        loop None None arguments

    let private runCanonical command arguments =
        match commonOptions arguments with
        | Error message ->
            eprintfn "%s" message
            usage ()
        | Ok(Some schema, Some path) when File.Exists path ->
            match PortableWorkspaceContract.kindForSchema schema with
            | Error reason ->
                eprintfn "%s" reason
                3
            | Ok kind ->
                let bytes = File.ReadAllBytes path

                match PortableWorkspaceContract.canonicalBytes kind bytes with
                | Error reason ->
                    eprintfn "%s" reason
                    3
                | Ok canonical when command = "digest" ->
                    printfn "%s" (PortableWorkspaceContract.digest canonical)
                    0
                | Ok canonical ->
                    printfn
                        "%s"
                        (JsonSerializer.Serialize
                            {|
                                schema = "fsgg.workspace.validation-result/1"
                                outcome = "passed"
                                documentSchema = schema
                                bytes = canonical.Length
                                sha256 = PortableWorkspaceContract.digest canonical
                            |})

                    0
        | Ok(_, Some path) when not (File.Exists path) ->
            eprintfn "portable workspace document does not exist: %s" path
            2
        | _ -> usage ()

    let private parseCounter value =
        if String.IsNullOrEmpty value || (value.Length > 1 && value[0] = '0') then
            Error "portable-counter-refused"
        else
            match UInt64.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture) with
            | true, parsed -> Ok parsed
            | _ -> Error "portable-counter-refused"

    let private parseInstant value =
        match
            DateTimeOffset.TryParseExact(
                value,
                "yyyy-MM-dd'T'HH:mm:ss.ffffff'Z'",
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal ||| DateTimeStyles.AdjustToUniversal
            )
        with
        | true, parsed when parsed.ToString("yyyy-MM-dd'T'HH:mm:ss.ffffff'Z'", CultureInfo.InvariantCulture) = value ->
            Ok parsed
        | _ -> Error "portable-timestamp-refused"

    let private prepareOptions (arguments: string list) =
        let rec loop (values: Map<string, string>) (remaining: string list) =
            match remaining with
            | [] -> Ok values
            | key :: value :: tail when
                key.StartsWith("--", StringComparison.Ordinal)
                && not (Map.containsKey key values)
                ->
                loop (Map.add key value values) tail
            | token :: _ -> Error $"unknown or repeated argument: %s{token}"

        loop Map.empty arguments

    let private prepare arguments =
        match prepareOptions arguments with
        | Error message ->
            eprintfn "%s" message
            usage ()
        | Ok values ->
            let required =
                [
                    "--profile"
                    "--command"
                    "--workspace-scope"
                    "--workflow-revision"
                    "--fence-generation"
                    "--observed-at"
                    "--now"
                ]

            if
                values.Count <> required.Length
                || required |> List.exists (fun key -> not (Map.containsKey key values))
            then
                usage ()
            elif not (File.Exists values["--profile"]) || not (File.Exists values["--command"]) then
                eprintfn "portable workspace profile or command does not exist"
                2
            else
                match
                    PortableWorkspaceContract.parseProfile (File.ReadAllBytes values["--profile"]),
                    PortableWorkspaceContract.parseCommand (File.ReadAllBytes values["--command"]),
                    parseCounter values["--workflow-revision"],
                    parseCounter values["--fence-generation"],
                    parseInstant values["--observed-at"],
                    parseInstant values["--now"]
                with
                | Ok profile, Ok command, Ok revision, Ok generation, Ok observedAt, Ok now ->
                    let authority =
                        {
                            WorkspaceScope = values["--workspace-scope"]
                            WorkflowRevision = revision
                            FenceGeneration = generation
                            ObservedAt = observedAt
                        }

                    match PortableWorkspaceAdapter.prepare now authority profile command with
                    | Error reason ->
                        eprintfn "%s" reason
                        3
                    | Ok operation ->
                        printfn
                            "%s"
                            (JsonSerializer.Serialize
                                {|
                                    schema = "fsgg.workspace.prepared-operation/1"
                                    outcome = "prepared"
                                    executionAuthority = false
                                    commandId = operation.CommandId.ToString("D").ToLowerInvariant()
                                    idempotencyId = operation.IdempotencyId
                                    workingDirectory = operation.WorkingDirectory
                                    entryPoint = operation.EntryPoint
                                    deadline =
                                        operation.Deadline.ToString(
                                            "yyyy-MM-dd'T'HH:mm:ss.ffffff'Z'",
                                            CultureInfo.InvariantCulture
                                        )
                                    maximumRuntimeSeconds =
                                        operation.MaximumRuntimeSeconds.ToString(CultureInfo.InvariantCulture)
                                    maximumOutputBytes =
                                        operation.MaximumOutputBytes.ToString(CultureInfo.InvariantCulture)
                                |})

                        0
                | Error reason, _, _, _, _, _
                | _, Error reason, _, _, _, _
                | _, _, Error reason, _, _, _
                | _, _, _, Error reason, _, _
                | _, _, _, _, Error reason, _
                | _, _, _, _, _, Error reason ->
                    eprintfn "%s" reason
                    3

    let run arguments =
        match arguments |> Array.toList with
        | "schema" :: "export" :: [ name ] -> writeArtifact "schema" name
        | "example" :: "export" :: [ name ] -> writeArtifact "example" name
        | "validate" :: rest -> runCanonical "validate" rest
        | "digest" :: rest -> runCanonical "digest" rest
        | "operation" :: "prepare" :: rest -> prepare rest
        | _ -> usage ()

open System
open System.Diagnostics
open System.IO
open System.Text.Json
open System.Xml.Linq

let private projectName (path: string) = Path.GetFileNameWithoutExtension path

let private normalizePath (path: string) = Path.GetFullPath path

let private valuesNamed (name: string) (document: XDocument) =
    document.Descendants(XName.Get name)
    |> Seq.choose (fun element ->
        let includeAttribute = element.Attribute(XName.Get "Include")
        if isNull includeAttribute then None else Some includeAttribute.Value)
    |> Seq.toList

let private sdkValues (document: XDocument) =
    let attributeValue attributeName (element: XElement) =
        let attribute = element.Attribute(XName.Get attributeName)
        if isNull attribute then None else Some attribute.Value

    [ if not (isNull document.Root) then
          yield! attributeValue "Sdk" document.Root |> Option.toList
      if not (isNull document.Root) then
          for element in document.Root.Descendants() do
              yield! attributeValue "Sdk" element |> Option.toList
              if element.Name.LocalName = "Sdk" then
                  yield! attributeValue "Name" element |> Option.toList ]

type private EvaluatedProject =
    { OutputType: string
      ProjectReferences: string list
      RuntimeReferences: string list }

let private evaluateProject (path: string) =
    let startInfo = ProcessStartInfo("dotnet")
    startInfo.ArgumentList.Add("msbuild")
    startInfo.ArgumentList.Add(path)
    startInfo.ArgumentList.Add("-nologo")
    startInfo.ArgumentList.Add("-verbosity:quiet")
    startInfo.ArgumentList.Add("-getProperty:OutputType")
    startInfo.ArgumentList.Add("-getItem:ProjectReference,PackageReference,Reference,FrameworkReference")
    startInfo.RedirectStandardOutput <- true
    startInfo.RedirectStandardError <- true
    startInfo.UseShellExecute <- false

    use childProcess = Process.Start startInfo
    let outputTask = childProcess.StandardOutput.ReadToEndAsync()
    let errorTask = childProcess.StandardError.ReadToEndAsync()
    childProcess.WaitForExit()
    let output = outputTask.Result
    let error = errorTask.Result.Trim()

    if childProcess.ExitCode <> 0 then
        Error(if String.IsNullOrWhiteSpace error then "dotnet-msbuild-evaluation-failed" else error)
    else
        try
            use result = JsonDocument.Parse output
            let properties = result.RootElement.GetProperty("Properties")
            let items = result.RootElement.GetProperty("Items")

            let itemValues (name: string) (property: string) =
                items.GetProperty(name).EnumerateArray()
                |> Seq.choose (fun item ->
                    let mutable value = Unchecked.defaultof<JsonElement>

                    if item.TryGetProperty(property, &value) then
                        value.GetString() |> Option.ofObj
                    else
                        None)
                |> Seq.toList

            Ok
                { OutputType = properties.GetProperty("OutputType").GetString() |> Option.ofObj |> Option.defaultValue ""
                  ProjectReferences = itemValues "ProjectReference" "FullPath"
                  RuntimeReferences =
                    [ yield! itemValues "PackageReference" "Identity"
                      yield! itemValues "Reference" "Identity"
                      yield! itemValues "FrameworkReference" "Identity" ] }
        with exceptionValue ->
            Error $"invalid-msbuild-evaluation-json:{exceptionValue.Message}"

let private allowedDependencies =
    Map.ofList
        [ "FS.GG.Coordination.Protocol", Set.empty
          "FS.GG.Coordination.Core", Set.singleton "FS.GG.Coordination.Protocol"
          "FS.GG.Coordination.GitHub",
          Set.ofList [ "FS.GG.Coordination.Protocol"; "FS.GG.Coordination.Core" ]
          "FS.GG.Coordination.Cli",
          Set.ofList
              [ "FS.GG.Coordination.Protocol"
                "FS.GG.Coordination.Core"
                "FS.GG.Coordination.GitHub" ]
          "FS.GG.Coordination.App",
          Set.ofList
              [ "FS.GG.Coordination.Protocol"
                "FS.GG.Coordination.Core"
                "FS.GG.Coordination.GitHub" ]
          "FS.GG.Coordination.Qualification.Contracts",
          Set.singleton "FS.GG.Coordination.Protocol" ]

let private containsAny (needles: string list) (value: string) =
    needles
    |> List.exists (fun needle -> value.Contains(needle, StringComparison.OrdinalIgnoreCase))

let private violation project dependency rule =
    $"DEPENDENCY_POLICY_VIOLATION project={project} dependency={dependency} rule={rule}"

let private inspectProject (path: string) =
    let name = projectName path
    let document = XDocument.Load path
    let evaluated = evaluateProject path

    let edgeViolations =
        [ yield!
              valuesNamed "ProjectReference" document
              |> List.map (fun reference ->
                  reference.Replace('\\', Path.DirectorySeparatorChar).Replace('/', Path.DirectorySeparatorChar)
                  |> fun relativePath -> Path.Combine(Path.GetDirectoryName path, relativePath)
                  |> normalizePath)
          match evaluated with
          | Ok project -> yield! project.ProjectReferences |> List.map normalizePath
          | Error _ -> () ]
        |> List.distinct
        |> List.choose (fun reference ->
            let dependency = projectName reference

            match Map.tryFind name allowedDependencies with
            | None -> Some(violation name dependency "unknown-production-project")
            | Some allowed when not (Set.contains dependency allowed) ->
                Some(violation name dependency "project-edge-not-allowed")
            | Some _ -> None)

    let transportViolations =
        if name = "FS.GG.Coordination.Protocol" || name = "FS.GG.Coordination.Core" then
            let referenceViolations =
                [ yield! valuesNamed "PackageReference" document
                  yield! valuesNamed "Reference" document
                  yield! valuesNamed "FrameworkReference" document
                  match evaluated with
                  | Ok project -> yield! project.RuntimeReferences
                  | Error _ -> () ]
                |> List.distinct
                |> List.filter (containsAny [ "GitHub"; "Octokit"; "AspNetCore"; "System.Net.Http"; "Extensions.Http"; "HttpClient" ])
                |> List.map (fun dependency -> violation name dependency "transport-reference-in-pure-layer")

            let sdkViolations =
                sdkValues document
                |> List.filter (containsAny [ "Microsoft.NET.Sdk.Web"; "Microsoft.NET.Sdk.Razor" ])
                |> List.map (fun dependency -> violation name dependency "transport-sdk-in-pure-layer")

            referenceViolations @ sdkViolations
        else
            []

    let hostViolations =
        if name = "FS.GG.Coordination.App" then
            let outputTypes =
                [ yield! document.Descendants(XName.Get "OutputType") |> Seq.map _.Value
                  match evaluated with
                  | Ok project -> yield project.OutputType
                  | Error _ -> () ]
                |> List.distinct

            let runtimeReferences =
                [ yield! valuesNamed "PackageReference" document
                  yield! valuesNamed "Reference" document
                  yield! valuesNamed "FrameworkReference" document
                  match evaluated with
                  | Ok project -> yield! project.RuntimeReferences
                  | Error _ -> () ]
                |> List.distinct

            [ for sdk in sdkValues document do
                  if containsAny [ "Microsoft.NET.Sdk.Web"; "Microsoft.NET.Sdk.Razor"; "Microsoft.NET.Sdk.Worker" ] sdk then
                      violation name sdk "app-host-runtime-sdk-forbidden"
              for outputType in outputTypes do
                  if outputType.Equals("Exe", StringComparison.OrdinalIgnoreCase)
                     || outputType.Equals("WinExe", StringComparison.OrdinalIgnoreCase) then
                      violation name $"OutputType={outputType}" "app-host-must-not-be-executable"
              for runtimeReference in runtimeReferences do
                  if containsAny [ "AspNetCore"; "Kestrel"; "Webhook"; "Extensions.Hosting" ] runtimeReference then
                      violation name runtimeReference "app-host-runtime-binding-forbidden" ]
        else
            []

    let evaluationViolations =
        match evaluated with
        | Ok _ -> []
        | Error detail -> [ violation name detail "project-evaluation-failed" ]

    evaluationViolations @ edgeViolations @ transportViolations @ hostViolations

let private parseRoot (arguments: string array) =
    let args = arguments |> Array.filter ((<>) "--")

    args
    |> Array.tryFindIndex ((=) "--root")
    |> Option.bind (fun index -> args |> Array.tryItem (index + 1))
    |> Option.defaultValue (Directory.GetCurrentDirectory())
    |> normalizePath

let root = parseRoot (fsi.CommandLineArgs |> Array.skip 1)
let sourceRoot = Path.Combine(root, "src")

if not (Directory.Exists sourceRoot) then
    eprintfn "%s" (violation "repository" sourceRoot "source-root-missing")
    exit 1

let projects =
    Directory.EnumerateFiles(sourceRoot, "*.fsproj", SearchOption.AllDirectories)
    |> Seq.filter (fun path ->
        not (path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}"))
        && not (path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")))
    |> Seq.sort
    |> Seq.toList

let names = projects |> List.map projectName |> Set.ofList
let required = allowedDependencies |> Map.keys |> Set.ofSeq

let missingViolations =
    Set.difference required names
    |> Seq.map (fun name -> violation name "missing" "required-project-missing")
    |> Seq.toList

let unknownViolations =
    Set.difference names required
    |> Seq.map (fun name -> violation name "undeclared" "unknown-production-project")
    |> Seq.toList

let violations =
    [ yield! missingViolations
      yield! unknownViolations
      for project in projects do
          yield! inspectProject project ]

if List.isEmpty violations then
    printfn "DEPENDENCY_POLICY_OK projects=%d" projects.Length
    exit 0
else
    violations |> List.iter (eprintfn "%s")
    exit 1

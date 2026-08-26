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

let private elementValuesNamed (name: string) (document: XDocument) =
    document.Descendants(XName.Get name)
    |> Seq.map (fun element -> element.Value.Trim())
    |> Seq.filter (String.IsNullOrWhiteSpace >> not)
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

type private EvaluatedPackageReference =
    { Identity: string
      Version: string
      VersionOverride: string
      GeneratePathProperty: string }

type private EvaluatedProject =
    { OutputType: string
      ProjectReferences: string list
      PackageReferences: EvaluatedPackageReference list
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

            let itemValue (property: string) (item: JsonElement) =
                let mutable value = Unchecked.defaultof<JsonElement>

                if item.TryGetProperty(property, &value) then
                    value.GetString() |> Option.ofObj |> Option.defaultValue ""
                else
                    ""

            let packageReferences =
                items.GetProperty("PackageReference").EnumerateArray()
                |> Seq.map (fun item ->
                    { Identity = itemValue "Identity" item
                      Version = itemValue "Version" item
                      VersionOverride = itemValue "VersionOverride" item
                      GeneratePathProperty = itemValue "GeneratePathProperty" item })
                |> Seq.filter (fun reference -> not (String.IsNullOrWhiteSpace reference.Identity))
                |> Seq.toList

            Ok
                { OutputType = properties.GetProperty("OutputType").GetString() |> Option.ofObj |> Option.defaultValue ""
                  ProjectReferences = itemValues "ProjectReference" "FullPath"
                  PackageReferences = packageReferences
                  RuntimeReferences =
                    [ yield! packageReferences |> List.map _.Identity
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

let private allowedPureLayerRuntimeReferences =
    Set.ofList [ "FSharp.Core"; "Microsoft.NETCore.App" ]

let private containsAny (needles: string list) (value: string) =
    needles
    |> List.exists (fun needle -> value.Contains(needle, StringComparison.OrdinalIgnoreCase))

let private violation project dependency rule =
    $"DEPENDENCY_POLICY_VIOLATION project={project} dependency={dependency} rule={rule}"

let private inspectProject (path: string) =
    let name = projectName path
    let document = XDocument.Load path
    let evaluated = evaluateProject path

    let packageReferences =
        document.Descendants(XName.Get "PackageReference")
        |> Seq.toList

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

            if dependency.StartsWith("FS.GG.SDD", StringComparison.OrdinalIgnoreCase) then
                Some(violation name dependency "published-kernel-source-project-reference-forbidden")
            else
                match Map.tryFind name allowedDependencies with
                | None -> Some(violation name dependency "unknown-production-project")
                | Some allowed when not (Set.contains dependency allowed) ->
                    Some(violation name dependency "project-edge-not-allowed")
                | Some _ -> None)

    let publishedKernelViolations =
        let references =
            packageReferences
            |> List.filter (fun element ->
                let includeAttribute = element.Attribute(XName.Get "Include")
                not (isNull includeAttribute)
                && includeAttribute.Value.Equals("FS.GG.SDD.Artifacts", StringComparison.OrdinalIgnoreCase))

        let evaluatedReferences =
            match evaluated with
            | Ok project ->
                project.PackageReferences
                |> List.filter (fun reference ->
                    reference.Identity.Equals("FS.GG.SDD.Artifacts", StringComparison.OrdinalIgnoreCase))
            | Error _ -> []

        let hasReference = not (List.isEmpty references) || not (List.isEmpty evaluatedReferences)

        [ if name = "FS.GG.Coordination.Qualification.Contracts" && not hasReference then
              yield violation name "FS.GG.SDD.Artifacts" "published-kernel-package-reference-required"

          if name <> "FS.GG.Coordination.Qualification.Contracts" && hasReference then
              yield violation name "FS.GG.SDD.Artifacts" "published-kernel-consumer-not-allowed"

          for reference in references do
              for attributeName in [ "Version"; "VersionOverride" ] do
                  let attribute = reference.Attribute(XName.Get attributeName)
                  if not (isNull attribute) then
                      yield violation name attribute.Value "published-kernel-version-must-be-central"

              if name = "FS.GG.Coordination.Qualification.Contracts" then
                  let generatePath = reference.Attribute(XName.Get "GeneratePathProperty")
                  if isNull generatePath || not (generatePath.Value.Equals("true", StringComparison.OrdinalIgnoreCase)) then
                      yield violation name "FS.GG.SDD.Artifacts" "published-kernel-path-property-required"

          for reference in evaluatedReferences do
              for value in [ reference.Version; reference.VersionOverride ] do
                  if not (String.IsNullOrWhiteSpace value) then
                      yield violation name value "published-kernel-version-must-be-central"

              if name = "FS.GG.Coordination.Qualification.Contracts"
                 && not (reference.GeneratePathProperty.Equals("true", StringComparison.OrdinalIgnoreCase)) then
                  yield violation name "FS.GG.SDD.Artifacts" "published-kernel-path-property-required" ]

    let packageSourceViolations =
        [ "RestoreSources"; "RestoreAdditionalProjectSources" ]
        |> List.collect (fun property -> elementValuesNamed property document)
        |> List.collect (fun value -> value.Split(';', StringSplitOptions.RemoveEmptyEntries) |> Array.toList)
        |> List.map _.Trim()
        |> List.filter (fun value ->
            not (value.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            && not (value.StartsWith("http://", StringComparison.OrdinalIgnoreCase)))
        |> List.map (fun value -> violation name value "checkout-relative-package-source-forbidden")

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
                |> List.filter (fun dependency -> not (Set.contains dependency allowedPureLayerRuntimeReferences))
                |> List.map (fun dependency -> violation name dependency "runtime-reference-not-allowed-in-pure-layer")

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

    evaluationViolations @ edgeViolations @ publishedKernelViolations @ packageSourceViolations @ transportViolations @ hostViolations

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

let centralPinViolations =
    let path = Path.Combine(root, "Directory.Packages.local.props")
    if not (File.Exists path) then
        [ violation "repository" "FS.GG.SDD.Artifacts" "published-kernel-central-pin-missing" ]
    else
        let document = XDocument.Load path
        let pins =
            document.Descendants(XName.Get "PackageVersion")
            |> Seq.choose (fun element ->
                let includeAttribute = element.Attribute(XName.Get "Include")
                let versionAttribute = element.Attribute(XName.Get "Version")
                if isNull includeAttribute || isNull versionAttribute then None
                elif includeAttribute.Value.Equals("FS.GG.SDD.Artifacts", StringComparison.OrdinalIgnoreCase) then
                    Some versionAttribute.Value
                else None)
            |> Seq.toList

        match pins with
        | [ "[1.4.0]" ] -> []
        | [] -> [ violation "repository" "FS.GG.SDD.Artifacts" "published-kernel-central-pin-missing" ]
        | values -> [ violation "repository" (String.concat "," values) "published-kernel-central-pin-must-equal-1.4.0" ]

let producerCopyViolations =
    let forbiddenNames =
        Set.ofList
            [ "q1-identity-manifest.json"
              "QuintCompiler.fs"
              "QuintCompiler.fsi"
              "QuintProfile.fs"
              "QuintProfile.fsi"
              "QuintSource.fs"
              "QuintSource.fsi"
              "QuintReplay.fs"
              "QuintReplay.fsi" ]

    Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
    |> Seq.filter (fun path ->
        not (path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}"))
        && not (path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}"))
        && not (path.Contains($"{Path.DirectorySeparatorChar}.git{Path.DirectorySeparatorChar}"))
        && not (path.Contains($"{Path.DirectorySeparatorChar}docs{Path.DirectorySeparatorChar}"))
        && not (path.Contains($"{Path.DirectorySeparatorChar}work{Path.DirectorySeparatorChar}"))
        && not (path.Contains($"{Path.DirectorySeparatorChar}readiness{Path.DirectorySeparatorChar}"))
        && not (path.Contains($"{Path.DirectorySeparatorChar}evidence{Path.DirectorySeparatorChar}"))
        && not (path.Contains($"{Path.DirectorySeparatorChar}tests{Path.DirectorySeparatorChar}")))
    |> Seq.choose (fun path ->
        let fileName = Path.GetFileName path
        if Path.GetExtension(path).Equals(".qnt", StringComparison.OrdinalIgnoreCase)
           || Set.contains fileName forbiddenNames
           || (fileName = "main.go" && path.Contains($"{Path.DirectorySeparatorChar}quint{Path.DirectorySeparatorChar}lmt{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase)) then
            Some(violation "repository" (Path.GetRelativePath(root, path)) "published-kernel-producer-copy-forbidden")
        else None)
    |> Seq.toList

let repositoryPackageSourceViolations =
    Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
    |> Seq.filter (fun path ->
        not (path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}"))
        && not (path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}"))
        && Set.contains (Path.GetExtension(path).ToLowerInvariant()) (Set.ofList [ ".props"; ".targets"; ".fsproj" ]))
    |> Seq.collect (fun path ->
        let document = XDocument.Load path
        [ "RestoreSources"; "RestoreAdditionalProjectSources" ]
        |> Seq.collect (fun property -> elementValuesNamed property document)
        |> Seq.collect (fun value -> value.Split(';', StringSplitOptions.RemoveEmptyEntries)))
    |> Seq.map _.Trim()
    |> Seq.filter (fun value ->
        not (value.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        && not (value.StartsWith("http://", StringComparison.OrdinalIgnoreCase)))
    |> Seq.distinct
    |> Seq.map (fun value -> violation "repository" value "checkout-relative-package-source-forbidden")
    |> Seq.toList

let nugetConfigPackageSourceViolations =
    Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
    |> Seq.filter (fun path ->
        Path.GetFileName(path).Equals("NuGet.Config", StringComparison.OrdinalIgnoreCase)
        && not (path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}"))
        && not (path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}"))
        && not (path.Contains($"{Path.DirectorySeparatorChar}.git{Path.DirectorySeparatorChar}")))
    |> Seq.collect (fun path ->
        let document = XDocument.Load path
        document.Descendants()
        |> Seq.filter (fun element -> element.Name.LocalName.Equals("packageSources", StringComparison.OrdinalIgnoreCase))
        |> Seq.collect _.Elements()
        |> Seq.filter (fun element -> element.Name.LocalName.Equals("add", StringComparison.OrdinalIgnoreCase))
        |> Seq.choose (fun element ->
            element.Attributes()
            |> Seq.tryFind (fun attribute -> attribute.Name.LocalName.Equals("value", StringComparison.OrdinalIgnoreCase))
            |> Option.map _.Value))
    |> Seq.map _.Trim()
    |> Seq.filter (String.IsNullOrWhiteSpace >> not)
    |> Seq.filter (fun value ->
        not (value.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        && not (value.StartsWith("http://", StringComparison.OrdinalIgnoreCase)))
    |> Seq.distinct
    |> Seq.map (fun value -> violation "repository" value "checkout-relative-package-source-forbidden")
    |> Seq.toList

let missingViolations =
    Set.difference required names
    |> Seq.map (fun name -> violation name "missing" "required-project-missing")
    |> Seq.toList

let unknownViolations =
    Set.difference names required
    |> Seq.map (fun name -> violation name "undeclared" "unknown-production-project")
    |> Seq.toList

let violations =
    [ yield! centralPinViolations
      yield! producerCopyViolations
      yield! repositoryPackageSourceViolations
      yield! nugetConfigPackageSourceViolations
      yield! missingViolations
      yield! unknownViolations
      for project in projects do
          yield! inspectProject project ]

if List.isEmpty violations then
    printfn "DEPENDENCY_POLICY_OK projects=%d" projects.Length
    exit 0
else
    violations |> List.iter (eprintfn "%s")
    exit 1

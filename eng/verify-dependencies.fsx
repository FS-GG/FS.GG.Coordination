open System
open System.IO
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

    let edgeViolations =
        valuesNamed "ProjectReference" document
        |> List.choose (fun reference ->
            let dependency =
                reference.Replace('\\', Path.DirectorySeparatorChar).Replace('/', Path.DirectorySeparatorChar)
                |> fun relativePath -> Path.Combine(Path.GetDirectoryName path, relativePath)
                |> normalizePath
                |> projectName

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
                  yield! valuesNamed "FrameworkReference" document ]
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
            let root = document.Root
            let sdk = if isNull root then "" else string (root.Attribute(XName.Get "Sdk"))
            let outputTypes = document.Descendants(XName.Get "OutputType") |> Seq.map _.Value |> Seq.toList
            let packages = valuesNamed "PackageReference" document

            [ if sdk.Contains("Web", StringComparison.OrdinalIgnoreCase) then
                  violation name sdk "app-host-must-not-use-web-sdk"
              if outputTypes |> List.exists (fun value -> value.Equals("Exe", StringComparison.OrdinalIgnoreCase)) then
                  violation name "OutputType=Exe" "app-host-must-not-be-executable"
              for package in packages do
                  if containsAny [ "AspNet"; "Kestrel"; "Webhook" ] package then
                      violation name package "app-host-runtime-binding-forbidden" ]
        else
            []

    edgeViolations @ transportViolations @ hostViolations

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

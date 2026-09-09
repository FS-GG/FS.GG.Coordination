namespace FS.GG.Coordination.Qualification.Contracts

open System
open System.Text.RegularExpressions

type GitHubV1WriterCommand =
    { Name: string
      Writes: string }

type GitHubV1WriterSource =
    { Path: string
      Disposition: string
      SinkKinds: string list
      Sha256: string
      NonWriterJustification: string option }

type GitHubV1WriterCensusSnapshot =
    { Schema: string
      ProducerRevision: string
      ProducerTree: string
      RoadmapRevision: string
      RoadmapSha256: string
      AcceptedEpochReceiptSha256: string
      SourceBaseRevision: string
      Q0EvidenceSha256: string
      Q0CorpusSha256: string
      CensusSha256: string
      CommandContractSha256: string
      CheckerSha256: string
      FixtureRunnerSha256: string
      Commands: GitHubV1WriterCommand list
      Sources: GitHubV1WriterSource list }

type GitHubV1WriterCensusFinding =
    { Code: string
      Subject: string
      Message: string }

type GitHubV1WriterCensusControl =
    | SchemaBinding
    | AcceptedEpochPrerequisite
    | RoadmapBinding
    | HistoricalQ0Binding
    | ProducerSourceBinding
    | CensusByteBinding
    | CandidateCommandContract
    | CheckerBinding
    | CompleteCommandRoots
    | CompleteSourcePopulation
    | StableUniqueOrdering
    | ExactWriteClassification
    | SourceIdentity
    | SinkDisposition
    | UnknownCommandRefusal
    | DynamicWriterRefusal
    | NoFenceClaim
    | NoReceiverClaim

type GitHubV1WriterCensusControlResult =
    { Control: GitHubV1WriterCensusControl
      BaselineGreen: bool
      MutationRed: bool }

module GitHubV1WriterCensusQualification =
    let requiredControls =
        [ SchemaBinding; AcceptedEpochPrerequisite; RoadmapBinding; HistoricalQ0Binding
          ProducerSourceBinding; CensusByteBinding; CandidateCommandContract; CheckerBinding
          CompleteCommandRoots; CompleteSourcePopulation; StableUniqueOrdering
          ExactWriteClassification; SourceIdentity; SinkDisposition; UnknownCommandRefusal
          DynamicWriterRefusal; NoFenceClaim; NoReceiverClaim ]

    let controlId = function
        | SchemaBinding -> "schema-binding"
        | AcceptedEpochPrerequisite -> "accepted-epoch-prerequisite"
        | RoadmapBinding -> "roadmap-binding"
        | HistoricalQ0Binding -> "historical-q0-binding"
        | ProducerSourceBinding -> "producer-source-binding"
        | CensusByteBinding -> "census-byte-binding"
        | CandidateCommandContract -> "candidate-command-contract"
        | CheckerBinding -> "checker-binding"
        | CompleteCommandRoots -> "complete-command-roots"
        | CompleteSourcePopulation -> "complete-source-population"
        | StableUniqueOrdering -> "stable-unique-ordering"
        | ExactWriteClassification -> "exact-write-classification"
        | SourceIdentity -> "source-identity"
        | SinkDisposition -> "sink-disposition"
        | UnknownCommandRefusal -> "unknown-command-refusal"
        | DynamicWriterRefusal -> "dynamic-writer-refusal"
        | NoFenceClaim -> "no-fence-claim"
        | NoReceiverClaim -> "no-receiver-claim"

    let private sha256 value =
        not (isNull value) && Regex.IsMatch(value, "^[0-9a-f]{64}$", RegexOptions.CultureInvariant)

    let private revision value =
        not (isNull value) && Regex.IsMatch(value, "^[0-9a-f]{40}$", RegexOptions.CultureInvariant)

    let private finding code subject message = { Code = code; Subject = subject; Message = message }

    let validateSnapshot snapshot =
        let findings = ResizeArray<GitHubV1WriterCensusFinding>()
        if snapshot.Schema <> "fsgg.v1-writer-census-qualification/1" then
            findings.Add(finding "WC-SCHEMA" "snapshot" "unsupported qualification schema")
        for label, value in
            [ "producerRevision", snapshot.ProducerRevision; "producerTree", snapshot.ProducerTree
              "roadmapRevision", snapshot.RoadmapRevision; "sourceBaseRevision", snapshot.SourceBaseRevision ] do
            if not (revision value) then findings.Add(finding "WC-REVISION" label "expected a full lowercase Git revision")
        for label, value in
            [ "roadmapSha256", snapshot.RoadmapSha256
              "acceptedEpochReceiptSha256", snapshot.AcceptedEpochReceiptSha256
              "q0EvidenceSha256", snapshot.Q0EvidenceSha256; "q0CorpusSha256", snapshot.Q0CorpusSha256
              "censusSha256", snapshot.CensusSha256; "commandContractSha256", snapshot.CommandContractSha256
              "checkerSha256", snapshot.CheckerSha256; "fixtureRunnerSha256", snapshot.FixtureRunnerSha256 ] do
            if not (sha256 value) then findings.Add(finding "WC-DIGEST" label "expected a lowercase SHA-256")

        let commandNames = snapshot.Commands |> List.map _.Name
        if snapshot.Commands.Length <> 54 then findings.Add(finding "WC-COMMAND-COUNT" "commands" "expected all 54 typed command roots")
        if commandNames <> List.sort commandNames || commandNames.Length <> (commandNames |> List.distinct |> List.length) then
            findings.Add(finding "WC-COMMAND-ORDER" "commands" "command roots must be unique and ordinally sorted")
        for command in snapshot.Commands do
            if String.IsNullOrWhiteSpace command.Name then findings.Add(finding "WC-COMMAND-NAME" "commands" "blank command root")
            if not (Set.contains command.Writes (Set [ "always"; "conditional"; "never" ])) then
                findings.Add(finding "WC-WRITE-CLASS" command.Name "unknown write classification")
        let count writes = snapshot.Commands |> List.filter (_.Writes >> (=) writes) |> List.length
        if count "always" <> 20 || count "conditional" <> 6 || count "never" <> 28 then
            findings.Add(finding "WC-WRITE-COUNTS" "commands" "expected 20 always, 6 conditional, and 28 never roots")

        let writerDispositions = Set [ "remote-writer"; "conditional-remote-writer"; "protected-admin-writer"; "publish-writer" ]
        let otherDispositions = Set [ "local-only"; "read-only"; "declaration-only"; "build-only"; "guard-only"; "instruction-only"; "test-only"; "typed-command-catalogue" ]
        let sourcePaths = snapshot.Sources |> List.map _.Path
        if snapshot.Sources.IsEmpty then findings.Add(finding "WC-SOURCE-EMPTY" "sources" "source population must not be empty")
        if sourcePaths <> List.sort sourcePaths || sourcePaths.Length <> (sourcePaths |> List.distinct |> List.length) then
            findings.Add(finding "WC-SOURCE-ORDER" "sources" "source paths must be unique and ordinally sorted")
        for source in snapshot.Sources do
            if String.IsNullOrWhiteSpace source.Path || source.Path.StartsWith("/", StringComparison.Ordinal) || source.Path.Contains("..") then
                findings.Add(finding "WC-SOURCE-PATH" source.Path "source path must be repository-relative")
            if not (sha256 source.Sha256) then findings.Add(finding "WC-SOURCE-DIGEST" source.Path "source identity is not a SHA-256")
            if source.SinkKinds <> List.sort source.SinkKinds || source.SinkKinds.Length <> (source.SinkKinds |> List.distinct |> List.length) then
                findings.Add(finding "WC-SINK-ORDER" source.Path "sink kinds must be unique and ordinally sorted")
            if not (Set.contains source.Disposition writerDispositions || Set.contains source.Disposition otherDispositions) then
                findings.Add(finding "WC-DISPOSITION" source.Path "unknown source disposition")
            if not source.SinkKinds.IsEmpty && not (Set.contains source.Disposition writerDispositions || source.Disposition = "typed-command-catalogue") then
                if source.NonWriterJustification |> Option.forall (fun value -> String.IsNullOrWhiteSpace value || value.Trim().Length < 12) then
                    findings.Add(finding "WC-JUSTIFICATION" source.Path "non-writer sink requires a specific justification")
        if findings.Count = 0 then Ok() else Error(List.ofSeq findings)

    let validateControls generated independent =
        let expected = requiredControls |> List.map controlId |> Set.ofList
        let findingsFor source values =
            let groups = values |> List.groupBy (fun value -> controlId value.Control)
            [ for missing in Set.difference expected (groups |> List.map fst |> Set.ofList) do
                  finding "WC-CONTROL-MISSING" missing $"{source} omitted the required control"
              for control, results in groups do
                  if results.Length <> 1 then
                      finding "WC-CONTROL-DUPLICATE" control $"{source} supplied the control more than once"
                  else
                      if not results.Head.BaselineGreen then finding "WC-BASELINE-RED" control $"{source} baseline is not green"
                      if not results.Head.MutationRed then finding "WC-MUTATION-SURVIVED" control $"{source} mutation did not fail" ]
        let findings = findingsFor "generated" generated @ findingsFor "independent" independent
        if findings.IsEmpty then Ok() else Error findings

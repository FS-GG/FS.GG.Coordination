namespace FS.GG.Coordination.Orchestration.Runner.Protocol

open System
open System.Security.Cryptography
open System.Text
open System.Text.Json
open System.Text.Json.Serialization

[<CLIMutable>]
type RunnerPollRequest =
    { Schema: string; CommandId: Guid; WorkItemPersistenceId: string; SessionId: Guid
      RunnerId: Guid; PrincipalId: string; FingerprintSha256: string
      Generation: int64; ExpectedRevision: int64; ClientSequence: int64
      IssuedAt: DateTimeOffset; ExpiresAt: DateTimeOffset }

[<CLIMutable>]
type RunnerAssignment =
    { Schema: string; WorkItemPersistenceId: string; RouteId: Guid; AttemptId: Guid
      CandidateId: Guid; SessionId: Guid; RunnerId: Guid; PrincipalId: string
      FingerprintSha256: string; Generation: int64; WorkflowRevision: int64
      ClientSequence: int64; ServerSequence: int64; PayloadSha256: string
      AssignmentSha256: string; ExpiresAt: DateTimeOffset; Deadline: DateTimeOffset }

[<CLIMutable>]
type RunnerAckRequest =
    { Schema: string; CommandId: Guid; WorkItemPersistenceId: string; SessionId: Guid
      RunnerId: Guid; PrincipalId: string; FingerprintSha256: string
      Generation: int64; ExpectedRevision: int64; ClientSequence: int64
      AssignmentSha256: string; IssuedAt: DateTimeOffset; ExpiresAt: DateTimeOffset }

[<CLIMutable>]
type RunnerCandidateRequest =
    { Schema: string; CommandId: Guid; WorkItemPersistenceId: string; SessionId: Guid
      RunnerId: Guid; PrincipalId: string; FingerprintSha256: string
      Generation: int64; ExpectedRevision: int64; ClientSequence: int64
      AssignmentSha256: string
      CandidateId: Guid; BaselineSha: string; HeadSha: string; TreeSha: string
      ManifestSha256: string; ContentSha256: string; MediaType: string; SizeBytes: int64
      RetainUntil: DateTimeOffset; ContentBase64: string
      IssuedAt: DateTimeOffset; ExpiresAt: DateTimeOffset }

[<RequireQualifiedAccess>]
module RunnerWire =
    let pollSchema = "fsgg.orchestration.runner-poll/1"
    let assignmentSchema = "fsgg.orchestration.runner-assignment/1"
    let ackSchema = "fsgg.orchestration.runner-ack/1"
    let candidateSchema = "fsgg.orchestration.runner-candidate/1"

    let options =
        let value = JsonSerializerOptions(PropertyNamingPolicy=JsonNamingPolicy.CamelCase,MaxDepth=8)
        value.PropertyNameCaseInsensitive <- false
        value.UnmappedMemberHandling <- JsonUnmappedMemberHandling.Disallow
        value

    let sha256 (bytes:byte array) =
        SHA256.HashData bytes |> Convert.ToHexString |> _.ToLowerInvariant()

    let validSha256 (value:string) =
        not(String.IsNullOrWhiteSpace value) && value.Length=64
        && value |> Seq.forall(fun character -> Char.IsAsciiHexDigit character && not(Char.IsUpper character))

    let serialize value = JsonSerializer.SerializeToUtf8Bytes(value,options)

    let deserializeClosed<'T> (expected:Set<string>) maximumBytes (bytes:byte array) =
        if bytes.Length=0 || bytes.Length>maximumBytes then Error "runner-message-size-refused"
        else
            try
                use document = JsonDocument.Parse(ReadOnlyMemory bytes,JsonDocumentOptions(MaxDepth=8))
                if document.RootElement.ValueKind<>JsonValueKind.Object then Error "runner-message-object-required"
                else
                    let names=document.RootElement.EnumerateObject() |> Seq.map _.Name |> Seq.toList
                    if names.Length<>expected.Count || Set.ofList names<>expected then Error "runner-message-shape-refused"
                    else
                        let value=JsonSerializer.Deserialize<'T>(ReadOnlySpan bytes,options)
                        if isNull(box value) then Error "runner-message-null" else Ok value
            with :? JsonException -> Error "runner-message-json-refused"

    let pollProperties = set ["schema";"commandId";"workItemPersistenceId";"sessionId";"runnerId";"principalId";"fingerprintSha256";"generation";"expectedRevision";"clientSequence";"issuedAt";"expiresAt"]
    let ackProperties = Set.add "assignmentSha256" pollProperties
    let candidateProperties =
        set ["schema";"commandId";"workItemPersistenceId";"sessionId";"runnerId";"principalId";"fingerprintSha256";"generation";"expectedRevision";"clientSequence";"assignmentSha256";"candidateId";"baselineSha";"headSha";"treeSha";"manifestSha256";"contentSha256";"mediaType";"sizeBytes";"retainUntil";"contentBase64";"issuedAt";"expiresAt"]

    let parsePoll bytes = deserializeClosed<RunnerPollRequest> pollProperties 8192 bytes
    let parseAck bytes = deserializeClosed<RunnerAckRequest> ackProperties 8192 bytes
    let parseCandidate bytes = deserializeClosed<RunnerCandidateRequest> candidateProperties (140*1024*1024) bytes

    let assignmentDigest (value:RunnerAssignment) =
        let unsigned={value with AssignmentSha256=""}
        serialize unsigned |> sha256

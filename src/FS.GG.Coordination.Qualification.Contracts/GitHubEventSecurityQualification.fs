namespace FS.GG.Coordination.Qualification.Contracts

open System
open System.Security.Cryptography
open System.Text
open System.Text.Json
open System.Text.RegularExpressions

type GitHubEventSecurityFacts =
    { RawPayload: byte array; Signature: string; Secret: byte array; DeliveryId: string
      InstallationId: int64; ExpectedInstallationId: int64; Repository: string; ExpectedRepository: string
      ReceivedAtUnixSeconds: int64; EventTimestampUnixSeconds: int64; ReplayWindowSeconds: int64
      SeenDeliveryIds: string list; PayloadSubject: string; PayloadRevision: int64
      ApiSubject: string; ApiRevision: int64; RequiredPermissions: string list
      GrantedPermissions: string list; AttemptsDerivedWrite: bool }
type GitHubEventSecurityPlan =
    { SchemaVersion: int; DeliveryId: string; InstallationId: int64; Repository: string
      SignatureAlgorithm: string; Signature: string; PayloadSha256: string; EventTimestampUnixSeconds: int64
      Subject: string; SubjectRevision: int64; RequiredPermissions: string list
      ReplayLowerBound: int64; ReplayUpperBound: int64; Disposition: string
      AttemptsDerivedWrite: bool; SchedulingKey: string; Seal: string }
[<RequireQualifiedAccess>]
type GitHubEventSecurityFinding =
    | MissingField of string | MalformedField of string | InvalidSignature
    | InstallationScopeMismatch of int64 | RepositoryScopeMismatch of string
    | ReplayExpired of int64 | ReplayFromFuture of int64 | DuplicateDelivery of string
    | PayloadApiDisagreement of string | NonCanonicalPermissions of string
    | MissingPermission of string | ExcessivePermission of string | DirectWriteAttempt of string
    | AlteredSeal | ReplayConflict of string | InvalidSerialization of string
type GitHubEventSecurityControl =
    | EventSecurityPrerequisite | EventSecurityRoadmap | SignaturePositive | SignatureNegative
    | EventInstallationScope | EventRepositoryScope | ReplayLowerBound | ReplayUpperBound
    | DuplicateDelivery | PayloadApiAgreement | PayloadApiDisagreement | LeastPrivilege
    | ExcessivePermission | MissingPermission | SchedulingOnly | ExclusiveWriter
    | DirectWrite | EventSecurityOrdering | EventSecuritySeal | EventSecurityReplay
    | EventSecurityQuintPreservation | EventSecurityNoNetwork | EventSecurityNoProductionQueue
    | EventSecurityNoMutation
type GitHubEventSecurityControlResult =
    { Control: GitHubEventSecurityControl; ControlPassed: bool; BaselineGreen: bool }

module GitHubEventSecurityQualification =
    let disposition = "schedule-reconciliation"
    let requiredControls =
        [ EventSecurityPrerequisite; EventSecurityRoadmap; SignaturePositive; SignatureNegative
          EventInstallationScope; EventRepositoryScope; ReplayLowerBound; ReplayUpperBound
          DuplicateDelivery; PayloadApiAgreement; PayloadApiDisagreement; LeastPrivilege
          ExcessivePermission; MissingPermission; SchedulingOnly; ExclusiveWriter
          DirectWrite; EventSecurityOrdering; EventSecuritySeal; EventSecurityReplay
          EventSecurityQuintPreservation; EventSecurityNoNetwork; EventSecurityNoProductionQueue
          EventSecurityNoMutation ]
    let controlId = function
        | EventSecurityPrerequisite -> "prerequisite" | EventSecurityRoadmap -> "roadmap"
        | SignaturePositive -> "signature-positive" | SignatureNegative -> "signature-negative"
        | EventInstallationScope -> "installation-scope" | EventRepositoryScope -> "repository-scope"
        | ReplayLowerBound -> "replay-lower-bound" | ReplayUpperBound -> "replay-upper-bound"
        | DuplicateDelivery -> "duplicate-delivery" | PayloadApiAgreement -> "payload-api-agreement"
        | PayloadApiDisagreement -> "payload-api-disagreement" | LeastPrivilege -> "least-privilege"
        | ExcessivePermission -> "excessive-permission" | MissingPermission -> "missing-permission"
        | SchedulingOnly -> "scheduling-only" | ExclusiveWriter -> "exclusive-writer"
        | DirectWrite -> "direct-write" | EventSecurityOrdering -> "ordering"
        | EventSecuritySeal -> "seal" | EventSecurityReplay -> "replay"
        | EventSecurityQuintPreservation -> "quint-preservation" | EventSecurityNoNetwork -> "no-network"
        | EventSecurityNoProductionQueue -> "no-production-queue" | EventSecurityNoMutation -> "no-mutation"

    let private token = Regex("^[A-Za-z0-9][A-Za-z0-9._:/-]*$", RegexOptions.CultureInvariant)
    let private signaturePattern = Regex("^sha256=[0-9a-f]{64}$", RegexOptions.CultureInvariant)
    let private frame (value: string) = $"{Encoding.UTF8.GetByteCount value}:{value}"
    let private strings values = values |> List.map frame |> String.concat ""
    let private hash (value: string) =
        value |> Encoding.UTF8.GetBytes |> SHA256.HashData |> Convert.ToHexString |> _.ToLowerInvariant()
    let sign (secret: byte array) (rawPayload: byte array) =
        use hmac = new HMACSHA256(secret)
        "sha256=" + (hmac.ComputeHash(rawPayload) |> Convert.ToHexString |> _.ToLowerInvariant())
    let private signatureMatches (secret: byte array) (rawPayload: byte array) (supplied: string) =
        if isNull supplied || not(signaturePattern.IsMatch supplied) then false
        else
            let expected = sign secret rawPayload |> Encoding.ASCII.GetBytes
            let actual = Encoding.ASCII.GetBytes supplied
            CryptographicOperations.FixedTimeEquals(expected, actual)
    let private sealOf (plan: GitHubEventSecurityPlan) =
        [ "github-event-security/v1"; plan.DeliveryId; string plan.InstallationId; plan.Repository
          plan.SignatureAlgorithm; plan.Signature; plan.PayloadSha256; string plan.EventTimestampUnixSeconds
          plan.Subject; string plan.SubjectRevision; strings plan.RequiredPermissions
          string plan.ReplayLowerBound; string plan.ReplayUpperBound; plan.Disposition
          string plan.AttemptsDerivedWrite; plan.SchedulingKey ]
        |> strings |> hash
    let private isCanonical values = values = (values |> List.distinct |> List.sort)

    let compile (facts: GitHubEventSecurityFacts) =
        let errors = ResizeArray<GitHubEventSecurityFinding>()
        let requireText name value =
            if String.IsNullOrWhiteSpace value then errors.Add(GitHubEventSecurityFinding.MissingField name)
            elif not(token.IsMatch value) then errors.Add(GitHubEventSecurityFinding.MalformedField name)
        requireText "deliveryId" facts.DeliveryId
        requireText "repository" facts.Repository
        requireText "expectedRepository" facts.ExpectedRepository
        requireText "payloadSubject" facts.PayloadSubject
        requireText "apiSubject" facts.ApiSubject
        if isNull facts.RawPayload || facts.RawPayload.Length = 0 then errors.Add(GitHubEventSecurityFinding.MissingField "rawPayload")
        if isNull facts.Secret || facts.Secret.Length < 32 then errors.Add(GitHubEventSecurityFinding.MalformedField "secret")
        elif not(signatureMatches facts.Secret facts.RawPayload facts.Signature) then errors.Add GitHubEventSecurityFinding.InvalidSignature
        if facts.InstallationId <= 0L || facts.ExpectedInstallationId <= 0L then errors.Add(GitHubEventSecurityFinding.MalformedField "installationId")
        elif facts.InstallationId <> facts.ExpectedInstallationId then errors.Add(GitHubEventSecurityFinding.InstallationScopeMismatch facts.InstallationId)
        if facts.Repository <> facts.ExpectedRepository then errors.Add(GitHubEventSecurityFinding.RepositoryScopeMismatch facts.Repository)
        if facts.ReplayWindowSeconds <= 0L || facts.ReceivedAtUnixSeconds < 0L || facts.EventTimestampUnixSeconds < 0L
           || facts.ReceivedAtUnixSeconds > Int64.MaxValue - facts.ReplayWindowSeconds then
            errors.Add(GitHubEventSecurityFinding.MalformedField "replayWindow")
        else
            let lower = facts.ReceivedAtUnixSeconds - facts.ReplayWindowSeconds
            let upper = facts.ReceivedAtUnixSeconds + facts.ReplayWindowSeconds
            if facts.EventTimestampUnixSeconds < lower then errors.Add(GitHubEventSecurityFinding.ReplayExpired facts.EventTimestampUnixSeconds)
            if facts.EventTimestampUnixSeconds > upper then errors.Add(GitHubEventSecurityFinding.ReplayFromFuture facts.EventTimestampUnixSeconds)
        if facts.SeenDeliveryIds |> List.exists ((=) facts.DeliveryId) then errors.Add(GitHubEventSecurityFinding.DuplicateDelivery facts.DeliveryId)
        if facts.PayloadSubject <> facts.ApiSubject then errors.Add(GitHubEventSecurityFinding.PayloadApiDisagreement "subject")
        if facts.PayloadRevision <= 0L || facts.ApiRevision <= 0L then errors.Add(GitHubEventSecurityFinding.MalformedField "subjectRevision")
        elif facts.PayloadRevision <> facts.ApiRevision then errors.Add(GitHubEventSecurityFinding.PayloadApiDisagreement "revision")
        if facts.RequiredPermissions.IsEmpty || not(isCanonical facts.RequiredPermissions) then errors.Add(GitHubEventSecurityFinding.NonCanonicalPermissions "required")
        if not(isCanonical facts.GrantedPermissions) then errors.Add(GitHubEventSecurityFinding.NonCanonicalPermissions "granted")
        for permission in facts.RequiredPermissions do
            requireText "requiredPermission" permission
            if not(List.contains permission facts.GrantedPermissions) then errors.Add(GitHubEventSecurityFinding.MissingPermission permission)
        for permission in facts.GrantedPermissions do
            requireText "grantedPermission" permission
            if not(List.contains permission facts.RequiredPermissions) then errors.Add(GitHubEventSecurityFinding.ExcessivePermission permission)
        if facts.AttemptsDerivedWrite then errors.Add(GitHubEventSecurityFinding.DirectWriteAttempt facts.DeliveryId)
        if errors.Count > 0 then Error(List.ofSeq errors)
        else
            let lower = facts.ReceivedAtUnixSeconds - facts.ReplayWindowSeconds
            let upper = facts.ReceivedAtUnixSeconds + facts.ReplayWindowSeconds
            let schedulingKey = strings [ facts.Repository; facts.PayloadSubject ] |> hash
            let unsigned =
                { SchemaVersion = 1; DeliveryId = facts.DeliveryId; InstallationId = facts.InstallationId
                  Repository = facts.Repository; SignatureAlgorithm = "sha256"; Signature = facts.Signature
                  PayloadSha256 = facts.RawPayload |> SHA256.HashData |> Convert.ToHexString |> _.ToLowerInvariant()
                  EventTimestampUnixSeconds = facts.EventTimestampUnixSeconds
                  Subject = facts.PayloadSubject; SubjectRevision = facts.PayloadRevision
                  RequiredPermissions = facts.RequiredPermissions; ReplayLowerBound = lower; ReplayUpperBound = upper
                  Disposition = disposition; AttemptsDerivedWrite = false; SchedulingKey = schedulingKey; Seal = "" }
            Ok { unsigned with Seal = sealOf unsigned }

    let serialize (plan: GitHubEventSecurityPlan) =
        JsonSerializer.Serialize(
            {| schemaVersion = plan.SchemaVersion; deliveryId = plan.DeliveryId; installationId = plan.InstallationId
               repository = plan.Repository; signatureAlgorithm = plan.SignatureAlgorithm; signature = plan.Signature
               payloadSha256 = plan.PayloadSha256; eventTimestampUnixSeconds = plan.EventTimestampUnixSeconds
               subject = plan.Subject; subjectRevision = plan.SubjectRevision
               requiredPermissions = plan.RequiredPermissions; replayLowerBound = plan.ReplayLowerBound
               replayUpperBound = plan.ReplayUpperBound; disposition = plan.Disposition
               attemptsDerivedWrite = plan.AttemptsDerivedWrite; schedulingKey = plan.SchedulingKey; seal = plan.Seal |})

    let verify expectedSeal (plan: GitHubEventSecurityPlan) =
        if plan.SchemaVersion <> 1 then Error [ GitHubEventSecurityFinding.InvalidSerialization "schemaVersion" ]
        elif plan.SignatureAlgorithm <> "sha256" || not(signaturePattern.IsMatch plan.Signature) then Error [ GitHubEventSecurityFinding.InvalidSignature ]
        elif plan.Disposition <> disposition || plan.AttemptsDerivedWrite then Error [ GitHubEventSecurityFinding.DirectWriteAttempt plan.DeliveryId ]
        elif not(isCanonical plan.RequiredPermissions) then Error [ GitHubEventSecurityFinding.NonCanonicalPermissions "required" ]
        elif plan.ReplayLowerBound > plan.ReplayUpperBound then Error [ GitHubEventSecurityFinding.InvalidSerialization "replay bounds" ]
        elif plan.Seal <> sealOf plan || plan.Seal <> expectedSeal then Error [ GitHubEventSecurityFinding.AlteredSeal ]
        else Ok plan

    let parse (value: string) =
        try
            use document = JsonDocument.Parse value
            let root = document.RootElement
            let text (name: string) = root.GetProperty(name).GetString()
            let texts (name: string) = root.GetProperty(name).EnumerateArray() |> Seq.map _.GetString() |> Seq.toList
            let plan =
                { SchemaVersion = root.GetProperty("schemaVersion").GetInt32(); DeliveryId = text "deliveryId"
                  InstallationId = root.GetProperty("installationId").GetInt64(); Repository = text "repository"
                  SignatureAlgorithm = text "signatureAlgorithm"; Signature = text "signature"
                  PayloadSha256 = text "payloadSha256"; EventTimestampUnixSeconds = root.GetProperty("eventTimestampUnixSeconds").GetInt64()
                  Subject = text "subject"; SubjectRevision = root.GetProperty("subjectRevision").GetInt64()
                  RequiredPermissions = texts "requiredPermissions"; ReplayLowerBound = root.GetProperty("replayLowerBound").GetInt64()
                  ReplayUpperBound = root.GetProperty("replayUpperBound").GetInt64(); Disposition = text "disposition"
                  AttemptsDerivedWrite = root.GetProperty("attemptsDerivedWrite").GetBoolean()
                  SchedulingKey = text "schedulingKey"; Seal = text "seal" }
            match verify plan.Seal plan with
            | Error findings -> Error findings
            | Ok parsed when serialize parsed <> value -> Error [ GitHubEventSecurityFinding.InvalidSerialization "non-canonical bytes" ]
            | Ok parsed -> Ok parsed
        with error -> Error [ GitHubEventSecurityFinding.InvalidSerialization error.Message ]

    let replay prior facts =
        match verify prior.Seal prior, compile facts with
        | Error findings, _ -> Error findings
        | _, Error findings -> Error findings
        | Ok _, Ok candidate when serialize candidate = serialize prior -> Ok prior
        | Ok _, Ok _ -> Error [ GitHubEventSecurityFinding.ReplayConflict "event replay differs from sealed plan" ]

    let validateControls generated independent =
        let expected = requiredControls |> List.map controlId
        let validate label rows =
            [ if rows |> List.map (fun row -> controlId row.Control) <> expected then yield $"{label} control inventory differs"
              if rows |> List.exists (fun row -> not row.ControlPassed || not row.BaselineGreen) then yield $"{label} control failed" ]
        let errors = validate "generated" generated @ validate "independent" independent
        if errors.IsEmpty then Ok () else Error errors

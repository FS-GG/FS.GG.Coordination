module FS.GG.Coordination.GitHubEventSecurityTests

open System.Text
open Xunit
open FS.GG.Coordination.Qualification.Contracts
open FS.GG.Coordination.Qualification.Contracts.GitHubEventSecurityQualification

let private payloadFor installation repository kind id revision =
    Encoding.UTF8.GetBytes $"{{\"action\":\"edited\",\"installation\":{{\"id\":{installation}}},\"repository\":{{\"full_name\":\"{repository}\"}},\"subject\":{{\"kind\":\"{kind}\",\"id\":\"{id}\",\"revision\":{revision}}}}}"
let private payload = payloadFor 81234L "FS-GG/FS.GG.Coordination" "issue" "310" 7L
let private secret = Encoding.UTF8.GetBytes "0123456789abcdef0123456789abcdef"
let private received = 1788702000L
let private facts () =
    let unsigned =
        { RawPayload = payload; Signature = ""; Secret = secret; DeliveryId = "delivery-gs2-07-4"
          ExpectedInstallationId = 81234L; ExpectedRepository = "FS-GG/FS.GG.Coordination"
          ReceivedAtUnixSeconds = received; EventTimestampUnixSeconds = received; ReplayWindowSeconds = 300L
          SeenDeliveryIds = []; SeenPayloadSha256 = []
          ApiSubject = "issue:310"; ApiRevision = 7L
          RequiredPermissions = [ "contents:read"; "metadata:read" ]
          GrantedPermissions = [ "contents:read"; "metadata:read" ]; AttemptsDerivedWrite = false }
    { unsigned with Signature = sign secret payload }
let private get = function Ok value -> value | Error errors -> failwithf "unexpected refusal: %A" errors
let private findings = function Error errors -> errors | Ok value -> failwithf "expected refusal: %A" value

[<Fact>]
let ``valid signed event emits only a reconciliation schedule`` () =
    let plan = facts () |> compile |> get
    Assert.Equal(disposition, plan.Disposition)
    Assert.False(plan.AttemptsDerivedWrite)
    Assert.Equal("sha256", plan.SignatureAlgorithm)
    Assert.Equal(received, plan.EventTimestampUnixSeconds)
    Assert.Equal("issue:310", plan.Subject)
    Assert.Equal<string list>([ "contents:read"; "metadata:read" ], plan.RequiredPermissions)

[<Fact>]
let ``signature authenticates exact raw bytes and canonical header`` () =
    let original = facts ()
    Assert.True(compile original |> Result.isOk)
    Assert.Contains(GitHubEventSecurityFinding.InvalidSignature, compile { original with RawPayload = Encoding.UTF8.GetBytes "{}" } |> findings)
    Assert.Contains(GitHubEventSecurityFinding.InvalidSignature, compile { original with Signature = original.Signature.ToUpperInvariant() } |> findings)
    let malformed = Encoding.UTF8.GetBytes "{\"installation\":{\"id\":81234},\"repository\":{\"full_name\":\"FS-GG/FS.GG.Coordination\"}}"
    Assert.Contains(GitHubEventSecurityFinding.MalformedPayload "missing subject", compile { original with RawPayload = malformed; Signature = sign secret malformed } |> findings)

[<Fact>]
let ``installation and repository scopes are exact`` () =
    let original = facts ()
    let wrongInstallation = payloadFor 7L "FS-GG/FS.GG.Coordination" "issue" "310" 7L
    Assert.Contains(GitHubEventSecurityFinding.InstallationScopeMismatch 7L, compile { original with RawPayload = wrongInstallation; Signature = sign secret wrongInstallation } |> findings)
    let wrongRepository = payloadFor 81234L "FS-GG/Outside" "issue" "310" 7L
    Assert.Contains(GitHubEventSecurityFinding.RepositoryScopeMismatch "FS-GG/Outside", compile { original with RawPayload = wrongRepository; Signature = sign secret wrongRepository } |> findings)

[<Fact>]
let ``replay window is inclusive and duplicates are rejected`` () =
    let original = facts ()
    Assert.True(compile { original with EventTimestampUnixSeconds = received - 300L } |> Result.isOk)
    Assert.True(compile { original with EventTimestampUnixSeconds = received + 300L } |> Result.isOk)
    Assert.Contains(GitHubEventSecurityFinding.ReplayExpired(received - 301L), compile { original with EventTimestampUnixSeconds = received - 301L } |> findings)
    Assert.Contains(GitHubEventSecurityFinding.ReplayFromFuture(received + 301L), compile { original with EventTimestampUnixSeconds = received + 301L } |> findings)
    Assert.Contains(GitHubEventSecurityFinding.MalformedField "replayWindow", compile { original with ReceivedAtUnixSeconds = System.Int64.MaxValue } |> findings)
    Assert.Contains(GitHubEventSecurityFinding.DuplicateDelivery original.DeliveryId, compile { original with SeenDeliveryIds = [ original.DeliveryId ] } |> findings)
    let baseline = compile original |> get
    let changedDelivery = { original with DeliveryId = "delivery-changed"; SeenDeliveryIds = [ original.DeliveryId ]; SeenPayloadSha256 = [ baseline.PayloadSha256 ] }
    Assert.Contains(GitHubEventSecurityFinding.DuplicatePayload baseline.PayloadSha256, compile changedDelivery |> findings)

[<Fact>]
let ``payload and API authority must agree exactly`` () =
    let original = facts ()
    Assert.Contains(GitHubEventSecurityFinding.PayloadApiDisagreement "subject", compile { original with ApiSubject = "issue:311" } |> findings)
    Assert.Contains(GitHubEventSecurityFinding.PayloadApiDisagreement "revision", compile { original with ApiRevision = 8L } |> findings)
    let contradictory = payloadFor 81234L "FS-GG/Outside" "issue" "999" 99L
    let contradictions = compile { original with RawPayload = contradictory; Signature = sign secret contradictory } |> findings
    Assert.Contains(GitHubEventSecurityFinding.RepositoryScopeMismatch "FS-GG/Outside", contradictions)
    Assert.Contains(GitHubEventSecurityFinding.PayloadApiDisagreement "subject", contradictions)
    Assert.Contains(GitHubEventSecurityFinding.PayloadApiDisagreement "revision", contradictions)

[<Fact>]
let ``least privilege is exact and canonical`` () =
    let original = facts ()
    Assert.Contains(GitHubEventSecurityFinding.MissingPermission "contents:read", compile { original with GrantedPermissions = [ "metadata:read" ] } |> findings)
    Assert.Contains(GitHubEventSecurityFinding.ExcessivePermission "issues:write", compile { original with GrantedPermissions = [ "contents:read"; "issues:write"; "metadata:read" ] } |> findings)
    Assert.Contains(GitHubEventSecurityFinding.NonCanonicalPermissions "granted", compile { original with GrantedPermissions = List.rev original.GrantedPermissions } |> findings)

[<Fact>]
let ``direct derived write is refused`` () =
    let original = facts ()
    Assert.Contains(GitHubEventSecurityFinding.DirectWriteAttempt original.DeliveryId, compile { original with AttemptsDerivedWrite = true } |> findings)

[<Fact>]
let ``seal canonical serialization and replay fail closed`` () =
    let original = facts ()
    let plan = compile original |> get
    Assert.Equal(Ok plan, parse (serialize plan))
    Assert.Equal(Error [ GitHubEventSecurityFinding.InvalidSerialization "non-canonical bytes" ], parse (serialize plan + " "))
    Assert.Equal(Error [ GitHubEventSecurityFinding.AlteredSeal ], verify (String.replicate 64 "0") plan)
    Assert.Equal(serialize plan, serialize (replay plan original |> get))
    let later = { original with EventTimestampUnixSeconds = received + 1L }
    Assert.Contains(GitHubEventSecurityFinding.ReplayConflict "event replay differs from sealed plan", replay plan later |> findings)
    Assert.Contains(GitHubEventSecurityFinding.ReplayConflict "event replay differs from sealed plan", replay plan { original with DeliveryId = "delivery-changed" } |> findings)

[<Fact>]
let ``control inventory mutation is detected`` () =
    let green: GitHubEventSecurityControlResult list =
        requiredControls |> List.map (fun control -> { Control = control; ControlPassed = true; BaselineGreen = true })
    Assert.Equal(Ok (), validateControls green green)
    Assert.Contains("generated control inventory differs", validateControls green.Tail green |> findings)
    Assert.Contains("independent control failed", validateControls green ({ green.Head with ControlPassed = false } :: green.Tail) |> findings)

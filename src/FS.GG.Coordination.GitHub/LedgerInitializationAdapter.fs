namespace FS.GG.Coordination.GitHub

open System
open System.Globalization
open System.Security.Cryptography
open System.Text
open System.Text.Json.Nodes

type ExpectedLedgerRef = ExpectedAbsent | ExpectedParent of string
type LedgerObject = { Kind: string; Oid: string; Bytes: byte array }
type LedgerInitializationAuthority = { KeyId:string;PublicKeyPem:string;PublicKeySpkiSha256:string;Payload:byte array;Signature:byte array;AuthorizedAt:DateTimeOffset;ExpiresAt:DateTimeOffset }
type LedgerInitializationInput =
    { RepositoryId:int64;Repository:string;FleetId:string;Ref:string;Tag:string;ManifestSha256:string;TrustAnchorSha256:string
      SourceSha256:string;DesiredPolicySha256:string;FirstCaptureSha256:string;SecondCaptureSha256:string;AuthorizationKeyId:string;AuthorizationKeySpkiSha256:string
      AuthorizationWorkflowRevision:string;AuthorizationWorkflowSha256:string
      CutoverAppId:int64;CutoverInstallationId:int64;ControlIssueNumber:int64;ExpectedRef:ExpectedLedgerRef;CreatedAt:DateTimeOffset;AuthorName:string;AuthorEmail:string }
type LedgerInitializationPlan =
    { InputSha256:string;AuthorizationSha256:string;Event:LedgerObject;Head:LedgerObject;Tree:LedgerObject;Commit:LedgerObject
      Ref:string;Tag:string;ExpectedRef:ExpectedLedgerRef;OperationOrder:string list;Seal:string }
type LedgerRefRead = RefAbsent | RefAt of string | RefUnknown of string
type LedgerInitializationPort = { ReadRef:string->LedgerRefRead;PutObject:LedgerObject->Result<unit,string>;CreateRef:string->string->ExpectedLedgerRef->Result<unit,string> }
type LedgerInitializationOutcome = Initialized | AlreadyInitialized | InitializationRefused of string list | InitializationIndeterminate of string
type LedgerInitializationReadback = { Ref:LedgerRefRead;Tag:LedgerRefRead;Objects:LedgerObject list }

[<RequireQualifiedAccess>]
module LedgerInitializationAdapter =
    let private utf8 (value:string) = Encoding.UTF8.GetBytes value
    let private sha256 (bytes:byte array) = SHA256.HashData bytes |> Convert.ToHexString |> _.ToLowerInvariant()
    let private digestLike (value:string) = not(String.IsNullOrWhiteSpace value) && value.Length=64 && value |> Seq.forall (fun c -> c>='0'&&c<='9'||c>='a'&&c<='f')
    let private oid kind (bytes:byte array) =
        let header=utf8 $"{kind} {bytes.Length}\u0000"
        SHA1.HashData(Array.append header bytes) |> Convert.ToHexString |> _.ToLowerInvariant()
    let private object' kind bytes = {Kind=kind;Oid=oid kind bytes;Bytes=bytes}
    let private json (pairs:(string*JsonNode) list) =
        let root=JsonObject()
        pairs |> List.sortBy fst |> List.iter(fun (k,v)->root.Add(k,v))
        root.ToJsonString() |> ShardedJournalAdapter.canonicalJson |> Result.defaultWith invalidOp
    let private lease = function ExpectedAbsent -> "absent" | ExpectedParent x -> "parent:"+x
    let canonicalInput input =
        json ["authorEmail",JsonValue.Create input.AuthorEmail;"authorName",JsonValue.Create input.AuthorName;"authorizationKeyId",JsonValue.Create input.AuthorizationKeyId;"authorizationKeySpkiSha256",JsonValue.Create input.AuthorizationKeySpkiSha256;"authorizationWorkflowRevision",JsonValue.Create input.AuthorizationWorkflowRevision;"authorizationWorkflowSha256",JsonValue.Create input.AuthorizationWorkflowSha256;"controlIssueNumber",JsonValue.Create input.ControlIssueNumber
              "createdAt",JsonValue.Create(input.CreatedAt.ToUniversalTime().ToString("O"));"cutoverAppId",JsonValue.Create input.CutoverAppId;"cutoverInstallationId",JsonValue.Create input.CutoverInstallationId
              "desiredPolicySha256",JsonValue.Create input.DesiredPolicySha256;"expectedRef",JsonValue.Create(lease input.ExpectedRef);"firstCaptureSha256",JsonValue.Create input.FirstCaptureSha256
              "fleetId",JsonValue.Create input.FleetId;"manifestSha256",JsonValue.Create input.ManifestSha256;"ref",JsonValue.Create input.Ref;"repository",JsonValue.Create input.Repository
              "repositoryId",JsonValue.Create input.RepositoryId;"secondCaptureSha256",JsonValue.Create input.SecondCaptureSha256;"sourceSha256",JsonValue.Create input.SourceSha256
              "tag",JsonValue.Create input.Tag;"trustAnchorSha256",JsonValue.Create input.TrustAnchorSha256]
    let private authorityValid asOf (authority:LedgerInitializationAuthority) expected =
        try
            use rsa=RSA.Create()
            rsa.ImportFromPem(authority.PublicKeyPem)
            authority.KeyId.Length>0 && sha256(rsa.ExportSubjectPublicKeyInfo())=authority.PublicKeySpkiSha256 && authority.Payload=expected
            && authority.AuthorizedAt<=asOf && asOf<authority.ExpiresAt && authority.ExpiresAt-authority.AuthorizedAt<=TimeSpan.FromHours 2.
            && rsa.VerifyData(authority.Payload,authority.Signature,HashAlgorithmName.SHA256,RSASignaturePadding.Pss)
        with _ -> false
    let private treeBytes (entries:(string*LedgerObject) list) =
        entries |> List.sortBy fst |> List.collect(fun (name,value)->
            let prefix=utf8 $"100644 {name}\u0000" |> Array.toList
            let raw=Convert.FromHexString(value.Oid) |> Array.toList
            prefix@raw) |> List.toArray
    let plan asOf authority input =
        let address=ShardedJournalAdapter.address Cutover "fleet-cutover:fs-gg-production"
        let expected=canonicalInput input
        let errors=
            [ if input.RepositoryId<>1351660651L || input.Repository<>"FS-GG/FS.GG.Coordination.Authority" then "authority-repository"
              if input.FleetId<>"fs-gg-production" then "fleet"
              match address with Ok a when a.Ref=input.Ref -> () | _ -> "ref"
              if not(input.Tag.StartsWith("refs/tags/fsgg/v2/fleet-cutover/operating-v1/",StringComparison.Ordinal)) then "tag"
              for value in [input.ManifestSha256;input.TrustAnchorSha256;input.SourceSha256;input.DesiredPolicySha256;input.FirstCaptureSha256;input.SecondCaptureSha256] do if not(digestLike value) then "digest"
              if input.CutoverAppId<=0L || input.CutoverInstallationId<=0L || input.ControlIssueNumber<>2L then "binding"
              if authority.KeyId<>input.AuthorizationKeyId || authority.PublicKeySpkiSha256<>input.AuthorizationKeySpkiSha256 || not(digestLike input.AuthorizationKeySpkiSha256) then "authorization-key"
              if input.AuthorizationWorkflowRevision.Length<>40 || not(input.AuthorizationWorkflowRevision|>Seq.forall(fun c->c>='0'&&c<='9'||c>='a'&&c<='f')) || not(digestLike input.AuthorizationWorkflowSha256) then "authorization-workflow"
              match input.ExpectedRef with ExpectedAbsent -> () | ExpectedParent _ -> "genesis-must-expect-absence"
              if not(authorityValid asOf authority expected) then "authorization" ] |> List.distinct
        if not errors.IsEmpty then Error errors else
        let eventBytes=json ["fleetId",JsonValue.Create input.FleetId;"manifestSha256",JsonValue.Create input.ManifestSha256;"phase",JsonValue.Create "OperatingV1"
                             "schema",JsonValue.Create "fsgg.github-substrate.epoch-event/1";"trustAnchorSha256",JsonValue.Create input.TrustAnchorSha256]
        let event=object' "blob" eventBytes
        let address=match address with Ok value -> value | Error failure -> invalidOp (string failure)
        let unsigned={SchemaVersion=1;Address=address;Generation=1L;EventDigest=sha256 eventBytes;SnapshotDigest=None;Terminal=false;PriorHeadDigest=None;HeadDigest=""}
        let headBytes=ShardedJournalAdapter.journalHeadBytes unsigned
        let head=object' "blob" headBytes
        let tree=object' "tree" (treeBytes ["event.json",event;"head.json",head])
        let unix=input.CreatedAt.ToUnixTimeSeconds()
        let tz=input.CreatedAt.ToString("zzz",CultureInfo.InvariantCulture).Replace(":","")
        let commitBytes=utf8 $"tree {tree.Oid}\nauthor {input.AuthorName} <{input.AuthorEmail}> {unix} {tz}\ncommitter {input.AuthorName} <{input.AuthorEmail}> {unix} {tz}\n\nInitialize FS.GG production fleet epoch in OperatingV1\n"
        let commit=object' "commit" commitBytes
        let authSha=sha256(Array.append authority.Payload authority.Signature)
        let order=["put-event-blob";"put-head-blob";"put-tree";"put-commit";"reread-objects";"create-ref-expected-absent";"reread-ref";"create-tag-expected-absent";"reread-tag"]
        let inputSha=sha256 expected
        let seal=String.concat "\n" ([inputSha;authSha;event.Oid;head.Oid;tree.Oid;commit.Oid;input.Ref;input.Tag;lease input.ExpectedRef]@order) |> utf8 |> sha256
        Ok {InputSha256=inputSha;AuthorizationSha256=authSha;Event=event;Head=head;Tree=tree;Commit=commit;Ref=input.Ref;Tag=input.Tag;ExpectedRef=input.ExpectedRef;OperationOrder=order;Seal=seal}
    let verify (plan:LedgerInitializationPlan) (readback:LedgerInitializationReadback) =
        let errors=ResizeArray<string>()
        match readback.Ref with RefAt oid when oid=plan.Commit.Oid -> () | RefAt _ -> errors.Add "ref-conflict" | RefAbsent -> errors.Add "ref-absent" | RefUnknown why -> errors.Add("ref-unknown:"+why)
        match readback.Tag with RefAt oid when oid=plan.Commit.Oid -> () | RefAt _ -> errors.Add "tag-conflict" | RefAbsent -> errors.Add "tag-absent" | RefUnknown why -> errors.Add("tag-unknown:"+why)
        for expected in [plan.Event;plan.Head;plan.Tree;plan.Commit] do
            match readback.Objects |> List.tryFind(fun value->value.Kind=expected.Kind&&value.Oid=expected.Oid) with
            | Some actual when actual.Bytes=expected.Bytes && oid actual.Kind actual.Bytes=actual.Oid -> ()
            | _ -> errors.Add("object:"+expected.Oid)
        if errors.Count=0 then Ok() else Error(List.ofSeq errors)
    let apply (port:LedgerInitializationPort) (plan:LedgerInitializationPlan) =
        let settleTag () =
            match port.ReadRef plan.Tag with
            | RefAt oid when oid=plan.Commit.Oid -> Initialized
            | RefAt _ -> InitializationRefused ["expected-ref-lease-conflict"]
            | RefUnknown why -> InitializationIndeterminate why
            | RefAbsent ->
                let response=port.CreateRef plan.Tag plan.Commit.Oid ExpectedAbsent
                match port.ReadRef plan.Tag,response with
                | RefAt oid,_ when oid=plan.Commit.Oid -> Initialized
                | RefAt _,_ -> InitializationRefused ["tag-conflict"]
                | RefUnknown why,_ -> InitializationIndeterminate why
                | RefAbsent,Error why -> InitializationIndeterminate("tag-create:"+why)
                | RefAbsent,Ok() -> InitializationIndeterminate "tag-lost-response"
        match port.ReadRef plan.Ref,port.ReadRef plan.Tag with
        | RefAt r,RefAt t when r=plan.Commit.Oid&&t=plan.Commit.Oid -> AlreadyInitialized
        | RefAt r,RefAbsent when r=plan.Commit.Oid -> settleTag()
        | RefUnknown why,_|_,RefUnknown why -> InitializationIndeterminate why
        | RefAt _,_|_,RefAt _ -> InitializationRefused ["expected-ref-lease-conflict"]
        | RefAbsent,RefAbsent ->
            match [plan.Event;plan.Head;plan.Tree;plan.Commit] |> List.tryPick(fun o->match port.PutObject o with Error why->Some why | Ok()->None) with
            | Some why -> InitializationRefused ["object-store:"+why]
            | None ->
                let response=port.CreateRef plan.Ref plan.Commit.Oid plan.ExpectedRef
                match port.ReadRef plan.Ref,response with
                | RefAt oid,_ when oid=plan.Commit.Oid -> settleTag()
                | RefAt _,_ -> InitializationRefused ["branch-conflict"]
                | RefUnknown why,_ -> InitializationIndeterminate why
                | RefAbsent,Error why -> InitializationIndeterminate("branch-create:"+why)
                | RefAbsent,Ok() -> InitializationIndeterminate "branch-lost-response"
    let observationEnvelope (plan:LedgerInitializationPlan) =
        json ["commit",JsonValue.Create plan.Commit.Oid;"complete",JsonValue.Create true;"fleetId",JsonValue.Create "fs-gg-production"
              "generation",JsonValue.Create 1L;"genesisCommit",JsonValue.Create plan.Commit.Oid;"parent",null;"ref",JsonValue.Create plan.Ref
              "schema",JsonValue.Create "fsgg.github-substrate.epoch-wire/1";"tag",JsonValue.Create plan.Tag]

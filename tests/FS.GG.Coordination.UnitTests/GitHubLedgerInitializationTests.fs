module FS.GG.Coordination.GitHubLedgerInitializationTests

open System
open System.Diagnostics
open System.Security.Cryptography
open System.Text.Json
open Xunit
open FS.GG.Coordination.GitHub

let private digest c=String.replicate 64 c
let private fixture () =
    use rsa=RSA.Create(2048)
    let pem=rsa.ExportSubjectPublicKeyInfoPem()
    let keyDigest=SHA256.HashData(rsa.ExportSubjectPublicKeyInfo())|>Convert.ToHexString|>_.ToLowerInvariant()
    let input={RepositoryId=1351660651L;Repository="FS-GG/FS.GG.Coordination.Authority";FleetId="fs-gg-production";Ref="refs/heads/fsgg/v2/journal/cutover/d5";Tag="refs/tags/fsgg/v2/fleet-cutover/operating-v1/test";ManifestSha256=digest "1";TrustAnchorSha256=digest "2";SourceSha256=digest "3";DesiredPolicySha256=digest "4";FirstCaptureSha256=digest "5";SecondCaptureSha256=digest "6";AuthorizationKeyId="test";AuthorizationKeySpkiSha256=keyDigest;AuthorizationWorkflowRevision=String.replicate 40 "a";AuthorizationWorkflowSha256=digest "8";CutoverAppId=4882399L;CutoverInstallationId=160261436L;ControlIssueNumber=2L;ExpectedRef=ExpectedAbsent;CreatedAt=DateTimeOffset.Parse("2026-09-09T10:00:00Z");AuthorName="FS.GG cutover";AuthorEmail="cutover@fs.gg"}
    let payload=LedgerInitializationAdapter.canonicalInput input
    let authority={KeyId="test";PublicKeyPem=pem;PublicKeySpkiSha256=keyDigest;Payload=payload;Signature=rsa.SignData(payload,HashAlgorithmName.SHA256,RSASignaturePadding.Pss);AuthorizedAt=input.CreatedAt.AddMinutes(-1.);ExpiresAt=input.CreatedAt.AddMinutes(30.)}
    input,authority,LedgerInitializationAdapter.plan input.CreatedAt authority input|>Result.defaultWith(failwithf "%A")
let private gitOid (object':LedgerObject) =
    let start=ProcessStartInfo("git")
    start.ArgumentList.Add("hash-object")
    start.ArgumentList.Add("-t")
    start.ArgumentList.Add(object'.Kind)
    start.ArgumentList.Add("--stdin")
    start.RedirectStandardInput<-true
    start.RedirectStandardOutput<-true
    start.UseShellExecute<-false
    use child=Process.Start start
    child.StandardInput.BaseStream.Write object'.Bytes
    child.StandardInput.Close()
    let result=child.StandardOutput.ReadToEnd().Trim()
    child.WaitForExit()
    result
[<Fact>]
let ``genesis uses real git objects and observation is not stored bytes`` () =
    let input,_,plan=fixture()
    let canonical=LedgerInitializationAdapter.canonicalInput input|>System.Text.Encoding.UTF8.GetString
    Assert.Contains("authorizationKeySpkiSha256",canonical)
    Assert.Contains("authorizationWorkflowRevision",canonical)
    Assert.DoesNotContain("authorizationKeySha256",canonical)
    for value in [plan.Event;plan.Head;plan.Tree;plan.Commit] do Assert.Equal(value.Oid,gitOid value)
    let envelope=LedgerInitializationAdapter.observationEnvelope plan
    Assert.NotEqual<byte array>(plan.Event.Bytes,envelope)
    use doc=JsonDocument.Parse envelope
    Assert.Equal(JsonValueKind.Null,doc.RootElement.GetProperty("parent").ValueKind)
    Assert.Equal(plan.Commit.Oid,doc.RootElement.GetProperty("genesisCommit").GetString())
[<Fact>]
let ``apply is idempotent and refuses competing leases`` () =
    let _,_,plan=fixture()
    let mutable refs=Map.empty
    let objects=ResizeArray()
    let port=
        { ReadRef=fun name->Map.tryFind name refs|>Option.map RefAt|>Option.defaultValue RefAbsent
          PutObject=fun value->objects.Add value;Ok()
          CreateRef=fun name oid expected->
              match expected,Map.tryFind name refs with
              | ExpectedAbsent,None->refs<-Map.add name oid refs;Ok()
              | _->Error "lease" }
    Assert.Equal(Initialized,LedgerInitializationAdapter.apply port plan)
    Assert.Equal(AlreadyInitialized,LedgerInitializationAdapter.apply port plan)
    let readback={Ref=RefAt plan.Commit.Oid;Tag=RefAt plan.Commit.Oid;Objects=List.ofSeq objects}
    Assert.Equal(Ok(),LedgerInitializationAdapter.verify plan readback)
    refs<-Map.ofList[plan.Ref,plan.Commit.Oid]
    Assert.Equal(Initialized,LedgerInitializationAdapter.apply port plan)
    refs<-Map.ofList[plan.Ref,String.replicate 40 "f"]
    Assert.Equal(InitializationRefused ["expected-ref-lease-conflict"],LedgerInitializationAdapter.apply port plan)

[<Fact>]
let ``lost create responses are settled by exact ref reread`` () =
    let _,_,plan=fixture()
    let mutable refs=Map.empty
    let port=
        { ReadRef=fun name->Map.tryFind name refs|>Option.map RefAt|>Option.defaultValue RefAbsent
          PutObject=fun _->Ok()
          CreateRef=fun name oid _->refs<-Map.add name oid refs;Error "response-lost" }
    Assert.Equal(Initialized,LedgerInitializationAdapter.apply port plan)
    Assert.Equal(AlreadyInitialized,LedgerInitializationAdapter.apply port plan)
[<Fact>]
let ``authorization expiry and changed binding refuse planning`` () =
    let input,authority,_=fixture()
    Assert.True(LedgerInitializationAdapter.plan authority.ExpiresAt authority input|>Result.isError)
    Assert.True(LedgerInitializationAdapter.plan input.CreatedAt authority {input with ControlIssueNumber=3L}|>Result.isError)
    Assert.True(LedgerInitializationAdapter.plan input.CreatedAt authority {input with AuthorizationKeySpkiSha256=digest "9"}|>Result.isError)
    Assert.True(LedgerInitializationAdapter.plan input.CreatedAt authority {input with AuthorizationWorkflowRevision="main"}|>Result.isError)

let private operationalFixture () =
    use rsa=RSA.Create(2048)
    let observed=DateTimeOffset.Parse("2026-09-09T10:00:00Z")
    let pem=rsa.ExportSubjectPublicKeyInfoPem()
    let keyDigest=SHA256.HashData(rsa.ExportSubjectPublicKeyInfo())|>Convert.ToHexString|>_.ToLowerInvariant()
    let context={RepositoryId=1351660651L;FleetRef="refs/heads/fsgg/v2/journal/cutover/d5";ControlIssueNumber=2L;DesiredPolicySha256=digest "4";ProviderObservationSha256=digest "5";InitializationSeal=Some(digest "6");MonitorStoreId=Some "private-store-1";SignerPublicKeySpkiSha256=keyDigest}
    let signed dimension =
        let unsigned={Dimension=dimension;Context=context;RunId="run-17";InputSha256=digest "7";ObservedAt=observed;ExpiresAt=observed.AddMinutes(15.);Authority="FS-GG/fleet-cutover";SignerKeyId="test-operational";PublicKeyPem=pem;PublicKeySpkiSha256=keyDigest;Payload=[||];Signature=[||]}
        let payload=LedgerOperationalEvidence.canonicalPayload unsigned
        {unsigned with Payload=payload;Signature=rsa.SignData(payload,HashAlgorithmName.SHA256,RSASignaturePadding.Pss)}
    context,observed,[SettingsAppliedEvidence;AppCustodyEvidence;FleetInitializedEvidence;MonitoringEvidence]|>List.map signed

[<Fact>]
let ``operational state is derived only from complete signed evidence`` () =
    let context,observed,evidence=operationalFixture()
    let state=LedgerOperationalEvidence.derive (observed.AddMinutes(1.)) context evidence
    Assert.Equal(LedgerObservation.Observed true,state.SettingsApplied)
    Assert.Equal(LedgerObservation.Observed true,state.AppCustodyReady)
    Assert.Equal(LedgerObservation.Observed true,state.FleetInitialized)
    Assert.Equal(LedgerObservation.Observed true,state.MonitoringReady)

[<Fact>]
let ``missing duplicate and stale operational evidence remain explicit`` () =
    let context,observed,evidence=operationalFixture()
    let duplicate=(List.head evidence)::evidence
    let duplicated=LedgerOperationalEvidence.derive (observed.AddMinutes(1.)) context duplicate
    Assert.Equal(LedgerObservation.Unknown "invalid-or-duplicate-operational-evidence",duplicated.SettingsApplied)
    let missing=LedgerOperationalEvidence.derive (observed.AddMinutes(1.)) context (evidence|>List.filter(fun item->item.Dimension<>MonitoringEvidence))
    Assert.Equal(LedgerObservation.Unknown "missing-operational-evidence",missing.MonitoringReady)
    let stale=LedgerOperationalEvidence.derive (observed.AddMinutes(16.)) context evidence
    Assert.Equal(LedgerObservation.Unknown "invalid-or-duplicate-operational-evidence",stale.FleetInitialized)
    let wrongSigner=LedgerOperationalEvidence.derive (observed.AddMinutes(1.)) {context with SignerPublicKeySpkiSha256=digest "9"} evidence
    Assert.Equal(LedgerObservation.Unknown "invalid-or-duplicate-operational-evidence",wrongSigner.AppCustodyReady)

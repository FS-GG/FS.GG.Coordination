namespace FS.GG.Coordination.GitHub

open System
open System.Security.Cryptography
open System.Text
open System.Text.Json.Nodes

type LedgerOperationalDimension = SettingsAppliedEvidence | AppCustodyEvidence | FleetInitializedEvidence | MonitoringEvidence
type LedgerOperationalEvidenceContext = { RepositoryId:int64;FleetRef:string;ControlIssueNumber:int64;DesiredPolicySha256:string;ProviderObservationSha256:string;InitializationSeal:string option;MonitorStoreId:string option;SignerPublicKeySha256:string }
type LedgerOperationalEvidence =
    { Dimension:LedgerOperationalDimension;Context:LedgerOperationalEvidenceContext;RunId:string;InputSha256:string;ObservedAt:DateTimeOffset;ExpiresAt:DateTimeOffset
      Authority:string;SignerKeyId:string;PublicKeyPem:string;PublicKeySha256:string;Payload:byte array;Signature:byte array }

[<RequireQualifiedAccess>]
module LedgerOperationalEvidence =
    let private sha (bytes:byte array)=SHA256.HashData bytes|>Convert.ToHexString|>_.ToLowerInvariant()
    let private name=function SettingsAppliedEvidence->"settings"|AppCustodyEvidence->"custody"|FleetInitializedEvidence->"initialization"|MonitoringEvidence->"monitoring"
    let canonicalPayload (evidence:LedgerOperationalEvidence) =
        let c=evidence.Context
        let root=JsonObject()
        let optional (value:string option) = match value with Some text -> JsonValue.Create(text) :> JsonNode | None -> null
        let values : (string*JsonNode) list =
            [ "authority",JsonValue.Create(evidence.Authority)
              "controlIssueNumber",JsonValue.Create(c.ControlIssueNumber)
              "desiredPolicySha256",JsonValue.Create(c.DesiredPolicySha256)
              "dimension",JsonValue.Create(name evidence.Dimension)
              "expiresAt",JsonValue.Create(evidence.ExpiresAt.ToUniversalTime().ToString("O"))
              "fleetRef",JsonValue.Create(c.FleetRef)
              "initializationSeal",optional c.InitializationSeal
              "inputSha256",JsonValue.Create(evidence.InputSha256)
              "monitorStoreId",optional c.MonitorStoreId
              "observedAt",JsonValue.Create(evidence.ObservedAt.ToUniversalTime().ToString("O"))
              "providerObservationSha256",JsonValue.Create(c.ProviderObservationSha256)
              "repositoryId",JsonValue.Create(c.RepositoryId)
              "runId",JsonValue.Create(evidence.RunId) ]
        root.Add("signerPublicKeySha256",JsonValue.Create(c.SignerPublicKeySha256))
        values |> List.sortBy fst |> List.iter(fun(k,v)->root.Add(k,v))
        root.ToJsonString() |> ShardedJournalAdapter.canonicalJson |> Result.defaultWith invalidOp
    let private valid asOf (expected:LedgerOperationalEvidenceContext) (evidence:LedgerOperationalEvidence) =
        try
            use rsa=RSA.Create()
            rsa.ImportFromPem evidence.PublicKeyPem
            evidence.Context=expected && evidence.Payload=canonicalPayload evidence && evidence.ObservedAt<=asOf && asOf<evidence.ExpiresAt
            && evidence.ExpiresAt-evidence.ObservedAt<=TimeSpan.FromMinutes 15. && sha(Encoding.UTF8.GetBytes evidence.PublicKeyPem)=evidence.PublicKeySha256
            && evidence.PublicKeySha256=expected.SignerPublicKeySha256 && evidence.Authority="FS-GG/fleet-cutover" && not(String.IsNullOrWhiteSpace evidence.RunId) && evidence.InputSha256.Length=64
            && rsa.VerifyData(evidence.Payload,evidence.Signature,HashAlgorithmName.SHA256,RSASignaturePadding.Pss)
        with _->false
    let derive asOf expected (evidence:LedgerOperationalEvidence list) =
        let one dimension =
            let matches=evidence|>List.filter(fun item->item.Dimension=dimension)
            match matches with [item] when valid asOf expected item->Observed true | []->Unknown "missing-operational-evidence" | _->Unknown "invalid-or-duplicate-operational-evidence"
        {SettingsApplied=one SettingsAppliedEvidence;AppCustodyReady=one AppCustodyEvidence;FleetInitialized=one FleetInitializedEvidence;MonitoringReady=one MonitoringEvidence}

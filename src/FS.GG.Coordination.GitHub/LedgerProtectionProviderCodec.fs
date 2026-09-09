namespace FS.GG.Coordination.GitHub

open System
open System.IO
open System.Security.Cryptography
open System.Text
open System.Text.Json

type LedgerCaptureContinuity = Uninitialized | Matched | Drift
type LedgerProtectionCapture =
    { CapturePass: int; CapturedAt: DateTimeOffset; Continuity: LedgerCaptureContinuity
      PreviousEvidenceSha256: string option; RawSetSha256: string; NormalizedSetSha256: string
      Gaps: string list; Observation: LedgerProviderObservation; Conformance: LedgerProtectionConformanceSnapshot }

module LedgerProtectionProviderCodec =
    let private sha (bytes:byte array) = SHA256.HashData bytes |> Convert.ToHexString |> _.ToLowerInvariant()
    let private frame (value:string) = $"{Encoding.UTF8.GetByteCount value}:{value}"
    let private digestLike (value:string) = value.Length=64 && value |> Seq.forall Uri.IsHexDigit
    let private requiredString (name:string) (value:JsonElement) = value.GetProperty(name).GetString()
    let private optionalInt64 (name:string) (value:JsonElement) =
        let mutable child = Unchecked.defaultof<JsonElement>
        if value.TryGetProperty(name,&child) && child.ValueKind=JsonValueKind.Number then Some(child.GetInt64()) else None
    let private canonicalBytes (element:JsonElement) =
        use stream = new MemoryStream()
        use writer = new Utf8JsonWriter(stream)
        let rec write (value:JsonElement) =
            match value.ValueKind with
            | JsonValueKind.Object ->
                writer.WriteStartObject()
                value.EnumerateObject() |> Seq.sortBy _.Name |> Seq.iter (fun property -> writer.WritePropertyName(property.Name); write property.Value)
                writer.WriteEndObject()
            | JsonValueKind.Array -> writer.WriteStartArray(); value.EnumerateArray() |> Seq.iter write; writer.WriteEndArray()
            | _ -> value.WriteTo writer
        write element; writer.Flush(); stream.ToArray()
    let private rule = function "creation" -> Some Creation | "update" -> Some Update | "deletion" -> Some Deletion | "non_fast_forward" -> Some NonFastForward | _ -> None
    let private ruleset (value:JsonElement) =
        let target = match requiredString "target" value with "branch" -> Some Branch | "tag" -> Some Tag | _ -> None
        let enforcement = match requiredString "enforcement" value with "active" -> Some Active | "evaluate" -> Some Evaluate | "disabled" -> Some Disabled | _ -> None
        let rules = value.GetProperty("rules").EnumerateArray() |> Seq.map _.GetString() |> Seq.map rule |> Seq.toList
        let bypass =
            value.GetProperty("bypassActors").EnumerateArray()
            |> Seq.map (fun actor ->
                match requiredString "actorType" actor, actor.GetProperty("actorId").GetInt64(), requiredString "bypassMode" actor with
                | "Integration", id, "always" -> Some {Actor=App id;Mode=Always}
                | "OrganizationAdmin", _, "always" -> Some {Actor=OrganizationAdmin;Mode=Always}
                | "Integration", id, "pull_request" -> Some {Actor=App id;Mode=PullRequest}
                | _ -> None) |> Seq.toList
        if target.IsNone || enforcement.IsNone || rules |> List.exists Option.isNone || bypass |> List.exists Option.isNone then Error "capture-ruleset-enum"
        else Ok { Id=value.GetProperty("id").GetInt64(); Name=requiredString "name" value; Target=target.Value; Enforcement=enforcement.Value; Inherited=value.GetProperty("inherited").GetBoolean()
                  Includes=value.GetProperty("include").EnumerateArray() |> Seq.map _.GetString() |> Seq.toList
                  Excludes=value.GetProperty("exclude").EnumerateArray() |> Seq.map _.GetString() |> Seq.toList
                  Rules=rules |> List.choose id; Bypass=bypass |> List.choose id }
    let decode (bytes:ReadOnlyMemory<byte>) =
        try
            use document = JsonDocument.Parse bytes
            let root:JsonElement = document.RootElement
            let errors = ResizeArray<string>()
            if requiredString "schema" root <> "fsgg.github-ledger-protection-live-capture/v1" then errors.Add "capture-schema"
            if requiredString "repository" root <> LedgerProtectionPlanAdapter.authorityRepository || root.GetProperty("repositoryId").GetInt64() <> LedgerProtectionPlanAdapter.authorityRepositoryId then errors.Add "capture-authority"
            if root.GetProperty("applyAuthorized").GetBoolean() || root.GetProperty("writesAttempted").GetInt32()<>0 then errors.Add "capture-effect-boundary"
            let resources = root.GetProperty("resources").EnumerateArray() |> Seq.toList
            let expected = ["authority-issues";"authority-repository";"authority-revision";"branch-protection-rules";"classic-branch-protection";"effective-branch-rules";"environments";"fleet-head-refs";"organization-installations";"phase-tags";"rulesets"]
            let ids = resources |> List.map (requiredString "id")
            if List.sort ids <> expected || (ids |> List.distinct |> List.length)<>expected.Length then errors.Add "capture-resource-inventory"
            for resource in resources do
                let normalized = resource.GetProperty("normalized")
                if sha (canonicalBytes normalized) <> requiredString "normalizedSha256" resource then errors.Add("capture-normalized-digest:"+requiredString "id" resource)
                let id = requiredString "id" resource
                let state = requiredString "state" resource
                let allowedAbsence = (id="classic-branch-protection" || id="effective-branch-rules") && state="proven-absent"
                if not (resource.GetProperty("pagesComplete").GetBoolean()) || (state<>"observed" && not allowedAbsence) then errors.Add("capture-resource-state:"+id)
            let aggregate field = resources |> List.sortBy (requiredString "id") |> List.map (fun value -> frame(requiredString "id" value)+frame(requiredString field value)) |> String.concat "" |> Encoding.UTF8.GetBytes |> sha
            let rawSet = requiredString "rawSetSha256" root
            let normalizedSet = requiredString "normalizedSetSha256" root
            if aggregate "rawSha256" <> rawSet then errors.Add "capture-raw-set-digest"
            if aggregate "normalizedSha256" <> normalizedSet then errors.Add "capture-normalized-set-digest"
            let find id = resources |> List.find (fun value -> requiredString "id" value=id) |> _.GetProperty("normalized")
            let ruleResults = find "rulesets" |> _.EnumerateArray() |> Seq.map ruleset |> Seq.toList
            for result in ruleResults do match result with Error code -> errors.Add code | _ -> ()
            let gitRefs id =
                find id |> _.EnumerateArray() |> Seq.map (fun value ->
                    if value.ValueKind=JsonValueKind.String then {Name=value.GetString();ObjectSha=""}
                    else {Name=requiredString "name" value;ObjectSha=requiredString "objectSha" value}) |> Seq.toList
            let fleetHeads = gitRefs "fleet-head-refs"
            let phaseRefs = gitRefs "phase-tags"
            let tags = phaseRefs |> List.map _.Name
            let environmentValue = find "environments"
            let environmentNames = environmentValue.GetProperty("names").EnumerateArray() |> Seq.map _.GetString() |> Seq.toList
            let installationValues =
                find "organization-installations" |> _.EnumerateArray()
                |> Seq.map (fun value ->
                    let permissions = value.GetProperty("permissions").EnumerateObject() |> Seq.map (fun p -> p.Name,p.Value.GetString()) |> Seq.toList
                    let installationId = value.GetProperty("installationId").GetInt64()
                    let selected = value.GetProperty("selectedRepositories")
                    let endpoint,complete,repositories =
                        if selected.ValueKind=JsonValueKind.Array then
                            Some(LedgerProtectionProviderAdapter.selectedRepositoriesEndpoint installationId),true,Observed(selected.EnumerateArray() |> Seq.map _.GetString() |> Seq.toList)
                        else None,false,Unknown "selected-repositories-unbound"
                    { InstallationId=installationId; AppId=value.GetProperty("appId").GetInt64(); Slug=requiredString "slug" value; RepositorySelection=requiredString "repositorySelection" value
                      Permissions=permissions; SelectedRepositoriesEndpoint=endpoint; SelectedRepositoriesPagesComplete=complete; SelectedRepositories=repositories }) |> Seq.toList
            let issueValues = find "authority-issues" |> _.EnumerateArray() |> Seq.map (fun value -> {Number=value.GetProperty("number").GetInt64();IsPullRequest=value.GetProperty("isPullRequest").GetBoolean()}) |> Seq.toList
            let capturedAt = DateTimeOffset.Parse(requiredString "capturedAt" root)
            let pass = root.GetProperty("capturePass").GetInt32()
            let continuity = match requiredString "continuity" root with "uninitialized" -> Uninitialized | "matched" -> Matched | _ -> Drift
            let previous = let mutable value=Unchecked.defaultof<JsonElement> in if root.TryGetProperty("previousEvidenceSha256",&value) && value.ValueKind=JsonValueKind.String then Some(value.GetString()) else None
            let mkPage endpoint payload = {Endpoint=endpoint;Page=1;LastPage=1;IsTerminal=true;HttpStatus=200;ObservedAt=capturedAt;PayloadSha256=LedgerProtectionProviderAdapter.payloadSha256 payload;Payload=payload}
            let observation =
                { SchemaVersion=1; Repository=LedgerProtectionPlanAdapter.authorityRepository; RepositoryId=LedgerProtectionPlanAdapter.authorityRepositoryId; Revision=requiredString "revision" root
                  PreviousObservationSha256=previous; PreviousObservationEvidenceSha256=previous; RawSetSha256=Some rawSet; NormalizedSetSha256=Some normalizedSet; DedicatedWriterAppId=None; ControlIssueNumber=None
                  Pages=[mkPage LedgerProtectionProviderAdapter.rulesetsEndpoint (RulesetsPage(ruleResults |> List.choose Result.toOption));mkPage LedgerProtectionProviderAdapter.phaseTagsEndpoint (PhaseTagsPage tags);mkPage LedgerProtectionProviderAdapter.environmentEndpoint (EnvironmentsPage environmentNames);mkPage LedgerProtectionProviderAdapter.installationsEndpoint (OrganizationInstallationsPage installationValues);mkPage LedgerProtectionProviderAdapter.controlIssuesEndpoint (ControlIssuesPage issueValues)] }
            let effectiveRules = find "effective-branch-rules" |> _.EnumerateArray() |> Seq.map (requiredString "type" >> rule) |> Seq.toList
            if effectiveRules |> List.exists Option.isNone then errors.Add "capture-effective-rule-enum"
            let classicResource = resources |> List.find (fun value -> requiredString "id" value="classic-branch-protection")
            let classic = if requiredString "state" classicResource="proven-absent" then ProvenAbsent else Observed true
            let fleetEnvironment =
                let detail = environmentValue.GetProperty("fleetCutover")
                if detail.ValueKind=JsonValueKind.Null then ProvenAbsent
                else
                    Observed
                        { Repository="FS-GG/.github"; Name=requiredString "name" detail
                          ReviewerIds=detail.GetProperty("reviewerIds").EnumerateArray() |> Seq.map _.GetInt64() |> Seq.toList
                          PreventSelfReview=detail.GetProperty("preventSelfReview").GetBoolean();CanAdminsBypass=detail.GetProperty("canAdminsBypass").GetBoolean()
                          ProtectedBranches=detail.GetProperty("protectedBranches").GetBoolean();CustomBranchPolicies=detail.GetProperty("customBranchPolicies").GetBoolean()
                          DeploymentBranchPatterns=detail.GetProperty("deploymentBranchPatterns").EnumerateArray() |> Seq.map _.GetString() |> Seq.toList }
            let branchPatterns = find "branch-protection-rules" |> _.EnumerateArray() |> Seq.map (requiredString "pattern") |> Seq.toList
            let conformance =
                { Provider=observation;FleetHeads=Observed fleetHeads;PhaseTags=Observed phaseRefs
                  EffectiveRules=Observed(effectiveRules |> List.choose id);ClassicProtection=classic;FleetEnvironment=fleetEnvironment
                  BranchProtectionRulePatterns=Observed branchPatterns;Bindings={OrdinaryWriterAppId=None;CutoverWriterAppId=None;ControlIssueNumber=None}
                  Operational={SettingsApplied=Unknown "not-observed";AppCustodyReady=Unknown "not-observed";FleetInitialized=Unknown "not-observed";MonitoringReady=Unknown "not-observed"} }
            let gaps = root.GetProperty("gaps").EnumerateArray() |> Seq.map _.GetString() |> Seq.toList
            if not gaps.IsEmpty then errors.AddRange(gaps |> List.map (fun value -> "capture-gap:"+value))
            if errors.Count>0 then Error(List.ofSeq errors) else Ok {CapturePass=pass;CapturedAt=capturedAt;Continuity=continuity;PreviousEvidenceSha256=previous;RawSetSha256=rawSet;NormalizedSetSha256=normalizedSet;Gaps=gaps;Observation=observation;Conformance=conformance}
        with error -> Error ["capture-unreadable:"+error.GetType().Name]
    let qualifiedObservation capture =
        match capture.CapturePass,capture.Continuity,capture.PreviousEvidenceSha256 with
        | 2,Matched,Some digest when digestLike digest && capture.Gaps.IsEmpty -> Ok capture.Observation
        | 1,Uninitialized,None -> Error ["capture-continuity-uninitialized"]
        | _ -> Error ["capture-continuity-refused"]

namespace FS.GG.Coord.Tests

open System
open Xunit
open FS.GG.Coord.SelfHost

module SelfHostTests =
    let private evidence =
        { Build = "trx:build"
          Unit = "trx:unit"
          FocusedProductionRoute = "trx:route"
          Provenance = "sha256:provenance"
          Inversion = "mutation:shared-refusal" }

    let private acceptance =
        { Actor = "host/ron000"
          AcceptedAt = DateTimeOffset.Parse "2026-08-22T18:00:00Z" }

    let private create reason =
        createReceipt
            "base-sha"
            "candidate-head"
            (String.replicate 64 "a")
            "1.2.0-candidate"
            "shared engine cannot decode self-host-bootstrap/v1"
            (String.replicate 64 "c")
            reason
            evidence
            "decision-key"
            "action-key"
            acceptance
        |> Result.defaultWith (String.concat "; " >> failwith)

    [<Theory>]
    [<InlineData("new-schema-case")>]
    [<InlineData("relocated-decision-boundary")>]
    let ``every enumerated bootstrap reason round-trips through the stable verifier`` reasonName =
        let reason =
            if reasonName = "new-schema-case" then
                BootstrapReason.NewSchemaCase
            else
                BootstrapReason.RelocatedDecisionBoundary
        let receipt = create reason
        Assert.Equal(Ok (), authorizeWrite receipt)
        match receipt |> encodeReceipt |> tryDecodeReceipt with
        | Ok(Some decoded) -> Assert.Equal(receipt, decoded)
        | other -> failwithf "expected verified receipt, got %A" other

    [<Fact>]
    let ``unknown and business-rule disagreement reasons are refused`` () =
        let body =
            create BootstrapReason.NewSchemaCase
            |> encodeReceipt
            |> fun value -> value.Replace("new-schema-case", "business-rule-disagreement")
        match tryDecodeReceipt body with
        | Error errors -> Assert.Contains("unknown", String.concat "; " errors)
        | other -> failwithf "expected closed-vocabulary refusal, got %A" other

    [<Fact>]
    let ``digest binds candidate bytes version heads refusal evidence decision action and host`` () =
        let receipt = create BootstrapReason.RelocatedDecisionBoundary
        let mutations =
            [ { receipt with BaseSha = "other-base" }
              { receipt with CandidateHeadSha = "other-head" }
              { receipt with CandidateBinarySha256 = String.replicate 64 "b" }
              { receipt with CandidateVersion = "other-version" }
              { receipt with SharedRefusal = "other refusal" }
              { receipt with SnapshotSha256 = String.replicate 64 "d" }
              { receipt with Evidence = { receipt.Evidence with Unit = "other unit evidence" } }
              { receipt with CandidateDecisionKey = "other-decision" }
              { receipt with CandidateActionKey = "other-action" }
              { receipt with HostAcceptance = { receipt.HostAcceptance with Actor = "other-host" } } ]
        for mutation in mutations do
            match authorizeWrite mutation with
            | Error errors -> Assert.Contains("digest", String.concat "; " errors)
            | Ok () -> failwith "a bound receipt field was mutable without invalidating authority"

    [<Fact>]
    let ``incomplete evidence and missing accountable host acceptance mint no receipt`` () =
        let attempt evidence acceptance =
            createReceipt
                "base"
                "head"
                (String.replicate 64 "a")
                "version"
                "refusal"
                (String.replicate 64 "c")
                BootstrapReason.NewSchemaCase
                evidence
                "decision"
                "action"
                acceptance
        match attempt { evidence with Inversion = "" } acceptance with
        | Error errors -> Assert.Contains("inversion evidence", String.concat "; " errors)
        | Ok _ -> failwith "incomplete evidence minted authority"
        match attempt evidence { acceptance with Actor = "" } with
        | Error errors -> Assert.Contains("host acceptance", String.concat "; " errors)
        | Ok _ -> failwith "anonymous acceptance minted authority"

    [<Fact>]
    let ``post-merge replay disagreement blocks completion and release`` () =
        let receipt = create BootstrapReason.NewSchemaCase
        Assert.Equal(Ok (), verifyReplay receipt { DecisionKey = "decision-key"; ActionKey = "action-key" })
        match verifyReplay receipt { DecisionKey = "changed"; ActionKey = "action-key" } with
        | Error errors -> Assert.Contains("decision key disagrees", String.concat "; " errors)
        | Ok () -> failwith "decision disagreement passed replay"
        match verifyReplay receipt { DecisionKey = "decision-key"; ActionKey = "changed" } with
        | Error errors -> Assert.Contains("action key disagrees", String.concat "; " errors)
        | Ok () -> failwith "action disagreement passed replay"

    [<Fact>]
    let ``durable replay is bound to bootstrap snapshot decision action and time`` () =
        let bootstrap = create BootstrapReason.NewSchemaCase
        let replay =
            createReplayReceipt
                bootstrap
                bootstrap.SnapshotSha256
                { DecisionKey = "decision-key"; ActionKey = "action-key" }
                (DateTimeOffset.Parse "2026-08-22T19:00:00Z")
            |> Result.defaultWith (String.concat "; " >> failwith)
        Assert.Equal(Ok (), verifyReplayReceipt bootstrap replay)
        match replay |> encodeReplayReceipt |> tryDecodeReplayReceipt with
        | Ok(Some decoded) -> Assert.Equal(replay, decoded)
        | other -> failwithf "expected verified replay receipt, got %A" other
        for mutation in
            [ { replay with BootstrapDigest = String.replicate 64 "b" }
              { replay with SnapshotSha256 = String.replicate 64 "d" }
              { replay with DecisionKey = "different" }
              { replay with ActionKey = "different" }
              { replay with ReplayedAt = replay.ReplayedAt.AddSeconds 1.0 } ] do
            match verifyReplayReceipt bootstrap mutation with
            | Error _ -> ()
            | Ok () -> failwith "a replay authority field was mutable without invalidating the digest"

    [<Fact>]
    let ``bootstrap presence requires exactly one agreeing replay before completion`` () =
        let bootstrap = create BootstrapReason.NewSchemaCase
        let bootstrapBody = encodeReceipt bootstrap
        match replayState [] with
        | NoBootstrap -> ()
        | other -> failwithf "ordinary items should be unaffected, got %A" other
        match replayState [ bootstrapBody ] with
        | ReplayRequired receipt -> Assert.Equal(bootstrap.Digest, receipt.Digest)
        | other -> failwithf "bootstrap did not require replay, got %A" other
        let replay =
            createReplayReceipt bootstrap bootstrap.SnapshotSha256
                { DecisionKey = "decision-key"; ActionKey = "action-key" }
                (DateTimeOffset.Parse "2026-08-22T19:00:00Z")
            |> Result.defaultWith (String.concat "; " >> failwith)
        let replayBody = encodeReplayReceipt replay
        match replayState [ bootstrapBody; replayBody ] with
        | VerifiedReplay receipt -> Assert.Equal(replay.Digest, receipt.Digest)
        | other -> failwithf "agreeing replay was not completion authority, got %A" other
        match replayState [ replayBody ] with
        | InvalidReplay errors -> Assert.Contains("without bootstrap", String.concat "; " errors)
        | other -> failwithf "orphan replay was accepted, got %A" other
        match replayState [ bootstrapBody; replayBody; replayBody ] with
        | InvalidReplay errors -> Assert.Contains("more than one self-host replay", String.concat "; " errors)
        | other -> failwithf "duplicate replay was accepted, got %A" other

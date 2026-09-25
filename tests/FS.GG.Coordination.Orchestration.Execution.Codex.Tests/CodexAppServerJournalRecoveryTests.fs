namespace FS.GG.Coordination.Orchestration.Execution.Codex.Tests

open System
open System.IO
open System.Security.Cryptography
open FS.GG.Coordination.Orchestration.Execution.Codex
open Xunit

type CodexAppServerJournalRecoveryTests() =
    let scope: DirectSessionTurnScope =
        { WorkspaceId = "main-fsharp-dev"
          Repository = "FS-GG/.github"
          ItemId = "work-item-v1-" + String.replicate 64 "b"
          IssueRef = "FS-GG/.github#123"
          AttemptId = "attempt-1"
          InvocationId = "invocation-" + String.replicate 64 "a"
          ThreadId = "native-thread"
          SourceIdentity = "coordination"
          ProducerId = "fsharp-dev-main"
          BindingDigest = String.replicate 64 "c" }
    let binding =
        { Scope = scope
          TurnId = "native-turn"
          TransportIdentity = "authenticated-local-app-server"
          ConnectionId = "connection-1"
          SubscriptionDigest = String.replicate 64 "d"
          ProtocolVersion = "codex-app-server-v2/0.156.1" }
    let fixture name =
        Path.Combine(AppContext.BaseDirectory, "Fixtures", "app-server", name)
        |> File.ReadAllBytes
    let started = fixture "turn-started.json"
    let usage = fixture "usage-updated.json"
    let completed = fixture "turn-completed.json"
    let frameEvent ordinal (bytes: byte array) =
        let digest = SHA256.HashData bytes |> Convert.ToHexString |> fun value -> value.ToLowerInvariant()
        ObservedFrame(ordinal, binding.TransportIdentity, binding.ConnectionId,
                      Convert.ToBase64String bytes, digest)
    let receipt ordinal prior event =
        { Append = { Binding = binding; PreviousEntryId = prior; Event = event }
          EntryId = sprintf "entry-%d" ordinal }
    let first = receipt 1L None (frameEvent 1L started)
    let second = receipt 2L (Some first.EntryId) (frameEvent 2L usage)
    let third = receipt 3L (Some second.EntryId) (frameEvent 3L completed)
    let seal entries =
        { Binding = binding
          EntryCount = List.length entries
          HeadEntryId = (List.last entries).EntryId
          SealId = "sealed-store-head-1" }
    let snapshot entries = { Seal = seal entries; Entries = entries }
    let authenticator =
        { new ICodexAppServerSubscriptionAuthenticator with
            member _.ReadBoundSubscription() = Ok binding }
    let source result =
        { new ICodexAppServerJournalRecoverySource with
            member _.ReadSealedSnapshot _ = result }
    let recover candidate =
        CodexAppServerJournalRecovery.recover scope binding.TurnId binding.TransportIdentity
            authenticator (source (Ok candidate))

    [<Fact>]
    member _.``sealed authored start usage terminal chain yields only provisional status``() =
        Assert.Equal(Ok(ProvisionalTerminal("completed", 1)), recover (snapshot [ first; second; third ]))

    [<Fact>]
    member _.``omitted tail and missing terminal refuse``() =
        let complete = snapshot [ first; second; third ]
        Assert.Equal(
            Error "app-server-recovery-count-mismatch",
            recover { complete with Entries = [ first; second ] }
        )
        Assert.Equal(
            Error "app-server-recovery-head-mismatch",
            recover { complete with Seal = { complete.Seal with EntryCount = 2 }; Entries = [ first; second ] }
        )
        Assert.Equal(
            Error "app-server-recovery-terminal-missing",
            recover (snapshot [ first; second ])
        )

    [<Fact>]
    member _.``broken predecessor duplicate ID and missing ordinal refuse``() =
        let broken = { second with Append = { second.Append with PreviousEntryId = None } }
        Assert.Equal(Error "app-server-recovery-chain-invalid", recover (snapshot [ first; broken; third ]))
        let duplicate = { second with EntryId = first.EntryId }
        Assert.Equal(Error "app-server-recovery-chain-invalid", recover (snapshot [ first; duplicate; third ]))
        let skipped = { second with Append = { second.Append with Event = frameEvent 3L usage } }
        Assert.Equal(Error "app-server-recovery-chain-invalid", recover (snapshot [ first; skipped; third ]))
        let missingEvent =
            { second with Append = { second.Append with Event = Unchecked.defaultof<CodexAppServerJournalEvent> } }
        Assert.Equal(Error "app-server-recovery-chain-invalid", recover (snapshot [ first; missingEvent; third ]))

    [<Fact>]
    member _.``foreign binding or seal cannot substitute workspace item``() =
        let complete = snapshot [ first; second; third ]
        let foreignScope = { scope with WorkspaceId = "other-workspace"; ItemId = "other-item" }
        let foreignBinding = { binding with Scope = foreignScope }
        Assert.Equal(
            Error "app-server-recovery-seal-invalid",
            recover { complete with Seal = { complete.Seal with Binding = foreignBinding } }
        )
        let foreignFirst = { first with Append = { first.Append with Binding = foreignBinding } }
        Assert.Equal(
            Error "app-server-recovery-chain-invalid",
            recover { complete with Entries = [ foreignFirst; second; third ] }
        )

    [<Fact>]
    member _.``changed bytes and noncanonical base64 refuse``() =
        let changed =
            match second.Append.Event with
            | ObservedFrame(ordinal, transport, connection, _, digest) ->
                ObservedFrame(ordinal, transport, connection,
                              Convert.ToBase64String(Array.append usage [| byte ' ' |]), digest)
            | _ -> failwith "expected usage frame"
        let tampered = { second with Append = { second.Append with Event = changed } }
        Assert.Equal(
            Error "app-server-recovery-frame-digest-mismatch",
            recover (snapshot [ first; tampered; third ])
        )
        let noncanonical =
            match second.Append.Event with
            | ObservedFrame(ordinal, transport, connection, encoded, digest) ->
                ObservedFrame(ordinal, transport, connection, encoded + " ", digest)
            | _ -> failwith "expected usage frame"
        let invalid = { second with Append = { second.Append with Event = noncanonical } }
        Assert.Equal(Error "app-server-recovery-frame-invalid", recover (snapshot [ first; invalid; third ]))

    [<Fact>]
    member _.``wrong event order and wrong native turn cannot become terminal``() =
        let usageFirst = { first with Append = { first.Append with Event = frameEvent 1L usage } }
        Assert.Equal(
            Ok(ProvisionalGap "app-server-continuity-start-missing"),
            recover (snapshot [ usageFirst ])
        )
        let wrongNativeTurn =
            System.Text.Encoding.UTF8.GetString(completed).Replace("native-turn", "other-turn", StringComparison.Ordinal)
            |> System.Text.Encoding.UTF8.GetBytes
        let wrongTerminal = { third with Append = { third.Append with Event = frameEvent 3L wrongNativeTurn } }
        Assert.Equal(
            Ok(ProvisionalGap "app-server-turn-identity-mismatch"),
            recover (snapshot [ first; second; wrongTerminal ])
        )

    [<Fact>]
    member _.``retained gap and disconnect remain gaps and reject later entries``() =
        let gap = receipt 2L (Some first.EntryId) (ObservedGap(2L, "app-server-journal-sequence-gap"))
        Assert.Equal(
            Ok(ProvisionalGap "app-server-journal-sequence-gap"),
            recover (snapshot [ first; gap ])
        )
        let afterGap = receipt 3L (Some gap.EntryId) (frameEvent 3L completed)
        Assert.Equal(Error "app-server-recovery-after-gap", recover (snapshot [ first; gap; afterGap ]))
        let disconnected = receipt 2L (Some first.EntryId) (ObservedDisconnect 2L)
        Assert.Equal(
            Ok(ProvisionalGap "app-server-continuity-disconnected"),
            recover (snapshot [ first; disconnected ])
        )
        let afterTerminal = receipt 4L (Some third.EntryId) (ObservedDisconnect 4L)
        Assert.Equal(
            Error "app-server-recovery-disconnect-after-terminal",
            recover (snapshot [ first; second; third; afterTerminal ])
        )

    [<Fact>]
    member _.``unavailable source or mismatched authenticated subscription refuses``() =
        let unavailable =
            CodexAppServerJournalRecovery.recover scope binding.TurnId binding.TransportIdentity
                authenticator (source (Error "store-unavailable"))
        Assert.Equal(Error "app-server-recovery-source-unavailable", unavailable)
        let wrongAuthenticator =
            { new ICodexAppServerSubscriptionAuthenticator with
                member _.ReadBoundSubscription() =
                    Ok { binding with Scope = { scope with ItemId = "other-item" } } }
        let result =
            CodexAppServerJournalRecovery.recover scope binding.TurnId binding.TransportIdentity
                wrongAuthenticator (source (Ok(snapshot [ first; second; third ])))
        Assert.Equal(Error "app-server-subscription-binding-mismatch", result)

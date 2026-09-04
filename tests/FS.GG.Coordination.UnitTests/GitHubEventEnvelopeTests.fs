module FS.GG.Coordination.GitHubEventEnvelopeTests

open Xunit
open FS.GG.Coordination.Qualification.Contracts
open FS.GG.Coordination.Qualification.Contracts.GitHubEventEnvelopeQualification

let private get = function Ok value -> value | Error findings -> failwithf "unexpected findings: %A" findings
let private source =
    { Kind="issues"; InstallationId="installation-42"; Repository="FS-GG/FS.GG.Coordination"
      SourceRevision="c1ae933ab8f4eb0b3f40119cebd985ed7f9e0f80" }
let private delivery position deliveryId eventId subject revision causation correlation receipt =
    { CursorPosition=position; DeliveryId=deliveryId; EventId=eventId; Subject=subject; SubjectRevision=string revision
      CausationId=causation; CorrelationId=correlation; ReceiptId=receipt; ReceiptDisposition="accepted" }
let private first = delivery 1L "delivery-1" "event-1" "issue-294" 1 "cause-root" "corr-294" "receipt-1"
let private second = delivery 2L "delivery-2" "event-2" "issue-294" 2 "event-1" "corr-294" "receipt-2"

[<Fact>]
let ``canonical envelope round trips and binds all fields`` () =
    let envelope = compile source [ first; second ] |> get
    let bytes = serialize envelope
    Assert.Equal(envelope, parse bytes |> get)
    Assert.Equal(envelope, verify envelope.Seal envelope |> get)
    Assert.True(envelope.Cursor = [ "1:delivery-1:event-1:receipt-1"; "2:delivery-2:event-2:receipt-2" ])
    Assert.Equal(64, envelope.Seal.Length)

[<Fact>]
let ``exact duplicates and reordered delivery converge without another effect`` () =
    let expected = compile source [ first; second ] |> get
    let duplicate = compile source [ first; second; first; second ] |> get
    let reordered = compile source [ second; first ] |> get
    Assert.Equal(expected, duplicate)
    Assert.Equal(expected, reordered)
    Assert.Equal(expected, replay expected source [ second; first; first ] |> get)

[<Fact>]
let ``conflicting delivery event and cursor identities fail closed`` () =
    let changed = { first with SubjectRevision="2" }
    match compile source [ first; changed ] with
    | Error findings -> Assert.Contains(GitHubEventEnvelopeFinding.DuplicateDeliveryConflict "delivery-1", findings); Assert.Contains(GitHubEventEnvelopeFinding.DuplicateEventConflict "event-1", findings); Assert.Contains(GitHubEventEnvelopeFinding.CursorPositionConflict 1L, findings)
    | Ok _ -> failwith "conflicting identity reuse survived"

[<Fact>]
let ``gaps stale revisions and causal receipt mismatches remain explicit`` () =
    let invalid = { second with CursorPosition=3L; SubjectRevision="0"; CausationId=second.EventId; CorrelationId=second.DeliveryId; ReceiptId=second.DeliveryId }
    match compile source [ first; invalid ] with
    | Error findings ->
        Assert.Contains(GitHubEventEnvelopeFinding.CursorGap(2L,3L), findings)
        Assert.Contains(GitHubEventEnvelopeFinding.StaleRevision "0", findings)
        Assert.Contains(GitHubEventEnvelopeFinding.CausationMismatch "event-2", findings)
        Assert.Contains(GitHubEventEnvelopeFinding.CorrelationMismatch "delivery-2", findings)
        Assert.Contains(GitHubEventEnvelopeFinding.ReceiptMismatch "delivery-2", findings)
    | Ok _ -> failwith "invalid cursor chain survived"

[<Fact>]
let ``unknown malformed source and cross source replay refuse`` () =
    Assert.True(Result.isError (compile { source with Kind="unknown"; InstallationId=" " } [ first ]))
    let envelope = compile source [ first ] |> get
    Assert.Equal(Error [ GitHubEventEnvelopeFinding.CrossSource "FS-GG/other" ], replay envelope { source with Repository="FS-GG/other" } [ first ])

[<Fact>]
let ``altered seal cursor and non canonical serialization refuse`` () =
    let envelope = compile source [ first; second ] |> get
    Assert.True(Result.isError (verify (String.replicate 64 "0") envelope))
    Assert.True(Result.isError (parse ((serialize envelope).Replace("\"cursor\":[", "\"cursor\":[\"bad\","))))
    Assert.True(Result.isError (parse (serialize envelope + " ")))

[<Fact>]
let ``generated and independent control inventories are exact`` () =
    let green: GitHubEventEnvelopeControlResult list =
        requiredControls |> List.map (fun control -> { Control=control; ControlPassed=true; BaselineGreen=true })
    Assert.Equal(Ok (), validateControls green green)
    Assert.True(Result.isError (validateControls green.Tail green))
    Assert.True(Result.isError (validateControls green ({ green.Head with ControlPassed=false }::green.Tail)))

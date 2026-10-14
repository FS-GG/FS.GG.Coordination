// Paired canonical producer vectors; these are inert codec/gate checks, not domain authentication.
// fsi --reference:<compiled production assembly> --exec paired.fsx <vectors.json> <sha256>
open System
open System.IO
open System.Text.Json
open System.Security.Cryptography
open System.Threading.Tasks
open FS.GG.Execution.Contracts
open FS.GG.Coordination.Orchestration.Execution

let require name condition = if not condition then failwith name else printfn "passed:%s" name
let accepted = function Ok value -> value | Error reason -> failwithf "%A" reason
if fsi.CommandLineArgs.Length <> 3 then failwith "paired-vectors-and-digest-required"
let path = fsi.CommandLineArgs[1]
if FileInfo(path).Length > 65536L then failwith "paired-vector-size-refused"
let content = File.ReadAllBytes path
if Convert.ToHexStringLower(SHA256.HashData content) <> fsi.CommandLineArgs[2] then failwith "paired-vector-identity-refused"
let document = JsonDocument.Parse(ReadOnlyMemory<byte> content)
let vectors = document.RootElement
let binding = vectors.GetProperty "binding"
let expected = JsonSerializer.SerializeToUtf8Bytes binding
let session = PhaseResultProtocol.Session(ReadOnlyMemory<byte> expected)
let unhex (element: JsonElement) = Convert.FromHexString(element.GetString())
require "paired-start-exact-canonical-equality" (session.StartFrame().ToArray() = unhex (vectors.GetProperty "start"))
let clock = binding.GetProperty "clock"
let number name = Int64.Parse(clock.GetProperty(name:string).GetString(),Globalization.CultureInfo.InvariantCulture)
let domain = {Id=Guid.Parse(clock.GetProperty("domain").GetString()); Frequency=number "frequency"}
let start = {Domain=domain;Ticks=number "start"}
let seconds (ticks:int64) = TimeSpan.FromTicks(int64 (bigint ticks * bigint TimeSpan.TicksPerSecond / bigint domain.Frequency))
let original = Budget.create start (seconds(number "total" - start.Ticks)) (seconds(number "total" - number "work")) |> accepted
let window = FiniteCustodyWindow.prepare start original |> accepted
let expectedAcks = vectors.GetProperty("acks").EnumerateArray() |> Seq.map unhex |> Seq.toArray
let mutable ackIndex = 0
for element in vectors.GetProperty("children").EnumerateArray() do
    let frame = unhex element
    let snapshots = session.Feed(ReadOnlyMemory<byte> frame) |> accepted
    let snapshot = List.exactlyOne snapshots
    // The paired .github suite owns authentication/domain semantics; this suite
    // verifies the real cross-language envelope bytes and retained-gate join.
    let needsAck = session.AcceptChild(snapshot,fun _ -> Ok ()) |> accepted
    if needsAck then
        let gate = session.BeginRetention(snapshot,fun s -> Task.FromResult(Ok {Binding=s.Binding;Ordinal=s.Ordinal;FrameDigest=s.Digest})) |> accepted
        gate.Completion.GetAwaiter().GetResult() |> accepted |> ignore
        let actual = session.TakeAck(start,window,gate) |> accepted
        require ("paired-ack-exact-" + snapshot.Kind) (actual.ToArray() = expectedAcks[ackIndex])
        session.ConfirmAckSent(start,window,snapshot) |> accepted |> ignore
        ackIndex <- ackIndex + 1
require "paired-six-complete-frames-three-acks" (session.Accepted.Length=6 && ackIndex=3 && session.FinishInput()=Ok ())
require "paired-original-task-accounting-settled" (session.LocalTasks=Settled && session.RetentionTasks.Length=3)
document.Dispose()

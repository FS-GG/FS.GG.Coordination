using System.Diagnostics;
using System.Text.Json;
using CliWrap;
using ProcessKit;

// Investigation source only: this is not the proposed shared production API.
if (args is ["--controls"]) { ObservationControls.Run(); return; }
if (args.Length != 4) throw new ArgumentException("backend case python fixture");
var backend = args[0];
var fixtureCase = args[1];
var python = args[2];
var fixture = args[3];
var clock = Stopwatch.StartNew();
var state = new Capture();
using var stdout = new Sink(state, true);
using var stderr = new Sink(state, false);
using var cancel = new CancellationTokenSource();
using var cancellationCause = cancel.Token.Register(() => state.RecordCause(
    fixtureCase == "ordinary-cancel" ? "cancel-requested" : "deadline"));
cancel.CancelAfter(fixtureCase == "ordinary-cancel" ? 100 : 500);
Task? libraryTask = null;
string libraryResult = "not-applicable";
string launch = "pending", exit = "unknown", output = "unknown", cleanup = "unknown";
string? error = null;
int? exitCode = null;
try
{
    if (backend == "baseline")
    {
        using var process = new Process();
        process.StartInfo = new ProcessStartInfo(python)
        { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
        process.StartInfo.ArgumentList.Add(fixture);
        process.StartInfo.ArgumentList.Add(fixtureCase);
        // All three adapters deliberately inherit the evaluator environment; no secrets are logged.
        process.Start();
        launch = "started";
        var reads = Task.WhenAll(Pump(process.StandardOutput.BaseStream, stdout, state, true, CancellationToken.None),
                                 Pump(process.StandardError.BaseStream, stderr, state, false, CancellationToken.None));
        var exited = process.WaitForExitAsync();
        try
        {
            // Waiting for both is deliberate: leader exit and EOF are separate observations.
            await Task.WhenAll(exited, reads).WaitAsync(cancel.Token);
            output = "complete";
        }
        catch (Exception e)
        {
            state.Cause ??= fixtureCase == "ordinary-cancel" ? "cancel-requested" :
                cancel.IsCancellationRequested ? "deadline" : "sink-failure";
            error = e.GetType().Name;
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            try { await Task.WhenAll(exited, reads).WaitAsync(Remaining(clock)); output = "complete"; }
            catch { output = reads.IsCompletedSuccessfully ? "complete" : "incomplete"; }
        }
        if (process.HasExited) { exit = "observed"; exitCode = process.ExitCode; }
        cleanup = exited.IsCompletedSuccessfully && reads.IsCompleted ? "direct-child-and-read-tasks-terminal" : "incomplete";
    }
    else if (backend == "cliwrap")
    {
        var running = Cli.Wrap(python).WithArguments(new[] { fixture, fixtureCase })
            .WithValidation(CommandResultValidation.None)
            .WithStandardOutputPipe(PipeTarget.Create((stream, token) => Pump(stream, stdout, state, true, token)))
            .WithStandardErrorPipe(PipeTarget.Create((stream, token) => Pump(stream, stderr, state, false, token))).ExecuteAsync(cancel.Token);
        launch = "submitted";
        libraryTask = running.Task;
        var result = await running.Task.WaitAsync(Remaining(clock));
        libraryResult = "success";
        exitCode = result.ExitCode;
        exit = "observed";
        output = "complete";
        cleanup = "library-task-terminal;descendants-unverified";
    }
    else if (backend == "processkit")
    {
        // Per-stream internal caps are additional guards, not a claim of a combined native cap.
        var policy = ProcessKit.OutputBufferPolicy.Default.WithMaxBytes(Capture.Cap)
            .WithOverflow(ProcessKit.OverflowMode.Error);
        var command = new ProcessKit.Command(python).Args(new[] { fixture, fixtureCase })
            .OutputBuffer(policy).StdoutTee(stdout).StderrTee(stderr).CancelOn(cancel.Token);
        launch = "submitted";
        var running = command.OutputBytesAsync();
        libraryTask = running;
        var result = await running.WaitAsync(Remaining(clock));
        libraryResult = result.IsOk ? "success" : "error";
        if (result.IsOk)
        {
            exitCode = result.ResultValue.Code?.Value;
            exit = "observed";
            output = result.ResultValue.Truncated ? "incomplete" : "complete";
            cleanup = "library-task-terminal;mechanism-and-descendants-unverified";
        }
        else
        {
            error = result.ErrorValue.GetType().Name;
            exitCode = result.ErrorValue.Code?.Value;
            if (exitCode is not null) exit = "observed";
            output = "incomplete-or-unreported";
            cleanup = "library-error;cleanup-unreported";
        }
    }
    else throw new ArgumentException("unknown backend");
}
catch (Exception e)
{
    error = e.GetType().Name;
    libraryResult = "exception";
    if (launch == "pending") launch = "failed";
    if (e is TimeoutException) cleanup = "evaluator-deadline;library-task-unresolved";
}
state.Cause ??= cancel.IsCancellationRequested
    ? (fixtureCase == "ordinary-cancel" ? "cancel-requested" : "deadline") : error is null ? "none" : "library-error";
// Callback completion and task settlement are observations independent of process/descendant cleanup.
if (backend == "cliwrap") output = state.StdoutRead == "eof" && state.StderrRead == "eof" ? "complete" : "incomplete";
Console.WriteLine(JsonSerializer.Serialize(new
{
    schema = "fsgg.process-evaluation.observation/1", backend, fixtureCase,
    launch, exit, exitCode, output, cause = state.Cause, cleanup, error,
    elapsedMs = clock.ElapsedMilliseconds, chargedBytes = state.Charged,
    stdoutRead = state.StdoutRead, stderrRead = state.StderrRead,
    libraryTask = ObservationControls.TaskState(libraryTask), libraryResult,
    eofScope = backend == "processkit" ? "not-exposed-by-tee;capture-completeness-separate" : "caller-owned-byte-reader",
    retainedBytes = state.Out.Length + state.Err.Length,
    stdoutPrefix = Convert.ToBase64String(state.Out.ToArray()),
    stderrPrefix = Convert.ToBase64String(state.Err.ToArray()),
    acceptance = "not-established"
}));

static TimeSpan Remaining(Stopwatch clock) => TimeSpan.FromMilliseconds(Math.Max(1, 1000 - clock.ElapsedMilliseconds));
static async Task Pump(Stream source, Stream sink, Capture state, bool stdout, CancellationToken token)
{
    state.SetRead(stdout, "reading");
    try
    {
        var buffer = new byte[1024];
        int count;
        while ((count = await source.ReadAsync(buffer, token)) != 0)
            await sink.WriteAsync(buffer.AsMemory(0, count), token);
        state.SetRead(stdout, "eof");
    }
    catch (OperationCanceledException) { state.SetRead(stdout, "cancelled-without-eof"); throw; }
    catch { state.SetRead(stdout, "failed-without-eof"); throw; }
}
sealed class Capture
{
    public const int Cap = 4096;
    public long Charged;
    public string? Cause;
    public string StdoutRead = "not-exposed", StderrRead = "not-exposed";
    public void RecordCause(string cause) { lock (this) Cause ??= cause; }
    public void SetRead(bool stdout, string observation)
    { lock (this) { if (stdout) StdoutRead = observation; else StderrRead = observation; } }
    public readonly MemoryStream Out = new(), Err = new();
    public void Write(bool stdout, ReadOnlySpan<byte> bytes)
    {
        lock (this)
        {
            Charged += bytes.Length; // Charge the whole incoming chunk before retaining any part.
            int remaining = (int)Math.Max(0, Cap - Out.Length - Err.Length);
            (stdout ? Out : Err).Write(bytes[..Math.Min(remaining, bytes.Length)]);
            if (Charged > Cap) { Cause ??= "output-overflow"; throw new IOException("combined output cap"); }
        }
    }
}
sealed class Sink(Capture state, bool stdout) : Stream
{
    public override bool CanRead => false;
    public override bool CanSeek => false;
    public override bool CanWrite => true;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
    public override void Flush() { }
    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long length) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => state.Write(stdout, buffer.AsSpan(offset, count));
    public override void Write(ReadOnlySpan<byte> bytes) => state.Write(stdout, bytes);
    public override ValueTask WriteAsync(ReadOnlyMemory<byte> bytes, CancellationToken cancellationToken = default)
    { cancellationToken.ThrowIfCancellationRequested(); state.Write(stdout, bytes.Span); return ValueTask.CompletedTask; }
}

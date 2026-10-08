using System.Text.Json;

// Evaluator-only interpretations; no consumer source/API change or acceptance claim.
static class ObservationControls
{
    public static string ExitState(int? code) => code is null ? "unknown" : "observed";

    public static string TaskState(Task? task) => task is null ? "not-applicable" :
        !task.IsCompleted ? "unresolved" : task.IsCanceled ? "cancelled" :
        task.IsFaulted ? "faulted" : "completed";

    public static string SddReason(bool launchFailed, bool? leaderExitedBeforeFailure,
                                   string cause, string output) =>
        launchFailed ? "launch-failure" :
        leaderExitedBeforeFailure == true && output != "complete" ? "pipes-held-after-exit" :
        leaderExitedBeforeFailure == false && cause == "deadline" ? "child-outlived-bound" :
        "insufficient-observation";

    public static string QuintExecutionCause(string cause) => cause switch
    {
        "output-overflow" => "OutputLimit",
        "cancel-requested" => "Cancelled",
        "deadline" => "TimedOut",
        _ => "domain-policy-required"
    };

    private static bool IsExplicitSinkFailure(Capture capture, bool rejected) =>
        rejected && capture.Cause == "sink-failure" && capture.Charged == 256 &&
        capture.Out.Length + capture.Err.Length == 0;

    public static void Run()
    {
        int count = 0;
        void Check(bool condition, string name)
        { if (!condition) throw new InvalidOperationException(name); count++; }
        var capture = new Capture();
        capture.Write(true, new byte[3000]);
        try { capture.Write(false, new byte[2000]); }
        catch (IOException) { }
        Check(capture.Charged == 5000 && capture.Out.Length + capture.Err.Length == 4096,
              "combined bytes charged before retention across streams");
        Check(capture.Cause == "output-overflow", "overflow retains distinct cause");
        var cancelled = new Capture();
        cancelled.RecordCause("cancel-requested");
        try { cancelled.Write(true, new byte[5000]); } catch (IOException) { }
        Check(cancelled.Cause == "cancel-requested", "later overflow cannot replace first cause");
        Check(SddReason(false, true, "deadline", "incomplete") == "pipes-held-after-exit",
              "SDD held-pipe meaning requires prior exit evidence");
        Check(SddReason(false, false, "deadline", "incomplete") == "child-outlived-bound",
              "SDD child timeout differs from held pipe");
        Check(SddReason(false, null, "deadline", "incomplete") == "insufficient-observation",
              "unknown exit cannot fabricate either SDD reason");
        Check(SddReason(true, null, "none", "unknown") == "launch-failure", "launch distinct");
        foreach (var pair in new[] { ("output-overflow", "OutputLimit"),
                                      ("cancel-requested", "Cancelled"), ("deadline", "TimedOut") })
            Check(QuintExecutionCause(pair.Item1) == pair.Item2, "Quint execution cause preserved");
        Check(QuintExecutionCause("none") == "domain-policy-required",
              "mechanical completion cannot infer Quint identity/version/domain success");
        Check(TaskState(Task.FromException(new IOException())) == "faulted",
              "faulted task is terminal but not successful cleanup");
        Check(TaskState(new TaskCompletionSource().Task) == "unresolved", "pending remains unresolved");
        var sinkFailure = new Capture();
        using var faultingSink = new Sink(sinkFailure, true, failOnWrite: true);
        bool rejected = false;
        try { faultingSink.Write(new byte[256]); } catch (IOException) { rejected = true; }
        Check(IsExplicitSinkFailure(sinkFailure, rejected), "finite non-overflow sink failure charged without retention");
        var cancelledSink = new Capture();
        cancelledSink.RecordCause("cancel-requested");
        using var lateFault = new Sink(cancelledSink, true, failOnWrite: true);
        try { lateFault.Write(new byte[256]); } catch (IOException) { }
        Check(cancelledSink.Cause == "cancel-requested", "later sink failure preserves cancellation first cause");
        Check(ExitState(null) == "unknown", "absent public exit code cannot fabricate observed exit");
        Check(ExitState(0) == "observed", "zero public exit code is positive exit evidence");
        Check(cancelledSink.Charged == 256 && cancelledSink.Out.Length == 0,
              "sink failure after cancellation charges without retention while preserving first cause");
        using var unfiredTimer = new CancellationTokenSource();
        var timedWait = new Capture();
        timedWait.RecordWaitFailure(new TimeoutException());
        timedWait.RecordCause(unfiredTimer.IsCancellationRequested ? "deadline" : "library-error");
        Check(!unfiredTimer.IsCancellationRequested && timedWait.Cause == "deadline",
              "negative control: observed wait timeout survives an uncancelled timer token");
        // A late public success supplies exit evidence; final success cannot replace the failed wait.
        var lateExit = ExitState(0);
        timedWait.RecordCause("none");
        Check(lateExit == "observed" && timedWait.Cause == "deadline",
              "late successful public exit cannot erase original timeout failure");
        // Guard-removal negative control: a successful ordinary sink cannot satisfy the fault case.
        var bypass = new Capture();
        using var ordinarySink = new Sink(bypass, true);
        bool bypassRejected = false;
        try { ordinarySink.Write(new byte[256]); } catch (IOException) { bypassRejected = true; }
        Check(!IsExplicitSinkFailure(bypass, bypassRejected), "negative control detects disabled fault injection");
        Console.WriteLine(JsonSerializer.Serialize(new { controls = count,
            scope = "pure evaluator/consumer-meaning controls; native and consumer acceptance not-established" }));
    }
}

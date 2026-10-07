using System.Text.Json;

// Evaluator-only interpretations; no consumer source/API change or acceptance claim.
static class ObservationControls
{
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
        Console.WriteLine(JsonSerializer.Serialize(new { controls = count,
            scope = "pure evaluator/consumer-meaning controls; native and consumer acceptance not-established" }));
    }
}

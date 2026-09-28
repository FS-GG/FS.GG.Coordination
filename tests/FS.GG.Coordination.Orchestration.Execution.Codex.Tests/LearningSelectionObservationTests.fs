namespace FS.GG.Coordination.Orchestration.Execution.Codex.Tests

open System
open System.IO
open FS.GG.Coordination.Orchestration.Execution
open FS.GG.Coordination.Orchestration.Execution.Codex
open Xunit

type LearningSelectionObservationTests() =
    let requested: RequestedSelection =
        {
            Model = Some "gpt-5.6-sol"
            Effort = Some "medium"
        }

    let resolved: ResolvedSelection =
        {
            Model = Some "gpt-5.6-sol"
            Effort = Some "medium"
        }

    let project fixture =
        let raw =
            File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "learning-selection", fixture))

        match CodexTurnProjection.project None 1L raw with
        | Some(Ok turn) -> CodexTurnProjection.learningSelection requested resolved turn
        | value -> failwithf "%A" value

    [<Fact>]
    member _.``native selection matches without conflating local resolution``() =
        let observation = project "matched.json"
        Assert.Equal(LearningSelectionDisposition.Matched, observation.Disposition)
        Assert.Equal(Some "gpt-5.6-sol", observation.Native.Model)
        Assert.Equal(resolved, observation.Resolved)

    [<Fact>]
    member _.``native mismatch remains a deviation under the requested arm``() =
        let observation = project "mismatch.json"

        Assert.Equal(
            LearningSelectionDisposition.Deviation [ "native-model-mismatch"; "native-effort-mismatch" ],
            observation.Disposition
        )

        Assert.Equal(requested, observation.Requested)
        Assert.Equal(resolved, observation.Resolved)
        Assert.Equal(Some "gpt-6-sol", observation.Native.Model)

    [<Fact>]
    member _.``missing native identity remains unknown``() =
        let observation = project "missing.json"

        Assert.Equal(
            LearningSelectionDisposition.ObservationUnknown [ "native-model-unobserved"; "native-effort-unobserved" ],
            observation.Disposition
        )

        Assert.Equal(None, observation.Native.Model)
        Assert.Equal(None, observation.Native.Effort)

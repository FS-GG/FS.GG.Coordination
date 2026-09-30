namespace FS.GG.Coordination.Cli

open System
open System.IO
open System.Text.Json
open System.Threading
open System.Threading.Tasks
open FS.GG.Coordination.Orchestration.Execution

type PortableWorkspaceRuntimeSelection =
    {
        OperationId: string
        OperationIdentity: string
        ComponentId: string option
        EntryPoint: string
    }

type PortableWorkspaceRuntimeEnrollment =
    {
        EnrollmentId: string
        Profile: PortableWorkspaceProfile
        Authority: PortableWorkspaceAuthority
        Policy: PortableExecutorPolicy
        Selections: PortableWorkspaceRuntimeSelection list
    }

type IPortableWorkspaceRuntimeEnrollmentSource =
    abstract Resolve: enrollmentId: string -> Result<PortableWorkspaceRuntimeEnrollment, string>

type PortableWorkspaceRuntimeCommandDependencies =
    {
        Enrollments: IPortableWorkspaceRuntimeEnrollmentSource
        CreateRunner: PortableRuntimePolicy -> IPortableProcessRunner
        Clock: unit -> DateTimeOffset
    }

type PortableWorkspaceRuntimeCommandResponse =
    {
        ExitCode: int
        StandardOutput: string
        StandardError: string
    }

[<RequireQualifiedAccess>]
module PortableWorkspaceRuntimeCommand =
    let private response exitCode standardOutput standardError =
        {
            ExitCode = exitCode
            StandardOutput = standardOutput
            StandardError = standardError
        }

    let private usage () =
        response
            2
            ""
            "portable-workspace execute --enrollment ID --operation-id ID --profile FILE --command FILE\nportable-workspace recover --enrollment ID --operation-id ID --profile FILE --command FILE"

    let private options (arguments: string list) =
        let rec loop (values: Map<string, string>) (remaining: string list) =
            match remaining with
            | [] -> Ok values
            | key :: value :: tail when
                key.StartsWith("--", StringComparison.Ordinal)
                && not (Map.containsKey key values)
                ->
                loop (Map.add key value values) tail
            | token :: _ -> Error $"unknown or repeated argument: %s{token}"

        loop Map.empty arguments

    let private exactSelection
        (enrollment: PortableWorkspaceRuntimeEnrollment)
        operationId
        (profile: PortableWorkspaceProfile)
        (command: PortableWorkspaceCommand)
        =
        if profile <> enrollment.Profile then
            Error "portable-runtime-profile-not-enrolled"
        elif
            enrollment.Policy.WorkspaceScope <> enrollment.Profile.WorkspaceScope
            || enrollment.Policy.SourceRevision <> enrollment.Profile.SourceRevision
            || enrollment.Policy.QualifiedImage <> enrollment.Profile.QualifiedImage
            || enrollment.Authority.WorkspaceScope <> enrollment.Profile.WorkspaceScope
        then
            Error "portable-runtime-enrollment-invalid"
        else
            match enrollment.Selections |> List.filter (fun selection -> selection.OperationId = operationId) with
            | [] -> Error "portable-runtime-operation-not-enrolled"
            | [ selection ] when
                    command.Operation <> selection.OperationIdentity
                    || command.ComponentId <> selection.ComponentId
                ->
                Error "portable-runtime-operation-binding-refused"
            | [ selection ] ->
                match
                    enrollment.Policy.Operations
                    |> List.filter (fun operation ->
                        operation.OperationIdentity = selection.OperationIdentity
                        && operation.ComponentId = selection.ComponentId
                        && operation.EntryPoint = selection.EntryPoint)
                with
                | [ _ ] -> Ok()
                | _ -> Error "portable-runtime-enrollment-invalid"
            | _ -> Error "portable-runtime-enrollment-invalid"

    let private output disposition (receipt: PortableExecutionReceipt) =
        let resultElement =
            use document =
                JsonDocument.Parse(PortableWorkspaceContract.resultBytes receipt.Result |> Result.defaultWith failwith)

            document.RootElement.Clone()

        JsonSerializer.Serialize
            {|
                schema = "fsgg.portable-workspace-runtime-result/1"
                outcome = disposition
                commandId = receipt.Result.CommandId.ToString("D").ToLowerInvariant()
                operation = receipt.OperationIdentity
                entryPoint = receipt.EntryPoint
                workspaceScope = receipt.WorkspaceScope
                sourceRevision = receipt.SourceRevision
                qualifiedImage = receipt.QualifiedImage
                outputSha256 = receipt.OutputSha256
                outputBytes = receipt.OutputBytes
                executionStarted = receipt.ExecutionStarted
                cleanupCompleted = receipt.CleanupCompleted
                cancellationRequested = receipt.CancellationRequested
                terminationObserved = receipt.TerminationObserved
                result = resultElement
            |}

    let private completedResponse disposition receipt =
        if not receipt.CleanupCompleted then
            response
                5
                (output "cleanup-incomplete" receipt)
                "portable-runtime-cleanup-incomplete"
        else
            let exitCode =
                match receipt.Result.ExitCode, receipt.Result.Error with
                | EvidenceKnown 0, None -> 0
                | _ -> 5

            response exitCode (output disposition receipt) ""

    let private executeResolved
        (dependencies: PortableWorkspaceRuntimeCommandDependencies)
        recover
        (enrollment: PortableWorkspaceRuntimeEnrollment)
        (profile: PortableWorkspaceProfile)
        (command: PortableWorkspaceCommand)
        cancellationToken
        =
        task {
            let runner = dependencies.CreateRunner enrollment.Policy.Runtime
            let executor = PortableWorkspaceExecutor.Executor(enrollment.Policy, runner, dependencies.Clock)

            let! outcome =
                if recover then
                    executor.RecoverAsync(profile, command, cancellationToken)
                else
                    executor.ExecuteAsync(enrollment.Authority, profile, command, cancellationToken)

            return
                match outcome with
                | Completed receipt -> completedResponse "completed" receipt
                | Duplicate receipt -> completedResponse "duplicate" receipt
                | PendingDuplicate commandId ->
                    let pendingId = commandId.ToString("D").ToLowerInvariant()

                    response
                        4
                        ""
                        $"portable-runtime-pending:%s{pendingId}"
                | Refused reason -> response 3 "" reason
        }

    let executeAsync
        (dependencies: PortableWorkspaceRuntimeCommandDependencies)
        (arguments: string array)
        (cancellationToken: CancellationToken)
        : Task<PortableWorkspaceRuntimeCommandResponse> =
        task {
            match arguments |> Array.toList with
            | ("execute" | "recover" as verb) :: rest ->
                match options rest with
                | Error reason -> return response 2 "" reason
                | Ok values ->
                    let required = [ "--enrollment"; "--operation-id"; "--profile"; "--command" ]

                    if
                        values.Count <> required.Length
                        || required |> List.exists (fun key -> not (Map.containsKey key values))
                    then
                        return usage ()
                    elif not (File.Exists values["--profile"]) || not (File.Exists values["--command"]) then
                        return response 2 "" "portable runtime profile or command does not exist"
                    else
                        match dependencies.Enrollments.Resolve values["--enrollment"] with
                        | Error reason -> return response 3 "" reason
                        | Ok enrollment when enrollment.EnrollmentId <> values["--enrollment"] ->
                            return response 3 "" "portable-runtime-enrollment-invalid"
                        | Ok enrollment ->
                            match
                                PortableWorkspaceContract.parseProfile (File.ReadAllBytes values["--profile"]),
                                PortableWorkspaceContract.parseCommand (File.ReadAllBytes values["--command"])
                            with
                            | Ok profile, Ok command ->
                                match exactSelection enrollment values["--operation-id"] profile command with
                                | Error reason -> return response 3 "" reason
                                | Ok() ->
                                    return!
                                        executeResolved
                                            dependencies
                                            (verb = "recover")
                                            enrollment
                                            profile
                                            command
                                            cancellationToken
                            | Error reason, _
                            | _, Error reason -> return response 3 "" reason
            | _ -> return usage ()
        }

    let runWithCancellation dependencies arguments cancellationToken =
        let result = executeAsync dependencies arguments cancellationToken |> _.GetAwaiter().GetResult()

        if not (String.IsNullOrEmpty result.StandardOutput) then
            Console.Out.WriteLine result.StandardOutput

        if not (String.IsNullOrEmpty result.StandardError) then
            Console.Error.WriteLine result.StandardError

        result.ExitCode

    let run dependencies arguments =
        runWithCancellation dependencies arguments CancellationToken.None

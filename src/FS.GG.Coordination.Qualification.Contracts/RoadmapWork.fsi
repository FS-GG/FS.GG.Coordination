namespace FS.GG.Coordination.Qualification.Contracts

open System

type RoadmapWorkFinding =
    { Code: string
      Path: string
      Message: string }

type RoadmapGateContract =
    { Id: string
      QGate: string
      CommandSha256: string }

type RoadmapGateCommand =
    { Id: string
      QGate: string
      Executable: string
      Arguments: string list
      CommandSha256: string }

type RoadmapUnit =
    { Id: string
      Title: string
      Owner: string
      Prerequisites: string list
      PermissionCeiling: string list
      ExitGate: string
      QGates: string list
      GateCommands: string list
      GateContracts: RoadmapGateContract list
      ContractSha256: string }

type RoadmapInspection =
    { RoadmapRevision: string
      RoadmapSha256: string
      Unit: RoadmapUnit }

type PrerequisiteStatus =
    { UnitId: string
      Ready: bool
      AcceptedReceiptDigests: string list }

type RoadmapArtifactInput =
    { Name: string
      Path: string
      Bytes: ReadOnlyMemory<byte> }

type RoadmapCandidate = { Commit: string; Tree: string }

[<RequireQualifiedAccess>]
module RoadmapWork =
    val inspect:
        index: ReadOnlyMemory<byte> ->
        roadmap: ReadOnlyMemory<byte> ->
        unitId: string ->
            Result<RoadmapInspection, RoadmapWorkFinding list>

    val checkPrerequisites:
        indexBytes: ReadOnlyMemory<byte> ->
        roadmapBytes: ReadOnlyMemory<byte> ->
        receiptDocuments: ReadOnlyMemory<byte> list ->
        unitId: string ->
            Result<PrerequisiteStatus, RoadmapWorkFinding list>

    val createManifest:
        indexBytes: ReadOnlyMemory<byte> ->
        roadmapBytes: ReadOnlyMemory<byte> ->
        receiptDocuments: ReadOnlyMemory<byte> list ->
        unitId: string ->
        candidate: RoadmapCandidate ->
        createdAt: string ->
        artifacts: RoadmapArtifactInput list ->
            Result<byte array, RoadmapWorkFinding list>

    val validateManifest:
        indexBytes: ReadOnlyMemory<byte> ->
        roadmapBytes: ReadOnlyMemory<byte> ->
        receiptDocuments: ReadOnlyMemory<byte> list ->
        unitId: string ->
        candidate: RoadmapCandidate ->
        manifest: ReadOnlyMemory<byte> ->
            Result<RoadmapGateContract list, RoadmapWorkFinding list>

    val commandContracts:
        index: ReadOnlyMemory<byte> ->
        roadmap: ReadOnlyMemory<byte> ->
        unitId: string ->
            Result<RoadmapGateContract list, RoadmapWorkFinding list>

    val validateGateCatalog:
        expected: RoadmapGateContract list ->
        catalog: ReadOnlyMemory<byte> ->
            Result<RoadmapGateCommand list, RoadmapWorkFinding list>

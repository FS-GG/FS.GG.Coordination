namespace FS.GG.Coordination.Qualification.Contracts

open System

type HarnessMutationProofContext =
    { CandidateCommit: string
      CandidateTreeSha256: string
      UnitContractSha256: string
      ValidatorSha256: string }

type HarnessMutationProofFinding =
    { Code: string; Path: string; Message: string }

[<RequireQualifiedAccess>]
module HarnessMutationProof =
    [<Literal>]
    val Schema: string = "fsgg.coordination.harness-mutation-proof/1"
    val GateClasses: string list
    val MutationKinds: string list
    val generate: context: HarnessMutationProofContext -> inventory: ReadOnlyMemory<byte> -> baseline: ReadOnlyMemory<byte> -> Result<byte array, HarnessMutationProofFinding list>
    val validate: context: HarnessMutationProofContext -> inventory: ReadOnlyMemory<byte> -> baseline: ReadOnlyMemory<byte> -> proof: ReadOnlyMemory<byte> -> Result<byte array, HarnessMutationProofFinding list>

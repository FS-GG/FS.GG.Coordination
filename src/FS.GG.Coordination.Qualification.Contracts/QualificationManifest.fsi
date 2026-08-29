namespace FS.GG.Coordination.Qualification.Contracts

open System

type QualificationManifestCandidate =
    { CommitSha: string
      TreeSha256: string
      ContractSha256: string
      Producer: string }

type QualificationManifestContent =
    { Id: string
      Sha256: string
      Bytes: int64
      MediaType: string
      Producer: string
      ObservedAt: DateTimeOffset }

type QualificationManifestEnvironment =
    { Os: string
      Architecture: string
      Runtime: string
      Locale: string
      Timezone: string
      NetworkMode: string
      Producer: string
      ObservedAt: DateTimeOffset }

type QualificationManifestResult =
    { Id: string
      QGate: string
      Sha256: string
      Producer: string
      CompletedAt: DateTimeOffset }

type QualificationManifestReview =
    { Id: string
      Role: string
      Sha256: string
      Principal: string
      CompletedAt: DateTimeOffset }

type QualificationManifestExpectedInventory =
    { Sources: string list
      Model: string list
      Compiler: string list
      Dependencies: string list
      GeneratedCases: string list
      IndependentCases: string list
      ExternalFixtures: string list
      Packages: string list
      Results: string list
      Reviewers: string list }

type QualificationManifestInput =
    { Candidate: QualificationManifestCandidate
      Expected: QualificationManifestExpectedInventory
      CreatedAt: DateTimeOffset
      Sources: QualificationManifestContent list
      Model: QualificationManifestContent list
      Compiler: QualificationManifestContent list
      Dependencies: QualificationManifestContent list
      GeneratedCases: QualificationManifestContent list
      IndependentCases: QualificationManifestContent list
      ExternalFixtures: QualificationManifestContent list
      Packages: QualificationManifestContent list
      Environment: QualificationManifestEnvironment
      Results: QualificationManifestResult list
      Reviewers: QualificationManifestReview list }

type QualificationManifestFinding =
    { Code: string
      Path: string
      Expected: string
      Actual: string }

[<RequireQualifiedAccess>]
module QualificationManifest =
    [<Literal>]
    val Schema: string = "fsgg.coordination.qualification-manifest/1"
    val generate:
        input: QualificationManifestInput -> Result<byte array, QualificationManifestFinding list>
    val generateInventory:
        expected: QualificationManifestExpectedInventory -> Result<byte array, QualificationManifestFinding list>
    val validate:
        inventory: ReadOnlyMemory<byte> -> manifest: ReadOnlyMemory<byte> -> Result<byte array, QualificationManifestFinding list>

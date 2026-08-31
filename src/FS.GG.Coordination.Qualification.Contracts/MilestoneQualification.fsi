module FS.GG.Coordination.Qualification.Contracts.MilestoneQualification

type Mode = Scoped | Comprehensive

type AcceptanceBinding =
    { ReceiptPath: string
      ReceiptDigest: string }

type ChildBinding =
    { Id: string
      ContractSha256: string
      Acceptance: AcceptanceBinding option }

type State =
    { PolicyVersion: string
      Parent: string
      Mode: Mode
      BoundaryKind: string option
      ExpectedChildren: string list
      Children: ChildBinding list }

type Validation =
    { State: State
      AcceptedPrefixLength: int
      ContractDrift: string list
      SubjectSha256: string }

val parse: bytes: byte array -> Result<State, string>
val validate: state: State -> receiptBytes: Map<string, byte array> -> Result<Validation, string>
val closureSubject: state: State -> exactHead: string -> treeSha256: string -> string

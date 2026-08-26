namespace FS.GG.Coordination.Qualification.Contracts

[<RequireQualifiedAccess>]
type QualificationResult =
    | Passed
    | Failed of rule: string

type QualificationReceipt =
    { Rule: string
      Result: QualificationResult }

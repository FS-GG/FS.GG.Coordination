namespace FS.GG.Coordination.Qualification.Contracts

open System

[<RequireQualifiedAccess>]
type QualificationResult =
    | Passed
    | Failed of rule: string

type QualificationReceipt =
    { Rule: string
      Result: QualificationResult }

type PublishedQuintKernelIdentity =
    { PackageId: string
      PackageVersion: string
      ManifestPath: string
      ManifestSha256: string
      Schema: string
      Profile: string
      ProducerMerge: string
      ConsumerMerge: string
      QuintVersion: string
      QuintBinarySha256: string
      GuidanceSource: string
      GuidanceTreeSha256: string }

type PublishedQuintKernelFinding =
    { Code: string
      Path: string
      Expected: string
      Actual: string }

[<RequireQualifiedAccess>]
module PublishedQuintKernel =
    val expected: PublishedQuintKernelIdentity
    val referencedAssemblyName: string
    val validateManifest:
        manifest: ReadOnlyMemory<byte> -> Result<PublishedQuintKernelIdentity, PublishedQuintKernelFinding list>

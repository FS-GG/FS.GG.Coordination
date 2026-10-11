namespace FS.GG.Execution.Contracts
[<RequireQualifiedAccess>]
module BoundedOutput =
    val create: CapturePolicy -> Result<IBoundedByteSink,ValidationError list>

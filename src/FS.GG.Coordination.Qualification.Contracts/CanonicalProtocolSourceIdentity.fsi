module FS.GG.Coordination.Qualification.Contracts.CanonicalProtocolSourceIdentity

[<Literal>]
val CurrentSha256: string = "edebdec2a4f80ea8ab94dc18cadb02894efe4b3e989d7e3f5732ed7d5c8ba0f1"

val isCurrent: root: string -> bool
val requireCurrent: root: string -> unit

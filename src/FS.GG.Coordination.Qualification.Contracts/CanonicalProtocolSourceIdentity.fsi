module FS.GG.Coordination.Qualification.Contracts.CanonicalProtocolSourceIdentity

[<Literal>]
val CurrentSha256: string = "fd8ee59710f4855883061dc8d970eb7d7a80c910af79a9bed69f8c5217fbda4c"

val isCurrent: root: string -> bool
val requireCurrent: root: string -> unit

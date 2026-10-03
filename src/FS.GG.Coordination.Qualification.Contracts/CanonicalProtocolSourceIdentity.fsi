module FS.GG.Coordination.Qualification.Contracts.CanonicalProtocolSourceIdentity

[<Literal>]
val CurrentSha256: string = "ab114cbfd7738dd1568ce2da3250b7b141b7d5759169bd9d9fb23d3165bdd354"

val isCurrent: root: string -> bool
val requireCurrent: root: string -> unit

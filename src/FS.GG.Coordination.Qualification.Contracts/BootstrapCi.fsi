/// Bootstrap qualification decisions shared by the production FSI adapter and tests.
module FS.GG.Coordination.Qualification.Contracts.BootstrapCi

/// Evaluate a bootstrap qualification command and return exit code, stdout, and stderr projections.
val execute: arguments: string list -> int * string * string

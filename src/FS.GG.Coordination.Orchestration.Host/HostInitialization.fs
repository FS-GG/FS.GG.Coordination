namespace FS.GG.Coordination.Orchestration.Host

open System.Threading
open Npgsql
open FS.GG.Coordination.Orchestration.PostgreSql

[<RequireQualifiedAccess>]
module HostInitialization =
    /// The only schema-writing host path. Ordinary serve startup never calls this function.
    let initialize (connectionString: string) (cancellationToken: CancellationToken) = task {
        use source = NpgsqlDataSource.Create connectionString
        let! backupIdentity = PostgreSqlSchema.migrate source cancellationToken
        do! PostgreSqlPilotSchema.migrate source cancellationToken
        do! PostgreSqlExecutionSchema.migrate source cancellationToken
        return backupIdentity }

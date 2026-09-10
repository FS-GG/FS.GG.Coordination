namespace FS.GG.Coordination.Orchestration.PostgreSql

open System.Threading
open System.Threading.Tasks
open Npgsql
open FS.GG.Coordination.Orchestration.Observer

[<RequireQualifiedAccess>]
module PostgreSqlObserverSchema =
    val migrate: NpgsqlDataSource -> CancellationToken -> Task<unit>

type PostgreSqlObserverStore =
    new: StoreOptions -> PostgreSqlObserverStore
    interface IObserverJournalStore

namespace FS.GG.Coordination.Orchestration.PostgreSql

open System
open System.Threading
open System.Threading.Tasks
open Akka.Configuration
open Npgsql
open FS.GG.Coordination.Core.Orchestration
open FS.GG.Coordination.Core.OrchestrationPersistence

type StoreOptions =
    { DataSource: NpgsqlDataSource
      StoreId: string
      BackupIdentity: string
      MinimumGenerationFence: int64
      RuntimeSchemaVersion: int
      SupportedEventSchemaVersions: Set<int>
      SupportedSerializerVersions: Set<string>
      MaximumCandidateBytes: int64 }

[<RequireQualifiedAccess>]
module EventEnvelope =
    val legacySerializerVersion: string
    val serializerVersion: string
    val encode: Event -> byte array
    val tryDecode: byte array -> Result<Event,string>

[<RequireQualifiedAccess>]
module PostgreSqlSchema =
    val migrate: NpgsqlDataSource -> CancellationToken -> Task<string>

type PostgreSqlStore =
    new: StoreOptions -> PostgreSqlStore
    interface IJournalStore
    interface ICandidateStore
    interface IBackupReconciler

type AkkaStoredEvent = { Envelope: byte array }

[<RequireQualifiedAccess>]
module AkkaPersistence =
    val configuration: connectionString:string -> Config

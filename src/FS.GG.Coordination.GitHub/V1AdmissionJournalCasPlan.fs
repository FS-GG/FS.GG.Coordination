namespace FS.GG.Coordination.GitHub

open System
open System.IO
open System.Text.Json

[<RequireQualifiedAccess>]
module V1AdmissionJournalCasPlan =
    let private address =
        ShardedJournalAdapter.address Operation "fleet-v1-admission:fs-gg-production"
        |> Result.defaultWith (string >> invalidOp)

    let encode (proposal: RegistryAppendProposal) =
        let cas = V1AdmissionRegistry.proposalCas proposal
        let objects = V1AdmissionRegistry.proposalObjects proposal
        let commit = V1AdmissionRegistry.gitObjectIdValue objects.CommitObjectId
        let parent = cas.ObservedObjectId
        if cas.Address <> address
           || cas.ProposedCommit.ParentOid <> Some parent
           || cas.ProposedCommit.CommitOid <> commit
           || cas.Refspec <> $"{commit}:{address.Ref}"
           || cas.ForceWithLease <> $"--force-with-lease={address.Ref}:{parent}" then
            Error [ "admission-cas-typed-plan-binding" ]
        elif [ objects.EventBytes; objects.HeadBytes; objects.TreeBytes; objects.CommitBytes ]
             |> List.exists (fun bytes -> bytes.Length = 0 || bytes.Length > 8192) then
            Error [ "admission-cas-typed-object-size" ]
        else
            use stream = new MemoryStream()
            use writer = new Utf8JsonWriter(stream)
            writer.WriteStartObject()
            writer.WriteString("schema", "fsgg.v1-admission-journal-cas/1")
            writer.WriteNumber("repositoryId", 1351660651L)
            writer.WriteString("ref", address.Ref)
            writer.WriteString("expectedParent", parent)
            writer.WriteString("proposedCommit", commit)
            writer.WriteString("operationId", cas.OperationId)
            writer.WriteStartArray("objects")
            let writeObject (kind: string) (oid: GitObjectId) (bytes: byte array) =
                writer.WriteStartObject()
                writer.WriteString("kind", kind)
                writer.WriteString("oid", V1AdmissionRegistry.gitObjectIdValue oid)
                writer.WriteString("bytesBase64", Convert.ToBase64String bytes)
                writer.WriteEndObject()
            writeObject "blob" objects.EventObjectId objects.EventBytes
            writeObject "blob" objects.HeadObjectId objects.HeadBytes
            writeObject "tree" objects.TreeObjectId objects.TreeBytes
            writeObject "commit" objects.CommitObjectId objects.CommitBytes
            writer.WriteEndArray()
            writer.WriteEndObject()
            writer.Flush()
            Ok(stream.ToArray())

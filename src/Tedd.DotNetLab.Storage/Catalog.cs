using Tedd.DotNetLab.Contracts;
using Tedd.DotNetLab.Domain;
using Microsoft.Data.Sqlite;

namespace Tedd.DotNetLab.Storage;

// All database writes resolve to this fixed local SQLite file. No server,
// credentials, overlays, environment variables, or connection-string input.
public sealed partial class Catalog : IDisposable
{
    private readonly string connectionString;
    private readonly SemaphoreSlim writer = new(1, 1);
    private readonly FileStream controllerLock;
    private readonly Action? beforeWriteCommit;
    private readonly long backupDiskReserveBytes;
    public string Root { get; }
    public Catalog(string root,Action<int>? migrationApplied=null,Action? beforeWriteCommit=null,long backupDiskReserveBytes=268435456)
    {
        if(backupDiskReserveBytes<0) throw new LabException("invalid_request","Catalog disk reserve must be nonnegative.");
        this.backupDiskReserveBytes=backupDiskReserveBytes;
        this.beforeWriteCommit=beforeWriteCommit;
        Root = Path.GetFullPath(root);
        for(var directory=new DirectoryInfo(Root);directory is not null;directory=directory.Parent)
            if(directory.Exists&&(directory.Attributes&FileAttributes.ReparsePoint)!=0)
                throw new LabException("invalid_workspace","Catalog workspaces cannot traverse reparse points.");
        if (Root.StartsWith(@"\\", StringComparison.Ordinal) ||
            new DriveInfo(Path.GetPathRoot(Root)!).DriveType == DriveType.Network)
            throw new LabException("invalid_workspace", "SQLite requires a local filesystem.");
        Directory.CreateDirectory(Root);
        CatalogFileSystem.Validate(Root);
        controllerLock = new FileStream(Path.Combine(Root, "controller.lock"), FileMode.OpenOrCreate,
            FileAccess.ReadWrite, FileShare.Read);
        connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = Path.Combine(Root, "catalog.sqlite"), Mode = SqliteOpenMode.ReadWriteCreate,
            ForeignKeys = true, Pooling = false, DefaultTimeout = 1
        }.ToString();
        try
        {
            EnsureSafeCatalogFiles(); InitializeSchema(migrationApplied);
        }
        catch(SqliteException error)
        {
            controllerLock.Dispose();
            throw new LabException(error.SqliteErrorCode is 5 or 6?"catalog_busy":"catalog_unavailable",
                "Catalog could not be opened or validated; source files were preserved.","Retry after contention ends, or inspect the original local catalog with SQLite recovery tools.");
        }
        catch { controllerLock.Dispose(); throw; }
    }
    private SqliteConnection Open()
    {
        var db = new SqliteConnection(connectionString);
        try
        {
            EnsureSafeCatalogFiles(); db.Open();
            using var durability=db.CreateCommand(); durability.CommandText="PRAGMA synchronous=FULL"; durability.ExecuteNonQuery();
            return db;
        }
        catch { db.Dispose(); throw; }
    }
    private static SqliteCommand Command(SqliteConnection db, SqliteTransaction? transaction, string sql,
        params (string, object?)[] args)
    {
        var command = db.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        foreach (var (name, value) in args) command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        return command;
    }
    private T Write<T>(Func<SqliteConnection, SqliteTransaction, T> body)
    {
        if(!writer.Wait(TimeSpan.FromSeconds(5))) throw new LabException("catalog_busy","Catalog writer admission timed out.");
        try
        {
            for(int attempt=0;attempt<3;attempt++)
            {
                try
                {
                    using var db=Open(); using var transaction=db.BeginTransaction();
                    T result=body(db,transaction); beforeWriteCommit?.Invoke(); transaction.Commit(); return result;
                }
                catch(SqliteException error) when(error.SqliteErrorCode is 5 or 6)
                {
                    if(attempt==2) throw new LabException("catalog_busy","Catalog contention exceeded three bounded attempts.","Retry with the same idempotency key after the other writer releases its transaction.");
                    Thread.Sleep(TimeSpan.FromMilliseconds(50*(attempt+1)));
                }
            }
            throw new InvalidOperationException("Catalog retry bound exhausted.");
        }
        finally { writer.Release(); }
    }
    public NotebookRecord Put<T>(string id, string kind, T value, long? expectedRevision = null)
    {
        string json = Wire.Serialize(value);
        string hash = Wire.Hash(value);
        if (json.Length > 1048576) throw new LabException("resource_limit", "Catalog record exceeds 1 MiB.");
        return Write((db, tx) =>
        {
            using var query = Command(db, tx, "SELECT COALESCE(MAX(Revision),0) FROM Record WHERE Id=$id", ("$id", id));
            long previous = Convert.ToInt64(query.ExecuteScalar());
            if (previous != (expectedRevision ?? 0))
                throw new LabException("revision_conflict", "Record already exists or expected revision is stale.");
            if (previous > 0)
            {
                using var old = Command(db, tx, "SELECT Kind,Json FROM Record WHERE Id=$id AND Revision=$rev", ("$id",id),("$rev",previous));
                using var read = old.ExecuteReader(); read.Read();
                if (read.GetString(0) != kind || !new[]{"hypothesis","work_item","target","capture","execution","baseline_alias"}.Contains(kind))
                    throw new LabException("immutable_record", "Completed evidence and record kinds are immutable.");
                using var document = System.Text.Json.JsonDocument.Parse(read.GetString(1));
                if (kind is "capture" or "execution" && document.RootElement.TryGetProperty("state",out var state) &&
                    new[]{"sealed","incomplete","failed","completed"}.Contains(state.GetString()))
                    throw new LabException("immutable_record", "Completed evidence cannot be revised.");
            }
            var result = new NotebookRecord(id, kind, previous + 1, hash, json, DateTimeOffset.UtcNow);
            using var insert = Command(db, tx, "INSERT INTO Record VALUES($id,$kind,$rev,$hash,$json,$at)",
                ("$id", id), ("$kind", kind), ("$rev", result.Revision), ("$hash", hash),
                ("$json", json), ("$at", result.CreatedAt.ToString("O")));
            insert.ExecuteNonQuery();
            Audit(db, tx, "record_created", id, hash);
            return result;
        });
    }
    public NotebookRecord Get(string id, long? revision = null)
    {
        using var db = Open();
        using var command = Command(db, null,
            "SELECT Id,Kind,Revision,Hash,Json,CreatedAt FROM Record WHERE Id=$id AND ($rev IS NULL OR Revision=$rev) ORDER BY Revision DESC LIMIT 1",
            ("$id", id), ("$rev", revision));
        using var reader = command.ExecuteReader();
        if (!reader.Read()) throw new LabException("record_not_found", "Record does not exist.");
        return ReadRecord(reader);
    }
    public T Get<T>(string id) => Wire.Deserialize<T>(Get(id).Json);
    public IReadOnlyList<NotebookRecord> RecordsByPrefix(string kind,string prefix,int maximum=128)
    {
        if(prefix.Length is <1 or >128||maximum is <1 or >1024) throw new LabException("resource_limit","Invalid record-prefix bounds.");
        using var db=Open();
        using var query=Command(db,null,"""
            SELECT Id,Kind,Revision,Hash,Json,CreatedAt FROM Record
            WHERE Kind=$kind AND substr(Id,1,length($prefix))=$prefix ORDER BY Id,Revision LIMIT $maximum
            """,("$kind",kind),("$prefix",prefix),("$maximum",maximum+1));
        using var reader=query.ExecuteReader(); var records=new List<NotebookRecord>();
        while(reader.Read())
        {
            if(records.Count==maximum) throw new LabException("resource_limit","Record-prefix query exceeds policy.");
            records.Add(ReadRecord(reader));
        }
        return records;
    }
    private static NotebookRecord ReadRecord(SqliteDataReader r) =>
        new(r.GetString(0), r.GetString(1), r.GetInt64(2), r.GetString(3), r.GetString(4), DateTimeOffset.Parse(r.GetString(5)));
    public Page<NotebookRecord> List(string kind, int limit = 100, string? cursor = null,bool includeOversizedFirst=false)
    {
        if (limit is < 1 or > 1000) throw new LabException("resource_limit", "Page limit must be 1..1000.");
        string after = "";
        if (cursor is not null)
        {
            NotebookRecord anchor;
            try { anchor=Get(cursor); }
            catch(LabException error) when(error.Code=="record_not_found")
            { throw new LabException("invalid_cursor","Cursor anchor is unknown or no longer retained."); }
            if (anchor.Kind != kind) throw new LabException("invalid_cursor", "Cursor belongs to another view.");
            after = anchor.Id;
        }
        using var db = Open();
        using var command = Command(db, null, """
            SELECT r.Id,r.Kind,r.Revision,r.Hash,r.Json,r.CreatedAt FROM Record r
            WHERE r.Kind=$kind AND r.Id>$after
            AND r.Revision=(SELECT MAX(s.Revision) FROM Record s WHERE s.Id=r.Id)
            ORDER BY r.Id LIMIT $limit
            """, ("$kind", kind), ("$after", after), ("$limit", limit + 1));
        using var reader = command.ExecuteReader();
        var rows = new List<NotebookRecord>();
        int bytes = 0;
        bool truncated = false;
        while (reader.Read())
        {
            var row = ReadRecord(reader);
            int size = System.Text.Encoding.UTF8.GetByteCount(Wire.Serialize(row));
            if (rows.Count == limit || bytes + size > 60000 && !(includeOversizedFirst && rows.Count==0))
            {
                if (rows.Count == 0) throw new LabException("resource_limit", "Record requires artifact retrieval.");
                truncated = true;
                break;
            }
            rows.Add(row);
            bytes += size;
        }
        return new(rows, truncated, truncated ? rows[^1].Id : null);
    }
    public Operation? FindAccepted(string caller,string key,string hash)
    {
        if(string.IsNullOrWhiteSpace(key)||key.Length>128||caller.Length>128)
            throw new LabException("invalid_request","Invalid idempotency identity.");
        using var db=Open();
        using var lookup=Command(db,null,"SELECT Hash,OperationId FROM IdempotencyRecord WHERE Caller=$caller AND Key=$key",
            ("$caller",caller),("$key",key));
        string id;
        using(var reader=lookup.ExecuteReader())
        {
            if(!reader.Read()) return null;
            if(reader.GetString(0)!=hash) throw new LabException("idempotency_conflict","Key was used for another request.");
            id=reader.GetString(1);
        }
        return ReadOperation(db,null,id);
    }
    public Operation Accept(string caller, string key, string hash, string kind, string? targetId,string? policyId=null)
    {
        if (string.IsNullOrWhiteSpace(key) || key.Length > 128 || caller.Length > 128)
            throw new LabException("invalid_request", "Invalid idempotency identity.");
        return Write((db, tx) =>
        {
            using var lookup = Command(db, tx,
                "SELECT Hash,OperationId FROM IdempotencyRecord WHERE Caller=$caller AND Key=$key",
                ("$caller", caller), ("$key", key));
            using (var reader = lookup.ExecuteReader())
                if (reader.Read())
                {
                    if (reader.GetString(0) != hash) throw new LabException("idempotency_conflict", "Key was used for another request.");
                    return ReadOperation(db, tx, reader.GetString(1));
                }
            var operation = new Operation(Wire.Id("op"), kind, OperationState.Accepted, 1,
                targetId, null, null, DateTimeOffset.UtcNow);
            using var insert = Command(db, tx, """
                INSERT INTO Operation VALUES($id,$kind,$state,1,$target,NULL,NULL,$at);
                INSERT INTO OperationEvent VALUES($id,1,$state,$at);
                INSERT INTO IdempotencyRecord VALUES($caller,$key,$hash,$id);
                """, ("$id", operation.Id), ("$kind", kind), ("$state", operation.State.ToString()),
                ("$target", targetId), ("$at", operation.CreatedAt.ToString("O")),
                ("$caller", caller), ("$key", key), ("$hash", hash));
            insert.ExecuteNonQuery();
            if(policyId is not null)
            {
                ReadPolicy(db,tx,policyId);
                using var binding=Command(db,tx,"INSERT INTO OperationPolicy VALUES($operation,$policy)",("$operation",operation.Id),("$policy",policyId));
                binding.ExecuteNonQuery();
            }
            Audit(db, tx, "operation_accepted", operation.Id, kind);
            return operation;
        });
    }
    private static Operation ReadOperation(SqliteConnection db, SqliteTransaction? tx, string id)
    {
        using var query = Command(db, tx, "SELECT * FROM Operation WHERE Id=$id", ("$id", id));
        using var reader = query.ExecuteReader();
        if (!reader.Read()) throw new LabException("operation_not_found", "Unknown operation.");
        return new(reader.GetString(0), reader.GetString(1), Enum.Parse<OperationState>(reader.GetString(2)),
            reader.GetInt64(3), reader.IsDBNull(4) ? null : reader.GetString(4),
            reader.IsDBNull(5) ? null : reader.GetString(5), reader.IsDBNull(6) ? null : reader.GetString(6),
            DateTimeOffset.Parse(reader.GetString(7)));
    }
    private T Read<T>(Func<SqliteConnection,T> query)
    {
        for(int attempt=0;;attempt++)
        {
            try { using var db=OpenReadOnly(); return query(db); }
            catch(SqliteException error) when(error.SqliteErrorCode is 5 or 6)
            {
                if(attempt==2) throw new LabException("catalog_busy","Catalog read contention exceeded three bounded attempts.",
                    "Retry this status query after catalog contention ends.");
                Thread.Sleep(TimeSpan.FromMilliseconds(50*(attempt+1)));
            }
        }
    }
    public Operation GetOperation(string id)=>Read(db=>ReadOperation(db,null,id));
    public Operation Transition(string id, OperationState state, string? resultId = null, string? error = null) =>
        Write((db, tx) =>
        {
            var old = ReadOperation(db, tx, id);
            if (old.State == state) return old;
            Lifecycle.Transition(old.State, state);
            using var update = Command(db, tx, """
                UPDATE Operation SET State=$state,Revision=Revision+1,ResultId=$result,ErrorCode=$error WHERE Id=$id;
                INSERT INTO OperationEvent VALUES($id,$rev,$state,$at);
                """, ("$id", id), ("$state", state.ToString()), ("$result", resultId), ("$error", error),
                ("$rev", old.Revision + 1), ("$at", DateTimeOffset.UtcNow.ToString("O")));
            update.ExecuteNonQuery();
            if(Lifecycle.Terminal(state))
            {
                // Completion certifies quiescence. Publish it with lease release, never before cleanup.
                using var leases=Command(db,tx,"SELECT TargetId FROM TargetLease WHERE OperationId=$op",("$op",id));
                var owned=new List<string>();
                using(var reader=leases.ExecuteReader()) while(reader.Read()) owned.Add(reader.GetString(0));
                foreach(string target in owned) ReleaseLeaseCore(db,tx,target,id,state is OperationState.Completed or OperationState.Failed);
            }
            return old with { State = state, Revision = old.Revision + 1, ResultId = resultId, ErrorCode = error };
        });
    public void AcquireLease(string targetId, string operationId) => Write((db, tx) =>
    {
        using var taint = Command(db, tx, "SELECT Reason FROM TaintedTarget WHERE TargetId=$id", ("$id", targetId));
        if (taint.ExecuteScalar() is string reason) throw new LabException("target_tainted", reason, "Restart the target.");
        using var insert = Command(db, tx, "INSERT INTO TargetLease VALUES($target,$op)",
            ("$target", targetId), ("$op", operationId));
        try { insert.ExecuteNonQuery(); }
        catch (SqliteException e) when (e.SqliteErrorCode == 19) { throw new LabException("lease_conflict", "Target is leased."); }
        return true;
    });
    public void ReleaseLease(string targetId, string operationId, bool quiescent) => Write((db, tx) =>
    {
        ReleaseLeaseCore(db,tx,targetId,operationId,quiescent);
        return true;
    });
    private static void ReleaseLeaseCore(SqliteConnection db,SqliteTransaction tx,string targetId,string operationId,bool quiescent)
    {
        using var release = Command(db, tx, "DELETE FROM TargetLease WHERE TargetId=$id AND OperationId=$op",
            ("$id", targetId), ("$op", operationId));
        if(release.ExecuteNonQuery()==0) return; // A former owner cannot clear or taint a successor's lease.
        if (!quiescent)
        {
            using var owner=Command(db,tx,"SELECT Kind FROM Operation WHERE Id=$op",("$op",operationId));
            string reason=owner.ExecuteScalar() as string=="profiler_attach"?"Profiler attachment unresolved: "+operationId:"Quiescence was not established.";
            using var mark = Command(db, tx, "INSERT OR REPLACE INTO TaintedTarget VALUES($id,$reason)",
                ("$id", targetId), ("$reason", reason));
            mark.ExecuteNonQuery();
            using var revoke = Command(db,tx,"DELETE FROM RecoverableMutation WHERE TargetId=$id",("$id",targetId));
            revoke.ExecuteNonQuery();
        }
    }
    public void UseGrant(Grant grant) => Write((db, tx) =>
    {
        using var use = Command(db, tx, """
            INSERT INTO GrantUse VALUES($id,1) ON CONFLICT(GrantId) DO UPDATE SET Count=Count+1
            WHERE Count<$maximum RETURNING Count
            """, ("$id", grant.Id), ("$maximum", grant.MaximumOperations));
        if (use.ExecuteScalar() is null) throw new LabException("permission_denied", "Grant operation budget exhausted.");
        Audit(db, tx, "grant_used", grant.Id, grant.Permission.ToString());
        return true;
    });
    public void Recover()
    {
        Write((db, tx) =>
        {
            using var taint = Command(db, tx, """
                INSERT OR IGNORE INTO RecoverableMutation
                SELECT l.TargetId,l.OperationId FROM TargetLease l JOIN Operation o ON o.Id=l.OperationId
                WHERE o.Kind='mutation' AND o.State IN ('Accepted','Running','CancellationRequested','Completed','Failed')
                AND NOT EXISTS(SELECT 1 FROM TaintedTarget t WHERE t.TargetId=l.TargetId);
                INSERT OR IGNORE INTO TaintedTarget
                SELECT l.TargetId,CASE WHEN o.Kind='profiler_attach' THEN 'Profiler attachment unresolved: '||o.Id
                    WHEN o.Kind IN ('execution','individual_latencies') OR o.State='Tainted' THEN 'Controller interrupted execution or unresolved restoration; restart required.'
                    ELSE 'Controller interrupted mutation; explicit reconciliation or restart required.' END
                FROM TargetLease l JOIN Operation o ON o.Id=l.OperationId
                WHERE o.Kind IN ('execution','mutation','mutation_recovery','individual_latencies','profiler_attach')
                AND NOT (o.Kind IN ('mutation_recovery','profiler_attach') AND o.State='Completed');
                DELETE FROM TargetLease;
                INSERT INTO OperationEvent SELECT Id,Revision+1,'Interrupted',strftime('%Y-%m-%dT%H:%M:%fZ','now')
                FROM Operation WHERE State IN ('Accepted','Running','CancellationRequested');
                UPDATE Operation SET State='Interrupted',Revision=Revision+1,ErrorCode='controller_interrupted'
                WHERE State IN ('Accepted','Running','CancellationRequested');
                """);
            taint.ExecuteNonQuery();
            return true;
        });
    }
    public string? RecoverableMutationOperation(string targetId)
    {
        using var db=Open();
        using var query=Command(db,null,"SELECT OperationId FROM RecoverableMutation WHERE TargetId=$id",("$id",targetId));
        return query.ExecuteScalar() as string;
    }
    public void AcquireMutationRecoveryLease(string targetId,string operationId,string sourceOperationId) => Write((db,tx)=>
    {
        var operation=ReadOperation(db,tx,operationId); var source=ReadOperation(db,tx,sourceOperationId);
        using var proof=Command(db,tx,"""
            SELECT 1 FROM RecoverableMutation r JOIN TaintedTarget t ON t.TargetId=r.TargetId
            WHERE r.TargetId=$target AND r.OperationId=$source
            """,("$target",targetId),("$source",sourceOperationId));
        if(operation.Kind!="mutation_recovery"||operation.TargetId!=targetId||operation.State!=OperationState.Accepted||
            source.Kind!="mutation"||source.TargetId!=targetId||source.State is not (OperationState.Interrupted or OperationState.Completed or OperationState.Failed)||proof.ExecuteScalar() is null)
            throw new LabException("target_tainted","Only the recorded interrupted mutation can receive a recovery lease.","Restart the target or reconcile its interrupted mutation.");
        using var lease=Command(db,tx,"INSERT INTO TargetLease VALUES($target,$op)",("$target",targetId),("$op",operationId));
        try { lease.ExecuteNonQuery(); }
        catch(SqliteException e) when(e.SqliteErrorCode==19) { throw new LabException("lease_conflict","Target is leased."); }
        Audit(db,tx,"mutation_recovery_started",operationId,sourceOperationId); return true;
    });
    public void CompleteMutationRecovery(string targetId,string operationId,string sourceOperationId,string evidenceId) => Write((db,tx)=>
    {
        var operation=ReadOperation(db,tx,operationId);
        using var proof=Command(db,tx,"""
            SELECT 1 FROM RecoverableMutation r JOIN TargetLease l ON l.TargetId=r.TargetId
            WHERE r.TargetId=$target AND r.OperationId=$source AND l.OperationId=$op
            """,("$target",targetId),("$source",sourceOperationId),("$op",operationId));
        using var evidence=Command(db,tx,"SELECT Kind,Hash,Json FROM Record WHERE Id=$id AND Revision=1",("$id",evidenceId));
        MutationRecoveryResult result;
        using(var reader=evidence.ExecuteReader())
        {
            if(!reader.Read()||reader.GetString(0)!="mutation_recovery_result") throw new LabException("recovery_unverified","Recovery evidence is absent.");
            result=Wire.Deserialize<MutationRecoveryResult>(reader.GetString(2));
            if(reader.GetString(1)!=Wire.Hash(result)) throw new LabException("artifact_corrupt","Recovery evidence hash differs.");
        }
        if(operation.Kind!="mutation_recovery"||operation.TargetId!=targetId||
            operation.State is not (OperationState.Running or OperationState.CancellationRequested)||proof.ExecuteScalar() is null||
            result.Id!=evidenceId||result.OperationId!=operationId||result.SourceOperationId!=sourceOperationId||result.Target.Id!=targetId||
            !result.OriginalBodyVerified||!result.HealthVerified||result.PristineRuntime||result.PromotionEligible||
            result.Phase is not ("restored" or "not_applied")||result.SourceJournalHeadHash.Length!=64||result.CandidateHash.Length!=64||
            result.OriginalBodyHash.Length!=64||string.IsNullOrEmpty(result.MethodScope)||string.IsNullOrEmpty(result.ModuleLoadIdentity)||
            string.IsNullOrEmpty(result.NativeManifestArtifactId)||string.IsNullOrEmpty(result.ValidationArtifactId))
            throw new LabException("recovery_unverified","Original-body and application-health evidence must match the leased recovery.");
        foreach(string artifactId in new[]{result.NativeManifestArtifactId,result.ValidationArtifactId})
        {
            using var artifactQuery=Command(db,tx,"""
                SELECT r.Hash,r.Json FROM Record r JOIN ArtifactPin p ON p.ArtifactId=r.Id
                WHERE r.Id=$id AND r.Kind='artifact' AND r.Revision=1
                """,("$id",artifactId));
            using var artifactReader=artifactQuery.ExecuteReader();
            if(!artifactReader.Read()) throw new LabException("recovery_unverified","Recovery artifacts must be sealed and pinned.");
            var artifact=Wire.Deserialize<Artifact>(artifactReader.GetString(1));
            if(!artifact.Complete||artifact.Id!=artifactId||artifactReader.GetString(0)!=Wire.Hash(artifact))
                throw new LabException("recovery_unverified","Recovery artifact metadata is unverified.");
        }
        using var complete=Command(db,tx,"""
            UPDATE Operation SET State='Completed',Revision=Revision+1,ResultId=$evidence,ErrorCode=NULL WHERE Id=$op;
            INSERT INTO OperationEvent VALUES($op,$revision,'Completed',$at);
            DELETE FROM TaintedTarget WHERE TargetId=$target;
            DELETE FROM RecoverableMutation WHERE TargetId=$target;
            DELETE FROM TargetLease WHERE TargetId=$target AND OperationId=$op;
            """,("$op",operationId),("$target",targetId),("$evidence",evidenceId),("$revision",operation.Revision+1),("$at",DateTimeOffset.UtcNow.ToString("O")));
        complete.ExecuteNonQuery(); Audit(db,tx,"mutation_recovery_verified",operationId,evidenceId); return true;
    });
    public void Pin(string artifactId, string reason) => Write((db, tx) =>
    {
        EnsureArtifactRetained(db,tx,artifactId);
        using var cmd = Command(db, tx, "INSERT OR IGNORE INTO ArtifactPin VALUES($id,$reason)",
            ("$id", artifactId), ("$reason", reason));
        cmd.ExecuteNonQuery(); return true;
    });
    public bool IsPinned(string artifactId)
    {
        using var db = Open();
        using var cmd = Command(db, null, "SELECT 1 FROM ArtifactPin WHERE ArtifactId=$id", ("$id", artifactId));
        return cmd.ExecuteScalar() is not null;
    }
    public CatalogBackup Backup(string destination)
    {
        if(!writer.Wait(TimeSpan.FromSeconds(5))) throw new LabException("catalog_busy","Catalog backup admission timed out.");
        try
        {
            using var source=OpenReadOnly(); return CreateVerifiedBackup(source,destination,CurrentSchemaVersion);
        }
        finally { writer.Release(); }
    }
    private static void Audit(SqliteConnection db, SqliteTransaction tx, string action, string id, string detail)
    {
        using var cmd = Command(db, tx, "INSERT INTO AuditEvent(Action,EntityId,Detail,CreatedAt) VALUES($action,$id,$detail,$at)",
            ("$action", action), ("$id", id), ("$detail", detail), ("$at", DateTimeOffset.UtcNow.ToString("O")));
        cmd.ExecuteNonQuery();
    }
    public void AppendAudit(string action,string id,string detail)=>Write((db,tx)=>
    {
        if(action.Length is <1 or >128||id.Length>256||detail.Length>16384) throw new LabException("resource_limit","Audit append exceeds its bounds.");
        Audit(db,tx,action,id,detail); return true;
    });
    public void Dispose() { controllerLock.Dispose(); writer.Dispose(); }
}

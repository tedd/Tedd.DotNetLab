using Tedd.DotNetLab.Application;
using Tedd.DotNetLab.Mcp;
using Tedd.DotNetLab.Contracts;
using Tedd.DotNetLab.Storage;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Tedd.DotNetLab.Tests;

public sealed class CatalogMigrationTests
{
    // Every fixture connection resolves directly to this task-owned local SQLite file.
    private static SqliteConnection Open(string root,bool readOnly=false)
    {
        var db=new SqliteConnection(new SqliteConnectionStringBuilder { DataSource=Path.Combine(root,"catalog.sqlite"),
            Mode=readOnly?SqliteOpenMode.ReadOnly:SqliteOpenMode.ReadWriteCreate,Pooling=false,DefaultTimeout=1 }.ToString());
        db.Open(); return db;
    }
    private static void Execute(SqliteConnection db,string sql)
    { using var command=db.CreateCommand(); command.CommandText=sql; command.ExecuteNonQuery(); }
    private static long Scalar(SqliteConnection db,string sql)
    { using var command=db.CreateCommand(); command.CommandText=sql; return Convert.ToInt64(command.ExecuteScalar()); }
    private static void Legacy(string root,int version)
    {
        using var db=Open(root); Execute(db,"PRAGMA journal_mode=WAL");
        for(int step=1;step<=version;step++)
        {
            Execute(db,CatalogMigrations.Script(step));
            using var history=db.CreateCommand(); history.CommandText="INSERT INTO SchemaMigration VALUES($version,$at)";
            history.Parameters.AddWithValue("$version",step); history.Parameters.AddWithValue("$at",DateTimeOffset.UtcNow.ToString("O")); history.ExecuteNonQuery();
        }
        string json=Wire.Serialize(new { evidence="legacy evidence" });
        using var insert=db.CreateCommand(); insert.CommandText="INSERT INTO Record VALUES('legacy','hypothesis',1,$hash,$json,$at)";
        insert.Parameters.AddWithValue("$hash",Wire.Hash(new { evidence="legacy evidence" })); insert.Parameters.AddWithValue("$json",json);
        insert.Parameters.AddWithValue("$at",DateTimeOffset.UtcNow.ToString("O")); insert.ExecuteNonQuery();
    }
    [Theory,InlineData(1),InlineData(2),Case("UT-ART-014","FR-072,FR-074","M1","Integration")]
    public void SupportedLegacySchemas_MigrateAtomicallyAndPreserveVerifiedBackup(int version)
    {
        using var workspace=new TestWorkspace(); Legacy(workspace.Root,version);
        using var catalog=new Catalog(workspace.Root);
        var backup=Assert.IsType<CatalogBackup>(catalog.MigrationBackup); Assert.Equal(version,backup.SchemaVersion); catalog.VerifyBackup(backup);
        Assert.Equal(Wire.Hash(new { evidence="legacy evidence" }),catalog.Get("legacy").Hash);
        using var current=Open(workspace.Root,true); Assert.Equal(Catalog.CurrentSchemaVersion,Scalar(current,"SELECT MAX(Version) FROM SchemaMigration"));
        string backupRoot=Path.GetDirectoryName(Path.Combine(workspace.Root,backup.RelativePath))!;
        using var old=new SqliteConnection(new SqliteConnectionStringBuilder { DataSource=Path.Combine(workspace.Root,backup.RelativePath),Mode=SqliteOpenMode.ReadOnly,Pooling=false }.ToString());
        old.Open(); Assert.Equal(version,Scalar(old,"SELECT MAX(Version) FROM SchemaMigration")); Assert.Equal(1,Scalar(old,"SELECT COUNT(*) FROM Record WHERE Id='legacy'"));
        Assert.True(Directory.Exists(backupRoot));
    }
    [Theory,InlineData("future"),InlineData("gap"),InlineData("missing_column"),InlineData("trigger"),InlineData("sqlite_prefix_trigger"),Case("UT-ART-014,CT-MCP-012","FR-074","M1","Integration")]
    public void UnsupportedOrMalformedSchema_IsNotModifiedAndHasConsistentBackup(string defect)
    {
        using var workspace=new TestWorkspace(); Legacy(workspace.Root,2);
        using(var db=Open(workspace.Root))
        {
            Execute(db,defect switch
            {
                "future"=>$"INSERT INTO SchemaMigration VALUES({Catalog.CurrentSchemaVersion+1},'future')",
                "gap"=>"DELETE FROM SchemaMigration WHERE Version=1",
                "missing_column"=>"ALTER TABLE Record DROP COLUMN Hash",
                "sqlite_prefix_trigger"=>"CREATE TRIGGER sqliteUnexpected AFTER INSERT ON Record BEGIN DELETE FROM ArtifactPin; END",
                _=>"CREATE TRIGGER unexpected AFTER INSERT ON Record BEGIN DELETE FROM ArtifactPin; END"
            });
        }
        var error=Assert.Throws<LabException>(()=>new Catalog(workspace.Root));
        Assert.Equal(defect=="future"?"schema_unsupported":"catalog_schema_invalid",error.Code); Assert.Contains("Preserved consistent backup",error.Recovery);
        string file=Assert.Single(Directory.EnumerateFiles(Path.Combine(workspace.Root,"migration-backups"),"*.sqlite"));
        Assert.True(File.Exists(file+".manifest.json"));
        using var original=Open(workspace.Root,true); Assert.Equal(0,Scalar(original,"SELECT COUNT(*) FROM sqlite_schema WHERE name='ArtifactReference'"));
        using var copied=new SqliteConnection(new SqliteConnectionStringBuilder { DataSource=file,Mode=SqliteOpenMode.ReadOnly,Pooling=false }.ToString()); copied.Open();
        Assert.Equal(Scalar(original,"SELECT MAX(Version) FROM SchemaMigration"),Scalar(copied,"SELECT MAX(Version) FROM SchemaMigration"));
        Assert.Equal(1,Scalar(copied,"SELECT COUNT(*) FROM Record WHERE Id='legacy'"));
    }
    [Fact,Case("UT-ART-014","FR-074","M1","Integration")]
    public void FailureBetweenMigrations_RollsBackEverySchemaChange()
    {
        using var workspace=new TestWorkspace(); Legacy(workspace.Root,1);
        var error=Assert.Throws<LabException>(()=>new Catalog(workspace.Root,migrationApplied:version=> { if(version==2) throw new IOException("injected migration failure"); }));
        Assert.Equal("catalog_migration_failed",error.Code); Assert.Contains("Preserved consistent backup",error.Recovery);
        using(var db=Open(workspace.Root,true))
        {
            Assert.Equal(1,Scalar(db,"SELECT MAX(Version) FROM SchemaMigration"));
            Assert.Equal(0,Scalar(db,"SELECT COUNT(*) FROM sqlite_schema WHERE name IN ('RecoverableMutation','ArtifactReference','ArtifactDeletion')"));
            Assert.Equal(1,Scalar(db,"SELECT COUNT(*) FROM Record WHERE Id='legacy'"));
        }
        using var retried=new Catalog(workspace.Root); Assert.Equal(1,retried.Get("legacy").Revision);
    }
    [Fact,Case("UT-ART-013","FR-062,FR-072","M1","Integration")]
    public void BusyBeforeCommit_RetriesRolledBackTransactionWithoutDuplicateOperation()
    {
        using var workspace=new TestWorkspace(); int attempts=0;
        using var catalog=new Catalog(workspace.Root,beforeWriteCommit:()=> { if(++attempts<=2) throw new SqliteException("injected busy",5); });
        var operation=catalog.Accept("caller","key","hash","capture",null);
        Assert.Equal(3,attempts); Assert.Equal(operation.Id,catalog.Accept("caller","key","hash","capture",null).Id);
        using var db=Open(workspace.Root,true); Assert.Equal(1,Scalar(db,"SELECT COUNT(*) FROM Operation")); Assert.Equal(1,Scalar(db,"SELECT COUNT(*) FROM IdempotencyRecord"));
    }
    [Fact,Case("UT-ART-013","FR-062,FR-072","M1","Integration")]
    public void RepeatedBusy_StopsAtThreeAttemptsWithoutPartialRecord()
    {
        using var workspace=new TestWorkspace(); int attempts=0;
        using var catalog=new Catalog(workspace.Root,beforeWriteCommit:()=> { attempts++; throw new SqliteException("injected busy",5); });
        Assert.Equal("catalog_busy",Assert.Throws<LabException>(()=>catalog.Put("never-committed","hypothesis",new { value=1 })).Code);
        Assert.Equal(3,attempts); Assert.Equal("record_not_found",Assert.Throws<LabException>(()=>catalog.Get("never-committed")).Code);
    }
    [Fact,Case("IT-STORE-002","FR-072","M1","Integration")]
    public async Task RealWriterContention_ReleasesIntoOneCommittedRecord()
    {
        using var workspace=new TestWorkspace(); using var catalog=new Catalog(workspace.Root);
        using var external=Open(workspace.Root); using var held=external.BeginTransaction();
        var started=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var pending=Task.Run(()=> { started.SetResult(); return catalog.Put("contended","hypothesis",new { value=1 }); });
        await started.Task; await Task.Delay(200); Assert.False(pending.IsCompleted); held.Commit();
        var committed=await pending.WaitAsync(TimeSpan.FromSeconds(5)); Assert.Equal(1,committed.Revision);
        Assert.Equal(committed.Hash,catalog.Get("contended").Hash);
    }
    [Theory,InlineData(true),InlineData(false),Case("UT-ART-013,IT-STORE-002","FR-062,FR-072","M1","Integration")]
    public async Task OperationStatusContention_IsReadOnlyAndBounded(bool release)
    {
        using var workspace=new TestWorkspace(); int commits=0;
        using var catalog=new Catalog(workspace.Root,beforeWriteCommit:()=>commits++);
        var operation=catalog.Accept("status","key","hash","capture",null); int originalCommits=commits;
        using var external=Open(workspace.Root); Execute(external,"PRAGMA locking_mode=EXCLUSIVE; BEGIN EXCLUSIVE");
        var started=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var pending=Task.Run(()=> { started.SetResult(); return catalog.GetOperation(operation.Id); });
        await started.Task;
        try
        {
            if(release)
            {
                await Task.Delay(1500); Assert.False(pending.IsCompleted);
                Execute(external,"COMMIT"); external.Close();
                Assert.Equal(operation,await pending.WaitAsync(TimeSpan.FromSeconds(8)));
            }
            else
            {
                var failure=await Assert.ThrowsAsync<LabException>(async()=>await pending.WaitAsync(TimeSpan.FromSeconds(8)));
                Assert.Equal("catalog_busy",failure.Code);
                Execute(external,"COMMIT"); external.Close();
            }
            Assert.Equal(originalCommits,commits); Assert.Equal(operation,catalog.GetOperation(operation.Id));
            using var check=Open(workspace.Root,true); Assert.Equal(1,Scalar(check,"SELECT COUNT(*) FROM Operation"));
        }
        finally
        {
            external.Close();
            try { await pending.WaitAsync(TimeSpan.FromSeconds(8)); } catch { /* Preserve the original assertion; release and join the fixture reader. */ }
        }
    }
    [Fact,Case("IT-STORE-002","FR-072","M1","Integration")]
    public void WalReaderBackupAndCheckpoint_HaveExplicitSnapshotBoundaries()
    {
        using var workspace=new TestWorkspace(); using var catalog=new Catalog(workspace.Root);
        catalog.Put("first","hypothesis",new { value=1 });
        using var reader=Open(workspace.Root,true); using var readTransaction=reader.BeginTransaction(deferred:true);
        using(var query=reader.CreateCommand()) { query.Transaction=readTransaction; query.CommandText="SELECT COUNT(*) FROM Record"; Assert.Equal(1,Convert.ToInt32(query.ExecuteScalar())); }
        catalog.Put("second","hypothesis",new { value=2 }); var backup=catalog.CreateBackup(); catalog.VerifyBackup(backup);
        using(var copied=new SqliteConnection(new SqliteConnectionStringBuilder { DataSource=Path.Combine(workspace.Root,backup.RelativePath),Mode=SqliteOpenMode.ReadOnly,Pooling=false }.ToString()))
        { copied.Open(); Assert.Equal(2,Scalar(copied,"SELECT COUNT(*) FROM Record")); }
        var blocked=catalog.Checkpoint(true); Assert.True(blocked.Busy); readTransaction.Commit();
        var drained=catalog.Checkpoint(true); Assert.False(drained.Busy); Assert.Equal(0,drained.LogFrames);
        Assert.Equal("backup_exists",Assert.Throws<LabException>(()=>catalog.Backup(Path.Combine(workspace.Root,backup.RelativePath))).Code);
        Assert.Equal("invalid_path",Assert.Throws<LabException>(()=>catalog.Backup(Path.Combine(workspace.Root,"..","outside.sqlite"))).Code);
    }
    [Fact,Case("IT-STORE-002","FR-062,FR-072","M1","Integration")]
    public async Task BackupJob_PersistsItsVerifiedSnapshotAndDetectsLaterCorruption()
    {
        using var workspace=new TestWorkspace(); string backupId; CatalogBackup backup;
        await using(var service=new LabService(workspace.Root))
        {
            service.Hypothesis("snapshot evidence","persisted",[]);
            var tools=new LabTools(service); var response=await tools.Run(new StartCatalogBackup("snapshot")); Assert.Equal("ok",response.Status);
            var operation=Wire.Deserialize<Operation>(response.Data!.Value.GetRawText());
            var completed=await ServiceTests.Wait(service,operation.Id); Assert.Equal(OperationState.Completed,completed.State); backupId=completed.ResultId!;
            backup=service.VerifyCatalogBackup(backupId); Assert.Equal(operation.Id,service.StartCatalogBackup("snapshot").Id);
            Assert.Equal("ok",(await tools.Query(new QueryCatalogBackup(backupId))).Status);
            Assert.Equal("ok",(await tools.Run(new CheckpointCatalog())).Status);
        }
        await using var reopened=new LabService(workspace.Root); Assert.Equal(backup,reopened.VerifyCatalogBackup(backupId));
        await File.WriteAllBytesAsync(Path.Combine(workspace.Root,backup.RelativePath),[1,2,3]);
        Assert.Equal("backup_corrupt",Assert.Throws<LabException>(()=>reopened.VerifyCatalogBackup(backupId)).Code);
    }
    [Fact,Case("UT-ART-007","FR-016,FR-072","M1","Integration")]
    public void ImpossibleDiskReserve_DoesNotCreateOrOverwriteSnapshotBytes()
    {
        using var workspace=new TestWorkspace(); using var catalog=new Catalog(workspace.Root,backupDiskReserveBytes:long.MaxValue);
        Assert.Equal("resource_limit",Assert.Throws<LabException>(()=>catalog.CreateBackup()).Code);
        Assert.False(Directory.Exists(Path.Combine(workspace.Root,"backups")));
    }
    [Fact,Case("IT-STORE-002","FR-072","M1","Integration")]
    public async Task ConcurrentShortConnections_TolerateWalSidecarCreationAndRemoval()
    {
        using var workspace=new TestWorkspace(); using var catalog=new Catalog(workspace.Root);
        var seed=catalog.Put("seed","hypothesis",new { value=1 });
        var readers=Enumerable.Range(0,3).Select(_=>Task.Run(()=>
        {
            for(int read=0;read<100;read++) Assert.Equal(seed.Hash,catalog.Get(seed.Id).Hash);
        })).ToArray();
        var writer=Task.Run(()=>
        {
            for(int write=0;write<50;write++) catalog.Put("record-"+write,"hypothesis",new { value=write });
        });
        await Task.WhenAll(readers.Append(writer));
        Assert.Equal(51,catalog.List("hypothesis",100).Rows.Count);
    }
}

using Tedd.DotNetLab.Analysis.Runtime;
using Tedd.DotNetLab.Application;
using Tedd.DotNetLab.Contracts;
using Tedd.DotNetLab.Domain;
using Tedd.DotNetLab.Mcp;
using Tedd.DotNetLab.Storage;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Tedd.DotNetLab.Tests;

public sealed class WorkspacePolicyTests
{
    private static SqliteConnection Open(string root)
    {
        var db=new SqliteConnection(new SqliteConnectionStringBuilder { DataSource=Path.Combine(root,"catalog.sqlite"),Mode=SqliteOpenMode.ReadWrite,Pooling=false }.ToString());
        db.Open(); return db;
    }
    private static void Execute(SqliteConnection db,string text) { using var command=db.CreateCommand(); command.CommandText=text; command.ExecuteNonQuery(); }
    [Fact,Case("UT-WORKSPACE-001","FR-016,FR-062,FR-072,FR-074","M1","Integration")]
    public async Task WorkspaceAndHistoricalOperationPolicySurvivePolicyChangesAndRestart()
    {
        using var workspace=new TestWorkspace(); string id,policyId,operationId; var limits=new ResourceLimits(MaximumConcurrentOperations:2,MaximumQueuedActivities:3);
        await using(var service=new LabService(workspace.Root,limits))
        {
            id=service.Workspace.Workspace.Id; policyId=service.Workspace.Policy.Id; Assert.Equal(limits,service.Limits);
            operationId=service.Catalog.Accept("fixture","key","hash","fixture",null,policyId).Id;
            var reply=await new LabTools(service).Discover(new DiscoverWorkspace()); Assert.Equal("ok",reply.Status);
            Assert.Equal(id,reply.Data!.Value.GetProperty("workspace").GetProperty("id").GetString());
        }
        await using(var reopened=new LabService(workspace.Root)) { Assert.Equal(limits,reopened.Limits); Assert.Equal(id,reopened.Workspace.Workspace.Id); Assert.Equal(policyId,reopened.Workspace.Policy.Id); }
        await using(var changed=new LabService(workspace.Root,limits with { MaximumConcurrentOperations=4 }))
        {
            Assert.Equal(id,changed.Workspace.Workspace.Id); Assert.NotEqual(policyId,changed.Workspace.Policy.Id); Assert.Equal(2,changed.Workspace.Policy.Revision);
            var historical=changed.Catalog.OperationPolicy(operationId); Assert.Equal("available",historical.Availability); Assert.Equal(limits,historical.Policy!.Limits);
            Assert.Equal(operationId,changed.Catalog.Accept("fixture","key","hash","fixture",null,changed.Workspace.Policy.Id).Id);
            Assert.Equal(policyId,changed.Catalog.OperationPolicy(operationId).Policy!.Id);
        }
        await using var restored=new LabService(workspace.Root,limits); Assert.Equal(policyId,restored.Workspace.Policy.Id); Assert.Equal(1,restored.Workspace.Policy.Revision);
    }
    [Fact,Case("UT-WORKSPACE-002","FR-062,FR-072","M1","Integration")]
    public void PolicyAndOperationBindingRollbackAtomically()
    {
        using var workspace=new TestWorkspace(); bool fail=true;
        using var catalog=new Catalog(workspace.Root,beforeWriteCommit:()=> { if(fail) throw new IOException("injected publication failure"); });
        Assert.Throws<IOException>(()=>catalog.ConfigureWorkspace());
        using(var db=Open(workspace.Root))
        {
            using var count=db.CreateCommand(); count.CommandText="SELECT (SELECT COUNT(*) FROM Workspace)+(SELECT COUNT(*) FROM PolicyRevision)"; Assert.Equal(0L,count.ExecuteScalar());
        }
        fail=false; var context=catalog.ConfigureWorkspace(); fail=true;
        Assert.Throws<IOException>(()=>catalog.Accept("fixture","atomic","hash","fixture",null,context.Policy.Id));
        fail=false; Assert.Null(catalog.FindAccepted("fixture","atomic","hash"));
        var accepted=catalog.Accept("fixture","atomic","hash","fixture",null,context.Policy.Id); Assert.Equal(context.Policy.Id,catalog.OperationPolicy(accepted.Id).Policy!.Id);
        var legacy=catalog.Accept("fixture","legacy","hash","fixture",null); Assert.Equal("not_collected",catalog.OperationPolicy(legacy.Id).Availability);
    }
    [Fact,Case("UT-WORKSPACE-003","FR-072,FR-074","M1","Integration")]
    public async Task CorruptPolicyCannotSilentlyBecomeDefaults()
    {
        using var workspace=new TestWorkspace(); await using(var service=new LabService(workspace.Root)) Assert.Equal(5,service.Workspace.Workspace.SchemaVersion);
        using(var db=Open(workspace.Root)) Execute(db,"UPDATE PolicyRevision SET Hash='corrupt'");
        Assert.Equal("catalog_corrupt",Assert.Throws<LabException>(()=>new LabService(workspace.Root)).Code); Assert.True(File.Exists(Path.Combine(workspace.Root,"catalog.sqlite")));
        Assert.Equal("catalog_corrupt",Assert.Throws<LabException>(()=>new LabService(workspace.Root,new(MaximumConcurrentOperations:2))).Code);
    }
    [Fact,Case("UT-ART-014,UT-WORKSPACE-004","FR-072,FR-074","M1","Integration")]
    public void SchemaFourMigratesWithBackupAndFutureSixIsNotHiddenByHistoryLimit()
    {
        using var workspace=new TestWorkspace();
        using(var db=new SqliteConnection(new SqliteConnectionStringBuilder { DataSource=Path.Combine(workspace.Root,"catalog.sqlite"),Pooling=false }.ToString()))
        {
            db.Open(); for(int version=1;version<=4;version++) { Execute(db,CatalogMigrations.Script(version)); Execute(db,$"INSERT INTO SchemaMigration VALUES({version},'2026-10-14T00:00:00Z')"); }
        }
        using(var migrated=new Catalog(workspace.Root)) { Assert.Equal(4,migrated.MigrationBackup!.SchemaVersion); migrated.VerifyBackup(migrated.MigrationBackup); migrated.ConfigureWorkspace(); }
        using(var db=Open(workspace.Root)) Execute(db,"INSERT INTO SchemaMigration VALUES(6,'future')");
        Assert.Equal("schema_unsupported",Assert.Throws<LabException>(()=>new Catalog(workspace.Root)).Code);
    }
    [Fact,Case("UT-WORKSPACE-005","FR-016,FR-038,FR-062","M1","Integration")]
    public async Task JobCapPreservesReplayAndBindsRejectedAdmissionPolicy()
    {
        using var workspace=new TestWorkspace(); var gate=new ParserMeasurementGate(maximumQueuedActivities:2);
        await using var service=new LabService(workspace.Root,new(MaximumConcurrentOperations:1,MaximumQueuedActivities:2),activityScheduler:gate);
        var artifact=await service.Artifacts.WriteJsonAsync("source",new { invalid_trace="bounded fixture" },"fixture"); service.Catalog.Put(artifact.Id,"artifact",artifact);
        Capture Make(string id)=>new(id,new("retained",1,1,"runtime","10.0.12","x64"),Profiles.Resolve("baseline-context"),CaptureState.Sealed,artifact.Id,null,DateTimeOffset.UtcNow);
        service.Catalog.Put("first-capture","capture",Make("first-capture")); service.Catalog.Put("second-capture","capture",Make("second-capture"));
        var measurement=await gate.EnterMeasurementAsync(); var first=service.StartTraceAnalysis("first-capture","first-job");
        using(var timeout=new CancellationTokenSource(TimeSpan.FromSeconds(10))) while(gate.State.Queued==0) await Task.Delay(10,timeout.Token);
        Assert.Equal(first.Id,service.StartTraceAnalysis("first-capture","first-job").Id);
        var response=await new LabTools(service).Run(new StartTraceAnalysis("second-capture","rejected-job")); Assert.Equal("resource_limit",response.Code);
        var rejected=service.Catalog.FindAccepted("local","rejected-job",Wire.Hash(new { captureId="second-capture",source_hash=artifact.Hash,TraceAnalyzer.Version,limits=service.Limits }));
        Assert.NotNull(rejected); Assert.Equal(OperationState.Failed,rejected.State); Assert.Equal("resource_limit",rejected.ErrorCode);
        Assert.Equal(service.Workspace.Policy.Id,service.Catalog.OperationPolicy(rejected.Id).Policy!.Id);
        measurement.Dispose(); Assert.Equal(OperationState.Completed,(await ServiceTests.Wait(service,first.Id)).State);
    }
    [Fact,Case("UT-WORKSPACE-006","FR-016,FR-038","M1","Unit")]
    public async Task QueueCapRejectsBeforeEnqueueAndCancellationRestoresCapacity()
    {
        var gate=new ParserMeasurementGate(maximumQueuedActivities:1); using var parser=await gate.EnterParserAsync(); using var cancel=new CancellationTokenSource();
        var first=gate.EnterMeasurementAsync(cancel.Token);
        Assert.Equal("resource_limit",(await Assert.ThrowsAsync<LabException>(()=>gate.EnterMeasurementAsync())).Code); Assert.Equal(1,gate.State.Queued);
        cancel.Cancel(); await Assert.ThrowsAnyAsync<OperationCanceledException>(()=>first); Assert.Equal(0,gate.State.Queued);
        var later=gate.EnterMeasurementAsync(); parser.Dispose(); using var measured=await later; Assert.Equal(0,gate.State.Queued);
    }
    [Theory,InlineData(0),InlineData(65)]
    [Case("UT-WORKSPACE-007","FR-016,FR-074","M1","Contract")]
    public void InvalidPolicyIsRejectedBeforeCreatingCatalog(int operations)
    {
        using var workspace=new TestWorkspace(); Assert.Equal("invalid_plan",Assert.Throws<LabException>(()=>new LabService(workspace.Root,new(MaximumConcurrentOperations:operations))).Code);
        Assert.False(File.Exists(Path.Combine(workspace.Root,"catalog.sqlite")));
    }
}

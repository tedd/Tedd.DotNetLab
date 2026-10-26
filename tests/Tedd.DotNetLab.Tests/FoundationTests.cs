using Tedd.DotNetLab.Artifacts;
using Tedd.DotNetLab.Contracts;
using Tedd.DotNetLab.Domain;
using Tedd.DotNetLab.Storage;
using Tedd.DotNetLab.Transport.Local;
using Xunit;

namespace Tedd.DotNetLab.Tests;

[Xunit.Sdk.TraitDiscoverer("Tedd.DotNetLab.Tests.CaseTraitDiscoverer","Tedd.DotNetLab.Tests")]
public sealed class CaseAttribute(string id, string requirement, string milestone, string level) : Attribute,Xunit.Sdk.ITraitAttribute
{
    public string Id { get; } = id;
    public string Requirement { get; } = requirement;
    public string Milestone { get; } = milestone;
    public string Level { get; } = level;
}
public sealed class TestWorkspace : IDisposable
{
    public static string Workload(string? runtimeVersion=null)
    {
        if(runtimeVersion is not null&&runtimeVersion.StartsWith("11.",StringComparison.Ordinal))
        {
            string net11=Path.Combine(TaskRoot,"bin","Tedd.DotNetLab.Net11Workload","Release","net11.0","Tedd.DotNetLab.Net11Workload.dll");
            if(File.Exists(net11)) return net11;
        }
        return Path.Combine(TaskRoot,"bin","LabWorkload","Release","net10.0","LabWorkload.dll");
    }
    public static System.Diagnostics.ProcessStartInfo PinRuntime(System.Diagnostics.ProcessStartInfo start)
    {
        if(Path.GetFileNameWithoutExtension(start.FileName)!="dotnet") return start;
        var arguments=start.ArgumentList.ToArray();
        if(arguments.FirstOrDefault()=="exec") return start;
        start.FileName=RuntimeSupport.ControllerStartInfo().FileName;
        start.ArgumentList.Clear(); RuntimeSupport.AddRuntimeArguments(start);
        foreach(string argument in arguments) start.ArgumentList.Add(argument);
        return start;
    }
    public static string TaskRoot
    {
        get
        {
            string defaultWindows=Directory.Exists(@"D:\Workspaces\AI")?@"D:\Workspaces\AI\dotnetlab-implementation":
                Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,"..","..","..",".."));
            string defaultLinux=Directory.Exists("/mnt/d/Workspaces/AI")?"/mnt/d/Workspaces/AI/dotnetlab-implementation/linux":
                Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,"..","..","..",".."));
            string root=Environment.GetEnvironmentVariable("DOTNETLAB_TEST_BUILD_ROOT")??(OperatingSystem.IsWindows()?defaultWindows:defaultLinux);
            string full=Path.GetFullPath(root);
            string? designated=OperatingSystem.IsWindows()&&Directory.Exists(@"D:\Workspaces\AI")?@"D:\Workspaces\AI\":
                OperatingSystem.IsLinux()&&Directory.Exists("/mnt/d/Workspaces/AI")?"/mnt/d/Workspaces/AI/":null;
            if(designated is not null&&!full.StartsWith(designated,StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Test output requires the designated AI workspace.");
            return full;
        }
    }
    public string Root { get; } = Path.Combine(TaskRoot, "tests", Guid.NewGuid().ToString("N"));
    public TestWorkspace() => Directory.CreateDirectory(Root);
    public void Dispose()
    {
        string full = Path.GetFullPath(Root);
        string parent=Path.Combine(TaskRoot,"tests");
        if (!full.StartsWith(parent+Path.DirectorySeparatorChar,OperatingSystem.IsWindows()?StringComparison.OrdinalIgnoreCase:StringComparison.Ordinal))
            throw new InvalidOperationException("Unsafe cleanup path.");
        OwnedDirectoryCleanup.DeleteAsync(parent,full).GetAwaiter().GetResult();
    }
}

public sealed class FoundationTests
{
    [Fact]
    [Case("CT-MCP-007", "FR-074", "M0", "Contract")]
    public void Wire_LargeTicks_PreserveExactValue()
    {
        long original = long.MaxValue - 11;
        Assert.Equal(original, Wire.Deserialize<long>(Wire.Serialize(original)));
        Assert.StartsWith("\"", Wire.Serialize(original));
    }
    [Fact]
    [Case("UT-ART-001", "FR-074", "M0", "Unit")]
    public void CanonicalHash_PropertyOrder_DoesNotChangeHash()
    {
        Assert.Equal(Wire.Hash(new { a = 1, b = 2 }), Wire.Hash(new { b = 2, a = 1 }));
        Assert.NotEqual(Wire.Hash(new { a = 1 }), Wire.Hash(new { a = "1" }));
    }
    [Fact]
    [Case("UT-OP-001", "FR-038", "M0", "Unit")]
    public void Lifecycle_TerminalState_RejectsRestart()
    {
        Assert.Throws<LabException>(() => Lifecycle.Transition(OperationState.Completed, OperationState.Running));
        Assert.Throws<LabException>(() => Lifecycle.Transition(CaptureState.Collecting, CaptureState.Sealed));
    }
    [Fact]
    [Case("UT-SEC-001", "FR-070", "M0", "Unit")]
    public void Authorization_ExpiredOrWrongScope_IsDenied()
    {
        var target = new TargetIdentity("t", 1, 2, "runtime", "10.0.12", "x64");
        var now = DateTimeOffset.UtcNow;
        var grant = new Grant("g", "w", "t", 2, Permission.Observe, now.AddMinutes(1), 1);
        Policies.Authorize(grant, "w", target, Permission.Observe, now);
        Assert.Throws<LabException>(() => Policies.Authorize(grant, "w", target, Permission.Observe, now.AddMinutes(2)));
        Assert.Throws<LabException>(() => Policies.Authorize(grant, "w", target with { StartTimeUtcTicks = 3 }, Permission.Observe, now));
        Assert.Throws<LabException>(() => Policies.Authorize(grant, "w", target, Permission.ExecuteRegisteredScenario, now));
    }
    [Fact]
    [Case("UT-CAP-001", "FR-010", "M0", "Unit")]
    public void Profile_Baseline_DoesNotImplyAllocationOrSampling()
    {
        var profile = Profiles.Resolve("baseline-context");
        Assert.Equal(4, profile.Providers[0].Level);
        Assert.DoesNotContain("allocation_samples", profile.DataClasses);
        Assert.DoesNotContain("samples", profile.DataClasses);
        Assert.Empty(Profiles.Resolve("quiet-validation").Providers);
        Assert.Contains("selected_probes",Profiles.Resolve("targeted-probes").DataClasses);
        Assert.Equal(profile.Hash, Profiles.Resolve("baseline-context").Hash);
    }
    [Fact]
    [Case("UT-EXP-002", "FR-035", "M0", "Unit")]
    public void Assignment_FixedSeed_ProducesSameCompleteBlocks()
    {
        var a = new DeterministicRandom(42);
        var b = new DeterministicRandom(42);
        for (int i = 0; i < 100; i++)
        {
            string[] values = ["00", "10", "01", "11"];
            var left = a.Shuffle(values);
            Assert.Equal(left, b.Shuffle(values));
            Assert.Equal(values.Order(), left.Order());
        }
    }
    [Fact]
    [Case("UT-OP-007", "FR-062", "M0", "Integration")]
    public void Catalog_IdempotencyAndRevision_SurviveRestart()
    {
        using var workspace = new TestWorkspace();
        string id;
        using (var catalog = new Catalog(workspace.Root))
        {
            id = catalog.Accept("test", "key", "hash", "capture", null).Id;
            Assert.Equal(id, catalog.Accept("test", "key", "hash", "capture", null).Id);
            Assert.Throws<LabException>(() => catalog.Accept("test", "key", "different", "capture", null));
            catalog.Put("hyp-1", "hypothesis", new { mechanism = "claim" });
            Assert.Throws<LabException>(() => catalog.Put("hyp-1", "hypothesis", new { mechanism = "changed" }));
            catalog.Put("hyp-1", "hypothesis", new { mechanism = "revised" }, 1);
        }
        using var reopened = new Catalog(workspace.Root);
        Assert.Equal(id, reopened.Accept("test", "key", "hash", "capture", null).Id);
        Assert.Equal(2, reopened.Get("hyp-1").Revision);
        Assert.Equal(1, reopened.Get("hyp-1", 1).Revision);
    }
    [Fact]
    [Case("UT-ID-007", "FR-004", "M0", "Integration")]
    public void Lease_ConcurrentOwnerAndTaint_PreventReuse()
    {
        using var workspace = new TestWorkspace();
        using var catalog = new Catalog(workspace.Root);
        var a = catalog.Accept("test", "a", "a", "execution", "t");
        var b = catalog.Accept("test", "b", "b", "execution", "t");
        catalog.AcquireLease("t", a.Id);
        Assert.Equal("lease_conflict", Assert.Throws<LabException>(() => catalog.AcquireLease("t", b.Id)).Code);
        catalog.ReleaseLease("t", a.Id, false);
        Assert.Equal("target_tainted", Assert.Throws<LabException>(() => catalog.AcquireLease("t", b.Id)).Code);
    }
    [Fact]
    [Case("UT-SEC-003", "FR-070", "M0", "Integration")]
    public void Grant_Budget_IsEnforcedDurably()
    {
        using var workspace = new TestWorkspace();
        using var catalog = new Catalog(workspace.Root);
        var grant = new Grant("g", workspace.Root, "t", 1, Permission.Observe, DateTimeOffset.UtcNow.AddHours(1), 1);
        catalog.UseGrant(grant);
        Assert.Equal("permission_denied", Assert.Throws<LabException>(() => catalog.UseGrant(grant)).Code);
    }
    [Fact]
    [Case("IT-STORE-002", "FR-072", "M0", "Integration")]
    public void Backup_ActiveWal_ContainsCommittedRecords()
    {
        using var workspace = new TestWorkspace();
        using var catalog = new Catalog(workspace.Root);
        catalog.Put("h", "hypothesis", new { evidence = "present" });
        string backupDir = Path.Combine(workspace.Root, "backup");
        Directory.CreateDirectory(backupDir);
        catalog.Backup(Path.Combine(backupDir, "catalog.sqlite"));
        using var backup = new Catalog(backupDir);
        Assert.Equal(catalog.Get("h").Hash, backup.Get("h").Hash);
    }
    [Fact]
    [Case("UT-ART-006", "FR-072", "M0", "Integration")]
    public async Task Artifact_DeduplicatesAndRejectsCorruption()
    {
        using var workspace = new TestWorkspace();
        var store = new ArtifactStore(workspace.Root);
        var a = await store.WriteJsonAsync("op-a", new { data = "original" }, "test");
        var b = await store.WriteJsonAsync("op-b", new { data = "original" }, "test");
        Assert.Equal(a.Id, b.Id);
        string path = await store.VerifiedPathAsync(a);
        await File.WriteAllTextAsync(path, "corrupt");
        Assert.Equal("artifact_corrupt", (await Assert.ThrowsAsync<LabException>(() => store.VerifyAsync(a.Hash, a.Length))).Code);
        Assert.Throws<LabException>(() => store.CreateIncoming("../escape"));
    }
    [Fact]
    [Case("CT-MCP-013", "FR-071", "M0", "Contract")]
    public async Task Framing_InvalidLength_RejectsBeforeAllocation()
    {
        using var stream = new MemoryStream([255, 255, 255, 127]);
        await Assert.ThrowsAsync<LabException>(() => Frames.ReadAsync<object>(stream, CancellationToken.None));
        using var valid = new MemoryStream();
        await Frames.WriteAsync(valid, new { schema_version = "1.0", ticks = long.MaxValue }, CancellationToken.None);
        valid.Position = 0;
        var roundTrip = await Frames.ReadAsync<System.Text.Json.JsonElement>(valid, CancellationToken.None);
        Assert.Equal(long.MaxValue.ToString(), roundTrip.GetProperty("ticks").GetString());
    }
}

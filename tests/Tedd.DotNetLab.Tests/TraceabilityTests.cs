using System.Reflection;
using Xunit;

namespace Tedd.DotNetLab.Tests;

public sealed class TraceabilityTests
{
    [Fact,Case("CT-TRACE-001","FR-074","M7","Contract")]
    public void EveryExecutableTestDeclaresCaseRequirementMilestoneAndLevel()
    {
        var missing=new List<string>();
        foreach(var type in typeof(TraceabilityTests).Assembly.GetTypes())
        foreach(var method in type.GetMethods(BindingFlags.Public|BindingFlags.Instance|BindingFlags.Static|BindingFlags.DeclaredOnly))
        {
            if(method.GetCustomAttribute<FactAttribute>() is null) continue;
            var tag=method.GetCustomAttribute<CaseAttribute>();
            if(tag is null) { missing.Add(type.FullName+"."+method.Name); continue; }
            var values=CaseTraitDiscoverer.Tags(tag.Id,tag.Requirement,tag.Milestone,tag.Level).ToArray();
            Assert.Contains(values,v=>v.Key=="Case"&&!string.IsNullOrWhiteSpace(v.Value));
            Assert.All(values.Where(v=>v.Key=="Requirement"),v=>Assert.Matches(@"\AFR-[0-9]{3}\z",v.Value));
            Assert.Contains(values,v=>v.Key=="Requirement"); Assert.Matches(@"\AM[0-7]\z",tag.Milestone);
            Assert.Contains(tag.Level,new[]{"Unit","Property","Contract","Integration","Reference","Performance"});
        }
        Assert.True(missing.Count==0,"Tests without requirement metadata: "+string.Join(", ",missing));
    }
    [Fact,Case("CT-TRACE-002","FR-074","M7","Contract")]
    public void CommaSeparatedTagsAreIndividualTraitsRatherThanOneCombinedCategory()
    {
        var values=CaseTraitDiscoverer.Tags("UT-ONE, IT-TWO,UT-ONE","FR-001,FR-074","M7","Integration").ToArray();
        Assert.Equal(new[]{"UT-ONE","IT-TWO"},values.Where(v=>v.Key=="Case").Select(v=>v.Value));
        Assert.Equal(new[]{"FR-001","FR-074"},values.Where(v=>v.Key=="Requirement").Select(v=>v.Value));
    }
    [Fact,Case("CT-TRACE-003","FR-074","M7","Contract")]
    public void DeclarationCatalogBindsDistinctMethodIdentitiesToTheActualAssembly()
    {
        var catalog=TestEvidenceCatalog.Describe(); Assert.Equal("test-declarations/v1",catalog.SchemaVersion);
        Assert.Matches(@"\A[0-9a-f]{64}\z",catalog.AssemblyHash); Assert.NotEmpty(catalog.Tests);
        Assert.Equal(catalog.Tests.Count,catalog.Tests.Select(t=>t.Method).Distinct().Count());
        Assert.All(catalog.Tests,test=>{ Assert.NotEmpty(test.Cases); Assert.NotEmpty(test.Requirements); });
    }
}

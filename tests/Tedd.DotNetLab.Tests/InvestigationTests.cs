using Tedd.DotNetLab.Application;
using Tedd.DotNetLab.Contracts;
using Tedd.DotNetLab.Mcp;
using Xunit;

namespace Tedd.DotNetLab.Tests;

public sealed class InvestigationTests
{
    private const string Pr="https://example.org/repository/pull/42";
    private static Investigation Claim(string area="parser",string approach="reuse-buffer") =>
        new(area,approach,"in_progress","Measure allocations on the large input.",[],null);

    [Fact]
    public async Task ReopeningWorkspacePreservesNotesEvidenceAndPriorRevisions()
    {
        using var workspace=new TestWorkspace();
        string path=Path.Combine(workspace.Root,".DotNetLab");
        NotebookRecord original,completed;
        string evidenceId;
        string notes="## Outcome\nNo improvement on .NET 10. Revisit for larger inputs.";
        await using(var lab=new LabService(path))
        {
            evidenceId=lab.Hypothesis("Reuse buffers","Fewer allocations",[]).Id;
            original=lab.RecordInvestigation(Claim());
            completed=lab.RecordInvestigation(Claim() with { Status="dead_end",Notes=notes,EvidenceIds=[evidenceId] },original.Revision);
            lab.RecordInvestigation(Claim(approach:"vectorize") with { Status="pr_open",PullRequestUrl=Pr });
        }
        await using(var reopened=new LabService(path))
        {
            Assert.Equal(completed,reopened.GetInvestigation("parser","reuse-buffer"));
            Assert.Equal(original,reopened.Catalog.Get(original.Id,1));
            Assert.Equal(2,completed.Revision);
            Assert.NotEqual(original.Hash,completed.Hash);
            var entry=Wire.Deserialize<Investigation>(completed.Json);
            Assert.Equal("dead_end",entry.Status);
            Assert.Equal(notes,entry.Notes);
            Assert.Equal(evidenceId,Assert.Single(entry.EvidenceIds));
            Assert.Equal(Pr,Wire.Deserialize<Investigation>(reopened.GetInvestigation("parser","vectorize").Json).PullRequestUrl);
        }
        await using var other=new LabService(Path.Combine(workspace.Root,"other",".DotNetLab"));
        Assert.Empty(other.Investigations(null,20,null).Rows);
    }

    [Fact]
    public async Task ConcurrentClaimsHaveExactlyOneWinnerAndUpdatesRequireCurrentRevision()
    {
        using var workspace=new TestWorkspace();
        await using var lab=new LabService(workspace.Root);
        var start=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var claims=Enumerable.Range(0,12).Select(async worker=>
        {
            await start.Task;
            return Record.Exception(()=>lab.RecordInvestigation(Claim() with { Notes=$"Worker {worker}" }));
        }).ToArray();
        start.SetResult();
        var results=await Task.WhenAll(claims);
        Assert.Single(results,error=>error is null);
        Assert.All(results.OfType<Exception>(),error=>Assert.Equal("revision_conflict",Assert.IsType<LabException>(error).Code));
        var first=lab.GetInvestigation("parser","reuse-buffer");
        var revised=lab.RecordInvestigation(Claim() with { Status="pr_open",PullRequestUrl=Pr },first.Revision);
        Assert.Equal("revision_conflict",Assert.Throws<LabException>(()=>lab.RecordInvestigation(Claim(),first.Revision)).Code);
        Assert.Equal("revision_conflict",Assert.Throws<LabException>(()=>lab.RecordInvestigation(Claim())).Code);
        Assert.Equal(revised,lab.GetInvestigation("parser","reuse-buffer"));
    }

    [Fact]
    public async Task AreaPaginationReturnsLatestRevisionsAndRejectsForeignCursors()
    {
        using var workspace=new TestWorkspace();
        await using var lab=new LabService(workspace.Root);
        var a=lab.RecordInvestigation(Claim(approach:"a"));
        var b=lab.RecordInvestigation(Claim(approach:"b"));
        var latest=lab.RecordInvestigation(Claim(approach:"a") with { Status="abandoned" },a.Revision);
        lab.RecordInvestigation(Claim("parser-cache","a"));
        lab.RecordInvestigation(Claim("runtime","a"));
        Assert.Empty(lab.Investigations("missing",20,null).Rows);
        Assert.Equal(4,lab.Investigations(null,20,null).Rows.Count);
        var first=lab.Investigations("parser",1,null);
        Assert.Equal(latest,Assert.Single(first.Rows));
        Assert.True(first.Truncated);
        Assert.NotNull(first.NextCursor);
        var last=lab.Investigations("parser",1,first.NextCursor);
        Assert.Equal(b,Assert.Single(last.Rows));
        Assert.False(last.Truncated);
        Assert.Null(last.NextCursor);
        foreach(string? area in new string?[] { null,"parser-cache","runtime" })
            Assert.Equal("invalid_cursor",Assert.Throws<LabException>(()=>lab.Investigations(area,1,first.NextCursor)).Code);
        Assert.Equal("invalid_cursor",Assert.Throws<LabException>(()=>lab.Investigations("parser",1,"parser/unknown")).Code);
        Assert.Equal("resource_limit",Assert.Throws<LabException>(()=>lab.Investigations("parser",0,null)).Code);
    }

    [Theory]
    [InlineData("in_progress",null)]
    [InlineData("dead_end",null)]
    [InlineData("inconclusive",null)]
    [InlineData("supported",null)]
    [InlineData("abandoned",null)]
    [InlineData("pr_open",Pr)]
    [InlineData("pr_closed",Pr)]
    [InlineData("pr_merged",Pr)]
    public async Task ValidStatesRoundTrip(string status,string? url)
    {
        using var workspace=new TestWorkspace();
        await using var lab=new LabService(workspace.Root);
        var investigation=Claim() with { Status=status,PullRequestUrl=url };
        var record=lab.RecordInvestigation(investigation);
        var read=Wire.Deserialize<Investigation>(record.Json);
        Assert.Equal(status,read.Status);
        Assert.Equal(url,read.PullRequestUrl);
    }

    [Theory]
    [InlineData("", "a","in_progress",null)]
    [InlineData("a/b","c","in_progress",null)]
    [InlineData("a","b:c","in_progress",null)]
    [InlineData("Parser","b","in_progress",null)]
    [InlineData("a","b\n","in_progress",null)]
    [InlineData("a","-b","in_progress",null)]
    [InlineData("a","b-","in_progress",null)]
    [InlineData("a","b","unknown",null)]
    [InlineData("a","b","pr_open",null)]
    [InlineData("a","b","pr_merged",null)]
    [InlineData("a","b","pr_closed",null)]
    [InlineData("a","b","pr_open","http://example.org/pull/42")]
    [InlineData("a","b","pr_open","/pull/42")]
    [InlineData("a","b","pr_open","https://user:secret@example.org/pull/42")]
    [InlineData("a","b","pr_open","https://example.org/pull/42 ")]
    public async Task MalformedClaimsLeaveNoRecord(string area,string approach,string status,string? url)
    {
        using var workspace=new TestWorkspace();
        await using var lab=new LabService(workspace.Root);
        var claim=Claim(area,approach) with { Status=status,PullRequestUrl=url };
        Assert.Equal("invalid_request",Assert.Throws<LabException>(()=>lab.RecordInvestigation(claim)).Code);
        Assert.Empty(lab.Investigations(null,20,null).Rows);
    }

    [Fact]
    public async Task ValidatesBoundsAndEvidenceBeforeWriting()
    {
        using var workspace=new TestWorkspace();
        await using var lab=new LabService(workspace.Root);
        var claim=Claim();
        foreach(var invalid in new[]
        {
            claim with { Area=new string('a',97) },claim with { Notes=" " },
            claim with { Notes=new string('x',8193) },claim with { EvidenceIds=["a","a"] },
            claim with { EvidenceIds=[""] },claim with { EvidenceIds=Enumerable.Repeat("x",33).ToArray() }
        })
            Assert.Equal("invalid_request",Assert.Throws<LabException>(()=>lab.RecordInvestigation(invalid)).Code);
        Assert.Equal("invalid_request",Assert.Throws<LabException>(()=>lab.RecordInvestigation(claim,0)).Code);
        Assert.Equal("record_not_found",Assert.Throws<LabException>(()=>lab.RecordInvestigation(claim with { EvidenceIds=["missing"] })).Code);
        Assert.Empty(lab.Investigations(null,20,null).Rows);
    }

    [Fact]
    public async Task WireActionsSupportClaimLookupUpdateListAndMissingRecords()
    {
        using var workspace=new TestWorkspace();
        await using var lab=new LabService(workspace.Root);
        var tools=new LabTools(lab);
        const string lookup="""
            {"view":"investigations","area":"parser","approach":"reuse-buffer"}
            """;
        Assert.Equal("record_not_found",(await tools.InvokeAsync("lab.query",lookup)).Code);
        var claimed=await tools.InvokeAsync("lab.experiment","""
            {"action":"record_investigation","area":"parser","approach":"reuse-buffer","status":"in_progress","notes":"Inspect allocations."}
            """);
        Assert.Equal("ok",claimed.Status);
        Assert.Equal("1",claimed.Data!.Value.GetProperty("revision").GetString());
        var updated=await tools.InvokeAsync("lab.experiment","""
            {"action":"record_investigation","area":"parser","approach":"reuse-buffer","status":"pr_merged",
             "notes":"Covered by the merged change.","pull_request_url":"https://example.org/repository/pull/42","expected_revision":"1"}
            """);
        Assert.Equal("ok",updated.Status);
        var found=await tools.InvokeAsync("lab.query",lookup);
        Assert.Equal(updated.Data!.Value.GetRawText(),found.Data!.Value.GetRawText());
        var list=await tools.InvokeAsync("lab.query","""{"view":"investigations","area":"parser","limit":1}""");
        var row=Assert.Single(list.Data!.Value.GetProperty("rows").EnumerateArray());
        Assert.Equal("2",row.GetProperty("revision").GetString());
        var content=Wire.Deserialize<Investigation>(row.GetProperty("json").GetString()!);
        Assert.Equal("pr_merged",content.Status);
        Assert.Equal(Pr,content.PullRequestUrl);
        Assert.Equal("invalid_request",(await tools.InvokeAsync("lab.query","""{"view":"investigations","approach":"reuse-buffer"}""")).Code);
    }
}

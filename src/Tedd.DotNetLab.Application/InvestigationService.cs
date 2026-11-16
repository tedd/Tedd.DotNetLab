using System.Text.RegularExpressions;
using Tedd.DotNetLab.Contracts;

namespace Tedd.DotNetLab.Application;

public sealed record Investigation(string Area,string Approach,string Status,string Notes,
    IReadOnlyList<string> EvidenceIds,string? PullRequestUrl,IReadOnlyList<string>? TargetKeys=null);

public sealed partial class LabService
{
    private const string InvestigationKind="investigation";

    public NotebookRecord RecordInvestigation(Investigation investigation,long? expectedRevision=null)
    {
        string id=InvestigationPrefix(investigation.Area)+InvestigationKey(investigation.Approach);
        bool pullRequestStatus=investigation.Status is "pr_open" or "pr_merged" or "pr_closed";
        if (investigation.Status is not ("in_progress" or "dead_end" or "inconclusive" or "supported" or
            "abandoned" or "pr_open" or "pr_merged" or "pr_closed") ||
            string.IsNullOrWhiteSpace(investigation.Notes) || investigation.Notes.Length>8192 ||
            investigation.EvidenceIds is null || investigation.EvidenceIds.Count>32 ||
            investigation.EvidenceIds.Any(id=>string.IsNullOrWhiteSpace(id) || id.Length>256) ||
            investigation.EvidenceIds.Distinct(StringComparer.Ordinal).Count()!=investigation.EvidenceIds.Count ||
            expectedRevision is <=0)
            throw new LabException("invalid_request","Invalid investigation status, notes, evidence or expected revision.");

        string? url=investigation.PullRequestUrl;
        if (pullRequestStatus && url is null || url is not null &&
            (url.Length>2048 || !Uri.TryCreate(url,UriKind.Absolute,out var uri) ||
             uri.Scheme!=Uri.UriSchemeHttps || uri.Host.Length==0 || uri.UserInfo.Length!=0 ||
             url.Any(char.IsWhiteSpace)))
            throw new LabException("invalid_request","Pull request statuses require an absolute HTTPS pull request URL without credentials.");

        if (investigation.TargetKeys is not null || expectedRevision is not null)
        {
            IReadOnlyList<string> previousKeys=[];
            if (expectedRevision is not null)
            {
                var previous=Catalog.Get(id);
                if (previous.Revision!=expectedRevision)
                    throw new LabException("revision_conflict","Expected investigation revision is stale.");
                previousKeys=Wire.Deserialize<Investigation>(previous.Json).TargetKeys??[];
            }
            var supplied=investigation.TargetKeys is null?[]:NormalizeTargetKeys(investigation.TargetKeys);
            var merged=NormalizeTargetKeys(previousKeys.Concat(supplied)
                .Distinct(StringComparer.Ordinal).ToArray(),allowEmpty:true);
            investigation=investigation with { TargetKeys=merged.Length==0?null:merged };
        }
        foreach (string evidenceId in investigation.EvidenceIds) Catalog.Get(evidenceId);
        return Catalog.Put(id,InvestigationKind,investigation,expectedRevision);
    }

    public Page<NotebookRecord> Investigations(string? area,int limit,string? cursor,IReadOnlyList<string>? targetKeys=null)
    {
        if (area is not null && targetKeys is not null)
            throw new LabException("invalid_request","Choose either an area or a set of exact target keys.");
        string[]? keys=targetKeys is null?null:NormalizeTargetKeys(targetKeys);
        string? prefix=area is null?null:InvestigationPrefix(area);
        string cursorScope=(keys is null?area??"*":"keys:"+Wire.Hash(keys))+"/";
        if (cursor is not null && !cursor.StartsWith(cursorScope,StringComparison.Ordinal))
            throw new LabException("invalid_cursor","Cursor belongs to different investigation filters.");
        var page=Catalog.List(InvestigationKind,limit,cursor?[cursorScope.Length..],includeOversizedFirst:true,prefix:prefix,
            investigationTargetKeys:keys);
        return page.NextCursor is null?page:page with { NextCursor=cursorScope+page.NextCursor };
    }

    public NotebookRecord GetInvestigation(string area,string approach) =>
        Catalog.Get(InvestigationPrefix(area)+InvestigationKey(approach));

    private static string InvestigationPrefix(string area) => $"investigation:{InvestigationKey(area)}:";

    private static string[] NormalizeTargetKeys(IReadOnlyList<string> keys,bool allowEmpty=false)
    {
        if (keys.Count>32 || !allowEmpty && keys.Count==0 || keys.Any(key=>
            string.IsNullOrWhiteSpace(key) || key.Length>512 || key.Any(char.IsControl)))
            throw new LabException("invalid_request","Supply 1–32 target keys of up to 512 characters each.");
        return keys.Select(key=>key.Trim().Replace('\\','/'))
            .Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
    }

    private static string InvestigationKey(string value)
    {
        if (value is null || value.Length is <1 or >96 || !InvestigationKeyPattern().IsMatch(value))
            throw new LabException("invalid_request","Area and approach keys must contain 1–96 lowercase ASCII letters, digits, or hyphens, with no leading or trailing hyphen.");
        return value;
    }

    [GeneratedRegex(@"\A[a-z0-9](?:[a-z0-9-]*[a-z0-9])?\z",RegexOptions.CultureInvariant)]
    private static partial Regex InvestigationKeyPattern();
}

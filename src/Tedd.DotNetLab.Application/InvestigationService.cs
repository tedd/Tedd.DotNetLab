using System.Text.RegularExpressions;
using Tedd.DotNetLab.Contracts;

namespace Tedd.DotNetLab.Application;

public sealed record Investigation(string Area,string Approach,string Status,string Notes,
    IReadOnlyList<string> EvidenceIds,string? PullRequestUrl);

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

        foreach (string evidenceId in investigation.EvidenceIds) Catalog.Get(evidenceId);
        return Catalog.Put(id,InvestigationKind,investigation,expectedRevision);
    }

    public Page<NotebookRecord> Investigations(string? area,int limit,string? cursor)
    {
        string? prefix=area is null?null:InvestigationPrefix(area);
        string cursorScope=(area??"*")+"/";
        if (cursor is not null && !cursor.StartsWith(cursorScope,StringComparison.Ordinal))
            throw new LabException("invalid_cursor","Cursor belongs to another investigation area or view.");
        var page=Catalog.List(InvestigationKind,limit,cursor?[cursorScope.Length..],includeOversizedFirst:true,prefix:prefix);
        return page.NextCursor is null?page:page with { NextCursor=cursorScope+page.NextCursor };
    }

    public NotebookRecord GetInvestigation(string area,string approach) =>
        Catalog.Get(InvestigationPrefix(area)+InvestigationKey(approach));

    private static string InvestigationPrefix(string area) => $"investigation:{InvestigationKey(area)}:";

    private static string InvestigationKey(string value)
    {
        if (value is null || value.Length is <1 or >96 || !InvestigationKeyPattern().IsMatch(value))
            throw new LabException("invalid_request","Area and approach keys must contain 1–96 lowercase ASCII letters, digits, or hyphens, with no leading or trailing hyphen.");
        return value;
    }

    [GeneratedRegex(@"\A[a-z0-9](?:[a-z0-9-]*[a-z0-9])?\z",RegexOptions.CultureInvariant)]
    private static partial Regex InvestigationKeyPattern();
}

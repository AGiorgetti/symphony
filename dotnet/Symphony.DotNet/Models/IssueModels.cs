namespace Symphony.DotNet.Models;

internal sealed record IssueBlocker(string Id, string Identifier, string State);

internal sealed record IssueRecord(
    string Id,
    string Identifier,
    string Title,
    string State,
    int? Priority,
    DateTimeOffset? CreatedAt,
    DateTimeOffset? UpdatedAt,
    string? Description,
    string? BranchName,
    string? Url,
    IReadOnlyList<string> Labels,
    IReadOnlyList<IssueBlocker> BlockedBy,
    bool AssignedToWorker = true);

internal sealed record IssueContext(string? IssueId, string IssueIdentifier)
{
    public static IssueContext FromIdentifier(string? issueIdentifier) =>
        new(null, string.IsNullOrWhiteSpace(issueIdentifier) ? "issue" : issueIdentifier);

    public static IssueContext FromIssue(IssueRecord issue) => new(issue.Id, issue.Identifier);
}

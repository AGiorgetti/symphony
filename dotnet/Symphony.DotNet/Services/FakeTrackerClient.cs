using Symphony.DotNet.Models;

namespace Symphony.DotNet.Services;

internal sealed class FakeTrackerClient(IReadOnlyList<IssueRecord> issues) : ITrackerClient
{
    public Task<IReadOnlyList<IssueRecord>> FetchCandidateIssuesAsync(CancellationToken cancellationToken) => Task.FromResult(issues);

    public Task<IReadOnlyList<IssueRecord>> FetchIssuesByStatesAsync(IReadOnlyList<string> stateNames, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<IssueRecord>>(issues.Where(issue => stateNames.Contains(issue.State, StringComparer.OrdinalIgnoreCase)).ToList());

    public Task<IReadOnlyList<IssueRecord>> FetchIssueStatesByIdsAsync(IReadOnlyList<string> issueIds, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<IssueRecord>>(issues.Where(issue => issueIds.Contains(issue.Id, StringComparer.OrdinalIgnoreCase)).ToList());
}

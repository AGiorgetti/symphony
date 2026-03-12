using Symphony.DotNet.Models;

namespace Symphony.DotNet.Services;

internal interface ITrackerClient
{
    Task<IReadOnlyList<IssueRecord>> FetchCandidateIssuesAsync(CancellationToken cancellationToken);
    Task<IReadOnlyList<IssueRecord>> FetchIssuesByStatesAsync(IReadOnlyList<string> stateNames, CancellationToken cancellationToken);
    Task<IReadOnlyList<IssueRecord>> FetchIssueStatesByIdsAsync(IReadOnlyList<string> issueIds, CancellationToken cancellationToken);
}

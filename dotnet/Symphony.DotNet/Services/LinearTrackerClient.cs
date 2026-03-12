using System.Net.Http.Headers;
using System.Text.Json;
using Symphony.DotNet.Models;

namespace Symphony.DotNet.Services;

internal sealed class LinearTrackerClient : ITrackerClient
{
    private const int PageSize = 50;

    private readonly WorkflowDocument _workflow;
    private readonly Func<string, object, CancellationToken, Task<JsonDocument>> _executeGraphQlAsync;

    private const string PollQuery = """
query SymphonyLinearPoll($projectSlug: String!, $stateNames: [String!]!, $first: Int!, $relationFirst: Int!, $after: String) {
  issues(filter: {project: {slugId: {eq: $projectSlug}}, state: {name: {in: $stateNames}}}, first: $first, after: $after) {
    nodes {
      id
      identifier
      title
      description
      priority
      state { name }
      branchName
      url
      assignee { id }
      labels { nodes { name } }
      inverseRelations(first: $relationFirst) {
        nodes {
          type
          issue {
            id
            identifier
            state { name }
          }
        }
      }
      createdAt
      updatedAt
    }
    pageInfo {
      hasNextPage
      endCursor
    }
  }
}
""";

    private const string QueryByIds = """
query SymphonyLinearIssuesById($ids: [ID!]!, $first: Int!, $relationFirst: Int!) {
  issues(filter: {id: {in: $ids}}, first: $first) {
    nodes {
      id
      identifier
      title
      description
      priority
      state { name }
      branchName
      url
      assignee { id }
      labels { nodes { name } }
      inverseRelations(first: $relationFirst) {
        nodes {
          type
          issue {
            id
            identifier
            state { name }
          }
        }
      }
      createdAt
      updatedAt
    }
  }
}
""";

    public LinearTrackerClient(WorkflowDocument workflow, HttpClient httpClient)
        : this(workflow, (query, variables, cancellationToken) => ExecuteHttpGraphQlAsync(httpClient, workflow, query, variables, cancellationToken))
    {
    }

    internal LinearTrackerClient(WorkflowDocument workflow, Func<string, object, CancellationToken, Task<JsonDocument>> executeGraphQlAsync)
    {
        _workflow = workflow;
        _executeGraphQlAsync = executeGraphQlAsync;
    }

    public Task<IReadOnlyList<IssueRecord>> FetchCandidateIssuesAsync(CancellationToken cancellationToken) =>
        FetchByStatesAsync(_workflow.Tracker.ActiveStates, cancellationToken);

    public Task<IReadOnlyList<IssueRecord>> FetchIssuesByStatesAsync(IReadOnlyList<string> stateNames, CancellationToken cancellationToken) =>
        FetchByStatesAsync(stateNames, cancellationToken);

    public async Task<IReadOnlyList<IssueRecord>> FetchIssueStatesByIdsAsync(IReadOnlyList<string> issueIds, CancellationToken cancellationToken)
    {
        if (issueIds.Count == 0)
        {
            return [];
        }

        var results = new List<IssueRecord>();
        foreach (var chunk in issueIds.Distinct(StringComparer.OrdinalIgnoreCase).Chunk(PageSize))
        {
            using var json = await _executeGraphQlAsync(QueryByIds, new { ids = chunk, first = chunk.Length, relationFirst = PageSize }, cancellationToken);
            var nodes = DecodeIssuesNode(json.RootElement);
            results.AddRange(nodes.EnumerateArray().Select(NormalizeIssue).Where(static issue => issue is not null)!);
        }

        return results;
    }

    internal async Task<IReadOnlyList<IssueRecord>> FetchByStatesAsync(IReadOnlyList<string> stateNames, CancellationToken cancellationToken)
    {
        if (stateNames.Count == 0)
        {
            return [];
        }

        var results = new List<IssueRecord>();
        string? after = null;

        while (true)
        {
            using var json = await _executeGraphQlAsync(PollQuery, new { projectSlug = _workflow.Tracker.ProjectSlug, stateNames, first = PageSize, relationFirst = PageSize, after }, cancellationToken);
            var issuesNode = DecodeIssuesConnection(json.RootElement);
            var nodes = issuesNode.GetProperty("nodes");
            results.AddRange(nodes.EnumerateArray().Select(NormalizeIssue).Where(static issue => issue is not null)!);

            if (!issuesNode.TryGetProperty("pageInfo", out var pageInfo) || pageInfo.ValueKind != JsonValueKind.Object)
            {
                throw new InvalidOperationException("linear_payload_shape");
            }

            if (!pageInfo.GetProperty("hasNextPage").GetBoolean())
            {
                break;
            }

            after = pageInfo.GetProperty("endCursor").GetString() ?? throw new InvalidOperationException("linear_missing_end_cursor");
        }

        return results;
    }

    internal static IssueRecord? NormalizeIssue(JsonElement issue)
    {
        if (!issue.TryGetProperty("id", out var idProp) || !issue.TryGetProperty("identifier", out var identifierProp) || !issue.TryGetProperty("title", out var titleProp))
        {
            return null;
        }

        var blockedBy = new List<IssueBlocker>();
        if (issue.TryGetProperty("inverseRelations", out var inverseRelations) && inverseRelations.TryGetProperty("nodes", out var relationNodes))
        {
            foreach (var relation in relationNodes.EnumerateArray())
            {
                if (!relation.TryGetProperty("type", out var typeProp) || !string.Equals(typeProp.GetString(), "blocks", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (!relation.TryGetProperty("issue", out var blockerIssue))
                {
                    continue;
                }

                blockedBy.Add(new IssueBlocker(
                    blockerIssue.GetProperty("id").GetString() ?? string.Empty,
                    blockerIssue.GetProperty("identifier").GetString() ?? string.Empty,
                    blockerIssue.GetProperty("state").GetProperty("name").GetString() ?? string.Empty));
            }
        }

        var labels = new List<string>();
        if (issue.TryGetProperty("labels", out var labelsNode) && labelsNode.TryGetProperty("nodes", out var labelNodes))
        {
            foreach (var labelNode in labelNodes.EnumerateArray())
            {
                if (labelNode.TryGetProperty("name", out var labelName) && !string.IsNullOrWhiteSpace(labelName.GetString()))
                {
                    labels.Add(labelName.GetString()!.ToLowerInvariant());
                }
            }
        }

        return new IssueRecord(
            idProp.GetString() ?? string.Empty,
            identifierProp.GetString() ?? string.Empty,
            titleProp.GetString() ?? string.Empty,
            issue.GetProperty("state").GetProperty("name").GetString() ?? string.Empty,
            issue.TryGetProperty("priority", out var priorityProp) && priorityProp.TryGetInt32(out var priority) ? priority : null,
            ParseDate(issue, "createdAt"),
            ParseDate(issue, "updatedAt"),
            issue.TryGetProperty("description", out var descriptionProp) ? descriptionProp.GetString() : null,
            issue.TryGetProperty("branchName", out var branchProp) ? branchProp.GetString() : null,
            issue.TryGetProperty("url", out var urlProp) ? urlProp.GetString() : null,
            labels,
            blockedBy,
            true);
    }

    private static JsonElement DecodeIssuesConnection(JsonElement root)
    {
        if (root.TryGetProperty("errors", out var errors) && errors.ValueKind == JsonValueKind.Array && errors.GetArrayLength() > 0)
        {
            throw new InvalidOperationException("linear_graphql_errors");
        }

        if (!root.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Object ||
            !data.TryGetProperty("issues", out var issues) || issues.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidOperationException("linear_payload_shape");
        }

        return issues;
    }

    private static JsonElement DecodeIssuesNode(JsonElement root)
    {
        var issues = DecodeIssuesConnection(root);
        if (!issues.TryGetProperty("nodes", out var nodes) || nodes.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidOperationException("linear_payload_shape");
        }

        return nodes;
    }

    private static DateTimeOffset? ParseDate(JsonElement issue, string propertyName)
    {
        if (!issue.TryGetProperty(propertyName, out var prop))
        {
            return null;
        }

        return DateTimeOffset.TryParse(prop.GetString(), out var parsed) ? parsed : null;
    }

    internal static async Task<JsonDocument> ExecuteHttpGraphQlAsync(HttpClient httpClient, WorkflowDocument workflow, string query, object variables, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(workflow.Tracker.ApiKey))
        {
            throw new InvalidOperationException("missing_linear_api_token");
        }

        using var request = new HttpRequestMessage(HttpMethod.Post, workflow.Tracker.Endpoint)
        {
            Content = JsonContent.Create(new { query, variables })
        };
        request.Headers.TryAddWithoutValidation("Authorization", workflow.Tracker.ApiKey);

        HttpResponseMessage response;
        try
        {
            response = await httpClient.SendAsync(request, cancellationToken);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException($"linear_api_request:{ex.Message}", ex);
        }

        using (response)
        {
            if (!response.IsSuccessStatusCode)
            {
                throw new InvalidOperationException($"linear_api_status:{(int)response.StatusCode}");
            }

            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            try
            {
                return JsonDocument.Parse(body);
            }
            catch (JsonException ex)
            {
                throw new InvalidOperationException($"linear_api_payload:{ex.Message}", ex);
            }
        }
    }
}


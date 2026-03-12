using Symphony.DotNet.Models;

namespace Symphony.DotNet.Services;

internal sealed class TrackerClientFactory(IHttpClientFactory httpClientFactory)
{
    public ITrackerClient Create(WorkflowDocument workflow)
    {
        return workflow.Tracker.Kind.ToLowerInvariant() switch
        {
            "linear" => new LinearTrackerClient(workflow, httpClientFactory.CreateClient(nameof(LinearTrackerClient))),
            _ => throw new InvalidOperationException($"unsupported_tracker_kind: {workflow.Tracker.Kind}")
        };
    }
}

using Microsoft.Extensions.Hosting;
using Symphony.DotNet.Models;

namespace Symphony.DotNet.Services;

internal sealed class SymphonyRuntimeService(RuntimeStateStore store, CliOptions options, ILogger<SymphonyRuntimeService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("Starting Symphony .NET runtime with workflow {WorkflowPath}", options.WorkflowPath);

        while (!stoppingToken.IsCancellationRequested)
        {
            var workflowExists = File.Exists(options.WorkflowPath);
            var running = workflowExists
                ? new[]
                {
                    new RunningIssueState(
                        IssueId: "demo-1",
                        IssueIdentifier: "DEMO-1",
                        State: "Idle",
                        SessionId: null,
                        TurnCount: 0,
                        LastEvent: "workflow_loaded",
                        LastMessage: Path.GetFileName(options.WorkflowPath),
                        StartedAt: DateTimeOffset.UtcNow,
                        LastEventAt: DateTimeOffset.UtcNow,
                        Tokens: new TokensPayload(0, 0, 0))
                }
                : Array.Empty<RunningIssueState>();

            var retrying = workflowExists
                ? Array.Empty<RetryIssueState>()
                : new[]
                {
                    new RetryIssueState(
                        IssueId: "workflow",
                        IssueIdentifier: "WORKFLOW-MISSING",
                        Attempt: 1,
                        DueAt: DateTimeOffset.UtcNow.AddSeconds(30),
                        Error: $"Workflow file not found: {options.WorkflowPath}")
                };

            store.Update(new RuntimeState(
                running,
                retrying,
                new CodexTotalsPayload(0, 0, 0, 0),
                workflowExists ? new { status = "ok" } : new { status = "degraded" },
                options.WorkflowPath));

            await Task.Delay(TimeSpan.FromSeconds(1), stoppingToken);
        }
    }
}

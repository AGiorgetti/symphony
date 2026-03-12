using Microsoft.Extensions.Logging;
using Symphony.DotNet.Models;
using Symphony.DotNet.Services;

var options = CliOptions.Parse(args);
if (!options.GuardrailsAcknowledged)
{
    Console.Error.WriteLine(CliOptions.AcknowledgementBanner());
    return;
}

var builder = WebApplication.CreateBuilder(args);
if (options.Port is not null)
{
    builder.WebHost.UseUrls($"http://127.0.0.1:{options.Port.Value}");
}

builder.Logging.ClearProviders();
builder.Logging.AddSimpleConsole(logging =>
{
    logging.SingleLine = false;
    logging.TimestampFormat = "yyyy-MM-ddTHH:mm:ss.fffZ ";
});

builder.Services.AddHttpClient();
builder.Services.AddSingleton(options);
builder.Services.AddSingleton<RuntimeStateStore>();
builder.Services.AddSingleton<WorkflowStore>();
builder.Services.AddSingleton<TrackerClientFactory>();
builder.Services.AddSingleton<WorkspaceManager>();
builder.Services.AddSingleton<PromptRenderer>();
builder.Services.AddSingleton<CodexAppServerClient>();
builder.Services.AddSingleton<IAgentRunner, CodexAgentRunner>();
builder.Services.AddSingleton<SymphonyRuntimeService>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<SymphonyRuntimeService>());

var app = builder.Build();

app.Use(async (context, next) =>
{
    var path = context.Request.Path.Value ?? string.Empty;
    var method = context.Request.Method;

    var isIssueRoute = path.StartsWith("/api/v1/", StringComparison.Ordinal) &&
                       !string.Equals(path, "/api/v1/state", StringComparison.Ordinal) &&
                       !string.Equals(path, "/api/v1/refresh", StringComparison.Ordinal);

    var methodAllowed = (path, method) switch
    {
        ("/", "GET") => true,
        ("/dashboard.css", "GET") => true,
        ("/api/v1/state", "GET") => true,
        ("/api/v1/refresh", "POST") => true,
        _ when isIssueRoute && string.Equals(method, "GET", StringComparison.OrdinalIgnoreCase) => true,
        _ => false
    };

    var isKnownRoute = path is "/" or "/dashboard.css" or "/api/v1/state" or "/api/v1/refresh" || isIssueRoute;
    if (isKnownRoute && !methodAllowed)
    {
        context.Response.StatusCode = StatusCodes.Status405MethodNotAllowed;
        await context.Response.WriteAsJsonAsync(new { error = new ErrorEnvelope("method_not_allowed", "Method not allowed") });
        return;
    }

    await next();
});

app.MapGet("/dashboard.css", () => Results.Content(DashboardAssets.Css, "text/css"));

app.MapGet("/", async context =>
{
    context.Response.ContentType = "text/html; charset=utf-8";
    await context.Response.WriteAsync(DashboardAssets.Html);
});

app.MapGet("/api/v1/state", (RuntimeStateStore store) => Results.Json(store.GetStatePayload()));

app.MapPost("/api/v1/refresh", async (SymphonyRuntimeService runtime) =>
{
    var payload = await runtime.RequestRefreshAsync();
    return Results.Json(payload, statusCode: StatusCodes.Status202Accepted);
});

app.MapGet("/api/v1/{issueIdentifier}", (string issueIdentifier, RuntimeStateStore store) =>
{
    var payload = store.GetIssuePayload(issueIdentifier);
    return payload is null
        ? Results.Json(new { error = new ErrorEnvelope("issue_not_found", "Issue not found") }, statusCode: StatusCodes.Status404NotFound)
        : Results.Json(payload);
});

app.MapFallback(() => Results.Json(new { error = new ErrorEnvelope("not_found", "Route not found") }, statusCode: StatusCodes.Status404NotFound));

app.Run();

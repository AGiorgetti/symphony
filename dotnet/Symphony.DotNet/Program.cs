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

builder.Services.AddSingleton(options);
builder.Services.AddSingleton<RuntimeStateStore>();
builder.Services.AddHostedService<SymphonyRuntimeService>();

var app = builder.Build();

app.MapGet("/dashboard.css", () => Results.Content(DashboardAssets.Css, "text/css"));

app.MapMethods("/", new[] { "GET" }, async context =>
{
    context.Response.ContentType = "text/html; charset=utf-8";
    await context.Response.WriteAsync(DashboardAssets.Html);
});

app.MapMethods("/api/v1/state", new[] { "GET" }, (RuntimeStateStore store) => Results.Json(store.GetStatePayload()));

app.MapMethods("/api/v1/refresh", new[] { "POST" }, (RuntimeStateStore store) =>
{
    var payload = store.RequestRefresh();
    return Results.Json(payload, statusCode: StatusCodes.Status202Accepted);
});

app.MapMethods("/api/v1/{issueIdentifier}", new[] { "GET" }, (string issueIdentifier, RuntimeStateStore store) =>
{
    var payload = store.GetIssuePayload(issueIdentifier);
    return payload is null
        ? Results.Json(new { error = new ErrorEnvelope("issue_not_found", "Issue not found") }, statusCode: StatusCodes.Status404NotFound)
        : Results.Json(payload);
});

app.MapFallback(() => Results.Json(new { error = new ErrorEnvelope("not_found", "Route not found") }, statusCode: StatusCodes.Status404NotFound));

app.Run();

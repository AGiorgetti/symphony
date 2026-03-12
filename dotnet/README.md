# Symphony .NET

This directory contains the ASP.NET Core port-in-progress for Symphony.

## Current Focus

The current implementation covers the observability host first:

- `GET /`
- `GET /dashboard.css`
- `GET /api/v1/state`
- `POST /api/v1/refresh`
- `GET /api/v1/{issueIdentifier}`
- JSON `404` and `405` handling for the known routes

It also includes a spec-driven `WORKFLOW.md` loader and a background runtime service that surfaces:

- valid workflow state when dispatch prerequisites are satisfied
- degraded/retrying state when workflow loading or validation fails

## Validation

```powershell
$env:DOTNET_CLI_HOME='C:\Work\symphony\dotnet\.dotnet-home'
$env:DOTNET_SKIP_FIRST_TIME_EXPERIENCE='1'
$env:DOTNET_CLI_TELEMETRY_OPTOUT='1'

dotnet build .\Symphony.DotNet\Symphony.DotNet.csproj -v minimal
dotnet run --project .\Symphony.DotNet.Tests\Symphony.DotNet.Tests.csproj
```

## Next Implementation Areas

- replace placeholder runtime state with real orchestrator state
- implement workspace management and safety invariants from `SPEC.md`
- implement Codex app-server runner and session accounting
- replace placeholder workflow-loaded row with real running/retrying issue rows
- add tracker integration beyond validation-only workflow state

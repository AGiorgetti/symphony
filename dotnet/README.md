# Symphony .NET

This directory contains the ASP.NET Core port of Symphony Elixir.

## Current Surface

The current implementation covers the observability host:

- `GET /`
- `GET /dashboard.css`
- `GET /api/v1/state`
- `POST /api/v1/refresh`
- `GET /api/v1/{issueIdentifier}`
- JSON `404` and `405` handling for the known routes

It also includes:

- a spec-driven `WORKFLOW.md` loader with last-known-good reload behavior
- a real background orchestrator with polling, dispatch, retries, reconciliation, and refresh coalescing
- workspace lifecycle management and safety checks
- a Codex app-server client with approval/user-input handling and `linear_graphql` dynamic tool support
- a Linear tracker client with explicit transport / GraphQL error mapping

On Windows, `codex.command` is launched via `cmd.exe /d /s /c`. On non-Windows hosts, it is launched via `/bin/bash -lc`.

## Validation

```powershell
$env:DOTNET_CLI_HOME='C:\Work\symphony\dotnet\.dotnet-home'
$env:DOTNET_SKIP_FIRST_TIME_EXPERIENCE='1'
$env:DOTNET_CLI_TELEMETRY_OPTOUT='1'

dotnet build .\Symphony.DotNet\Symphony.DotNet.csproj -v minimal
dotnet run --project .\Symphony.DotNet.Tests\Symphony.DotNet.Tests.csproj
```

## Remaining Gap

- run a live end-to-end Codex app-server session with valid local credentials
- exercise real Linear polling/reconciliation against live tracker data
- deepen issue-detail/history surfaces if exact dashboard parity with Elixir is required

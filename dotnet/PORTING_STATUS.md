# Symphony .NET Port Status

## Current Direction

- User requested a .NET version of Symphony Elixir.
- Direction changed from a console-first MVP to an ASP.NET Core host because the Elixir implementation's UI is a core surface.
- `harness-engineering` was used to narrow the first-pass contract around:
  - `WORKFLOW.md`
  - workspace safety
  - Linear tracker integration
  - Codex app-server protocol
  - observability JSON API and dashboard

## Repository State

- Created `dotnet/` directory.
- Generated projects:
  - `dotnet/Symphony.DotNet`
  - `dotnet/Symphony.DotNet.Tests`
- Generated solution file:
  - `dotnet/SymphonyDotNet.slnx`
- Added project reference from `Symphony.DotNet.Tests` to `Symphony.DotNet`.

## Key Findings Captured

### Elixir Runtime Boundary

- Main Elixir runtime modules:
  - `workflow.ex`, `config.ex`, `orchestrator.ex`, `agent_runner.ex`, `workspace.ex`
  - `codex/app_server.ex`
  - `tracker.ex` and `linear/*`
- Runtime entrypoint:
  - `./bin/symphony [--logs-root <path>] [--port <port>] [path-to-WORKFLOW.md]`

### UI / API Contract To Mirror

- Routes:
  - `GET /`
  - `GET /api/v1/state`
  - `POST /api/v1/refresh`
  - `GET /api/v1/{issue_identifier}`
- Wrong methods return `405` JSON.
- Unknown routes return `404` JSON.
- Shared presenter payload shape:
  - `generated_at`
  - `counts.running`
  - `counts.retrying`
  - `running[]`
  - `retrying[]`
  - `codex_totals`
  - `rate_limits`
- Dashboard sections:
  - hero/status header
  - metric cards
  - rate limits panel
  - running sessions table
  - retry queue table

## Blockers Encountered

- `apply_patch` is failing in this workspace even for a one-line probe.
- No implementation edits have landed yet beyond generated `dotnet` scaffolding.
- Shell-based file writes are being used as a fallback until patching is reliable again.

## Next Steps

1. Convert `dotnet/Symphony.DotNet` from console SDK to ASP.NET Core SDK.
2. Replace template `Program.cs` with an ASP.NET Core host.
3. Add an in-repo worklog update after each major milestone.
4. Implement shared presenter DTOs to match the Elixir JSON contract.
5. Implement minimal dashboard UI backed by the same JSON payload.
6. Implement runtime services behind the web host:
   - workflow loader
   - orchestrator loop
   - workspace manager
   - tracker abstraction
   - Codex app-server client
7. Add local tests in `Symphony.DotNet.Tests`.
## Milestone Update

- Added `dotnet/PORTING_STATUS.md` to persist context across restarts.
- Converted `dotnet/Symphony.DotNet` into an ASP.NET Core project.
- Implemented first-pass observability host with:
  - `GET /`
  - `GET /dashboard.css`
  - `GET /api/v1/state`
  - `POST /api/v1/refresh`
  - `GET /api/v1/{issueIdentifier}`
  - fallback `404` JSON
- Added shared DTOs matching the Elixir presenter payload shape.
- Added an in-memory background runtime service to populate the dashboard.
- Pending verification:
  - live smoke test after explicit Kestrel port binding change
  - method-specific `405` behavior parity
  - real workflow/orchestrator integration behind the presenter

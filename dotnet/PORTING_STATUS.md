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
## Spec Reminder

- Continue implementation against `SPEC.md`, not just Elixir parity.
- Immediate areas to align before expanding features:
  - workflow/config parsing and validation
  - API error/method behavior
  - observability payload semantics
  - workspace and agent-runner boundaries
## Verification Update

- Smoke-tested ASP.NET Core host against `C:\Work\symphony\elixir\WORKFLOW.md`.
- With `LINEAR_API_KEY` set, verified:
  - `GET /api/v1/state` -> `200`
  - `POST /api/v1/refresh` -> `202`
  - `GET /api/v1/WORKFLOW` -> `200`
  - wrong method on `/api/v1/state` -> `405`
- Current runtime is still workflow-validation-driven placeholder state, not a full orchestrator.
## Runtime Stability Fix

- Disabled default host logging providers and switched to console-only logging.
- Reason: Windows EventLog logging was throwing access-denied during background-service warnings and stopping the runtime before degraded snapshots could publish.
## Harness Update

- Added `Symphony.DotNet` and `Symphony.DotNet.Tests` to `SymphonyDotNet.slnx`.
- Added `dotnet/README.md` and `dotnet/AGENTS.md` with current commands and guardrails.
## Latest Stable Checkpoint

- `dotnet build .\\dotnet\\SymphonyDotNet.slnx -v minimal` passes.
- `dotnet run --project .\\dotnet\\Symphony.DotNet.Tests\\Symphony.DotNet.Tests.csproj` passes.
- Current implementation status:
  - ASP.NET Core observability host is working.
  - `WORKFLOW.md` loading/validation is wired in.
  - valid and degraded workflow states are both surfaced in the API.
  - placeholder runtime state still needs replacement with real orchestrator + tracker + Codex session behavior.
## Solution Note

- `SymphonyDotNet.slnx` did not build reliably under `dotnet build` in this workspace.
- Switching to a standard `.sln` file for the primary solution artifact.
## Latest Stable Checkpoint

- Primary solution file: `C:\Work\symphony\dotnet\SymphonyDotNetClassic.sln`
- `dotnet build C:\Work\symphony\dotnet\SymphonyDotNetClassic.sln -v minimal` passes.
- `dotnet run --project C:\Work\symphony\dotnet\Symphony.DotNet.Tests\Symphony.DotNet.Tests.csproj` passes.
- Current working surfaces:
  - ASP.NET Core observability host
  - dashboard HTML + CSS route
  - `/api/v1/state`
  - `/api/v1/refresh`
  - `/api/v1/{issueIdentifier}`
  - JSON `404` and `405`
  - `WORKFLOW.md` load/validation path
  - degraded workflow handling
- Major remaining implementation gap:
  - replace placeholder runtime state with real orchestrator, workspace, tracker, and Codex app-server behavior.
## Orchestrator Milestone

- Replaced the placeholder `WORKFLOW` heartbeat loop with a real orchestrator-owned runtime in `dotnet/Symphony.DotNet/Services/SymphonyRuntimeService.cs`.
- Added a `WorkflowStore` equivalent in `dotnet/Symphony.DotNet/Services/WorkflowStore.cs`:
  - keeps the last known good workflow,
  - watches `WORKFLOW.md` for changes,
  - preserves the effective config when reload fails.
- Startup behavior is now spec-aligned:
  - dispatch validation failure aborts host startup,
  - missing/invalid `WORKFLOW.md` no longer degrades into fake retry rows.
- Added orchestrator state/retry infrastructure in `dotnet/Symphony.DotNet/Services/OrchestratorModels.cs` and updated retry behavior to use the spec-aligned exponent cap (`10`) in `dotnet/Symphony.DotNet/Services/OrchestrationPolicy.cs`.
- Wired real refresh semantics:
  - `/api/v1/refresh` now signals the runtime and returns queue/coalescing metadata instead of a timestamp-only stub.
- Runtime state published to the API/dashboard is now orchestrator-backed and includes polling/session/runtime metadata:
  - `polling`
  - `codex_app_server_pid`
  - `runtime_seconds`
  - `due_in_ms`

## Worker / Codex Milestone

- Added strict prompt rendering in `dotnet/Symphony.DotNet/Services/PromptRenderer.cs` with support for:
  - `{{ issue.* }}` / `{{ attempt }}` variables
  - `{% if %} / {% else %} / {% endif %}` blocks
  - failure on unknown variables.
- Added `CodexAgentRunner` in `dotnet/Symphony.DotNet/Services/AgentRunner.cs`.
- Added a first-pass Codex app-server client in `dotnet/Symphony.DotNet/Services/CodexAppServerClient.cs` that:
  - launches `codex.command` through the platform shell in the workspace,
  - performs `initialize` / `initialized` / `thread/start` / `turn/start`,
  - streams Codex updates back into the orchestrator,
  - auto-handles approval and non-interactive input cases,
  - exposes a `linear_graphql` dynamic tool backed by Symphony Linear auth.
- Added `TrackerClientFactory` and upgraded `LinearTrackerClient` to:
  - send Authorization headers,
  - short-circuit empty state lists,
  - preserve pagination and blocker normalization behavior.

## Validation Update

- `dotnet build .\dotnet\Symphony.DotNet\Symphony.DotNet.csproj -v minimal` passes.
- `dotnet build .\dotnet\Symphony.DotNet.Tests\Symphony.DotNet.Tests.csproj -v minimal` passes.
- `dotnet build .\dotnet\SymphonyDotNetClassic.sln -m:1 -v minimal` passes.
- `dotnet run --project .\dotnet\Symphony.DotNet.Tests\Symphony.DotNet.Tests.csproj --no-build` passes.

## Codex / Tracker Hardening Checkpoint

- Windows Codex session startup no longer depends on Git Bash:
  - `dotnet/Symphony.DotNet/Services/CodexAppServerClient.cs` now launches `codex.command` with a Windows-safe shell strategy (`cmd.exe /d /s /c`) while preserving `/bin/bash -lc` semantics on non-Windows hosts.
  - stdio shutdowns now surface the subprocess exit code (`codex_port_exit:<status>`) instead of a generic closed-stream error.
- Codex config defaults now match the Elixir runtime posture more closely in `dotnet/Symphony.DotNet/Services/WorkflowLoader.cs`:
  - `codex.approval_policy` defaults to the reject-map rather than `"never"`,
  - `codex.turn_sandbox_policy` defaults to a concrete `workspaceWrite` policy rooted at the effective workspace root,
  - `codex.stall_timeout_ms <= 0` now disables stall detection instead of silently reverting to the default timeout.
- Non-interactive tool input fallback is now parity-aligned:
  - `item/tool/requestUserInput` first tries approval labels only in auto-approve mode,
  - otherwise it falls back to the canned non-interactive answer instead of stalling or failing prematurely.
- `linear_graphql` tool validation and Linear transport mapping were tightened:
  - invalid `variables` payloads now fail explicitly,
  - poll/query paths now surface `linear_graphql_errors` and `linear_payload_shape`,
  - HTTP transport, non-200 status, and malformed JSON bodies now map to explicit failure classes.
- Smoke coverage now includes:
  - Windows Codex launch strategy,
  - approval + tool-input + dynamic-tool round-trip handling,
  - Codex default policy/sandbox loading,
  - stall-timeout disable semantics,
  - Linear candidate fetch variable shape,
  - empty-state short-circuiting,
  - GraphQL error surfacing,
  - HTTP transport/status/payload error mapping.

## Current Remaining Gap

- The port is no longer on a placeholder runtime, but the app-server/tracker path has still not been driven through a real end-to-end Codex session against live tracker data in this workspace.
- Remaining completion work is concentrated in real integration hardening, not in missing host/orchestrator scaffolding:
  - validate the app-server protocol against a live Codex runtime,
  - exercise real tracker polling/retries with valid Linear auth,
  - deepen issue-detail history/log surfaces if parity with the Elixir dashboard needs to be exact.

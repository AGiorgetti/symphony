# Symphony .NET Agent Notes

## Source of Truth

- Follow [`..\SPEC.md`](C:\Work\symphony\SPEC.md) first.
- Treat the Elixir implementation as a reference, not an override of the spec.

## Current Shape

- ASP.NET Core host in `Symphony.DotNet`
- process-level smoke tests in `Symphony.DotNet.Tests`
- persistent handoff log in `..\PORTING_STATUS.md`

## Working Rules

- Keep the observability payload aligned with the spec and current Elixir presenter contract.
- Prefer local validation over assumptions.
- Update `PORTING_STATUS.md` after each meaningful milestone or blocker.
- If you change workflow/config behavior, re-check the `SPEC.md` sections for:
  - workflow parsing
  - validation and error surface
  - HTTP observability behavior
  - workspace safety

## Commands

```powershell
$env:DOTNET_CLI_HOME='C:\Work\symphony\dotnet\.dotnet-home'
$env:DOTNET_SKIP_FIRST_TIME_EXPERIENCE='1'
$env:DOTNET_CLI_TELEMETRY_OPTOUT='1'

dotnet build .\dotnet\Symphony.DotNet\Symphony.DotNet.csproj -v minimal
dotnet run --project .\dotnet\Symphony.DotNet.Tests\Symphony.DotNet.Tests.csproj
```

# AGENTS.md

## Scope

These instructions apply to the entire repository. More specific `AGENTS.md` files override these rules for their subtree.

Xel's Combat AI is a C# Dalamud plugin for Final Fantasy XIV. It manages a dedicated BossMod Reborn preset during combat, using BossMod IPC for movement/range/positioning strategies, Avarice shared data for positionals, and optional RotationSolver Reborn IPC for True North behavior.

## Repository Layout

- `XelsCombatAI/Plugin.cs` - composition root for Dalamud lifecycle, command registration, DTR, config save wiring, and UI setup.
- `XelsCombatAI/Config/` - persisted configuration, migrations, clamping, defaults, and reset behavior.
- `XelsCombatAI/UI/` - ImGui windows and UI-only helpers.
- `XelsCombatAI/Runtime/` - framework update orchestration, BossMod preset lifecycle, runtime cache, and status reporting.
- `XelsCombatAI/Combat/` - combat policy controllers for range, positionals, gap closers, and escape movement.
- `XelsCombatAI/Game/` - low-level game helpers, job role mapping, action IDs, constants, and geometry utilities.
- `XelsCombatAI/Integrations/` - BossMod, Avarice, RotationSolver, reflection, IPC, and dependency wrappers.
- `XelsCombatAI/Services/` - injected Dalamud service container/wrappers.
- `XelsCombatAI/Models/` - small shared enums and simple types.
- `XelsCombatAI/GlobalUsings.cs` - global imports for internal XCAI namespaces.
- `scripts/package-release.sh` - release build and zip packaging script.
- `.github/workflows/release.yml` - GitHub release workflow.
- `pluginmaster.json` - custom plugin repository metadata.
- `external/` - read-only external reference workspace. See `external/AGENTS.md`; its instructions override this file inside that directory.

## Build And Validation

Use these commands from the repository root:

```bash
dotnet restore XelsCombatAI/XelsCombatAI.csproj
dotnet build XelsCombatAI/XelsCombatAI.csproj -c Release -p:EnableWindowsTargeting=true
scripts/package-release.sh
```

Notes:

- The project targets `net10.0-windows8.0` through `Dalamud.NET.Sdk/15.0.0`.
- On Linux, set `DALAMUD_HOME` to a directory containing Dalamud dev assemblies before building.
- Local non-CI builds reference ECommons at `../../AutoDuty/ECommons/ECommons/ECommons.csproj`.
- CI builds reference ECommons at `../ECommons/ECommons.csproj` after checkout by the release workflow.
- There is currently no test project. Prefer `dotnet build` as the minimum validation after code changes.
- Before finishing code changes, run the most relevant available validation command and report any command that could not be run.

## C# Organization

- Use standard SDK-style C# organization for this plugin: one assembly, responsibility-based folders, and file-scoped namespaces matching folder paths under `XelsCombatAI` (`XelsCombatAI.Combat`, `XelsCombatAI.Runtime`, etc.).
- Keep `Plugin.cs` as the composition root only. Do not add combat policy, IPC details, game math, or UI drawing logic to `Plugin.cs`.
- Use `internal` for implementation types by default. Add `public` only for plugin/config surface or when required by Dalamud serialization.
- Follow the existing style: nullable enabled, explicit access modifiers, `this.` for instance members, and small internal helper methods.
- Use injected `DalamudServices` or explicit service dependencies in implementation classes. Do not reference static `Plugin.*` services outside `Plugin.cs`.
- Keep runtime update work lightweight. `CombatRuntime.OnFrameworkUpdate` runs frequently and currently throttles strategy updates to 250 ms.
- Use `System.Globalization.CultureInfo.InvariantCulture` for serialized numeric IPC strings.

## Documentation Policy

- `README.md` is user-facing only: installation, requirements, commands, configuration, behavior, and user safety notes.
- Do not put architecture, contributor, build, release, or agent instructions in `README.md`.
- Put repo structure, coding standards, validation commands, and agent workflow rules in `AGENTS.md`.
- When adding or changing user-visible settings, update `UI/ConfigWindow.cs`, `Config/Configuration.cs`, defaults/migrations as needed, and the user-facing `README.md`.

## Integration Safety

- Keep plugin behavior conservative. The code runs during combat and writes BossMod transient strategies, so avoid broad refactors or timing changes unless the task requires them.
- Preserve IPC names, track names, option strings, preset payload module names, and BossMod preset name unless you have verified the upstream contract in `external/` or the relevant upstream project.
- Required runtime dependencies are BossMod Reborn and Avarice. Do not remove or loosen those dependency checks unless the requested behavior explicitly requires it.
- RotationSolver Reborn is optional and only required for `Manage True North`. Do not loosen this behavior unless explicitly requested.
- Do not introduce network calls or background tasks in the combat update path.
- Log recoverable integration failures with `IPluginLog.Verbose` where the plugin should keep running.
- The plugin should fully hand control back out of combat by disabling movement, resetting range/role/positional strategy state, and clearing the active preset when appropriate.
- Death/resurrection handling matters because BossMod can clear active presets on death.

## Release And Metadata

When changing the plugin version, keep these files in sync:

- `XelsCombatAI/XelsCombatAI.csproj` `Version`
- `pluginmaster.json` `AssemblyVersion`

When changing plugin description, tags, icon URL, name, or Dalamud API metadata, check both:

- `XelsCombatAI/XelsCombatAI.json`
- `pluginmaster.json`

`scripts/package-release.sh` writes `artifacts/XelsCombatAI.zip`. Treat `artifacts/`, `bin/`, and `obj/` as generated output.

## External References And Generated Files

- Do not edit files under ignored external checkouts in `external/BossmodReborn/`, `external/Avarice/`, or `external/RotationSolverReborn/`.
- Do not commit or package external plugin source.
- Keep external reference URLs and tracked branches in `external/sources.json`.
- External references should track the latest remote branch heads, not pinned commits.
- Use `external/fetch-sources.sh` to clone or refresh ignored reference checkouts.
- Do not hand-edit generated build output under `artifacts/`, `bin/`, or `obj/`.

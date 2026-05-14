# AGENTS.md

## Scope

These instructions apply to the entire repository. Nested instruction files apply to their subtree and may add or override guidance for that scope.

Codex instruction discovery for this repository should be treated as:

- Start at the repository root and walk down to the working directory.
- In each directory, load at most one instruction file in this order: `AGENTS.override.md`, then `AGENTS.md`.
- If `AGENTS.override.md` exists in a directory, it replaces `AGENTS.md` for that same directory.
- Later files in the root-to-working-directory chain override earlier guidance when they conflict.
- Keep the combined instruction set small enough for Codex to load; the default project instruction cap is 32 KiB.

Xel's Combat AI is a C# Dalamud plugin for Final Fantasy XIV. It manages a dedicated BossMod Reborn preset during combat, using BossMod IPC for movement/range/positioning strategies, Avarice shared data for positionals, and optional RotationSolver Reborn IPC for True North behavior.

## Agent Instruction Standards

- Treat this file as the project-level instruction source for coding agents. Keep it focused on durable repository rules, not task-specific notes.
- Use nested `AGENTS.md` files for stable subtree-specific rules, such as tool-only or external-reference rules.
- Use `AGENTS.override.md` only for intentional local or temporary overrides. Do not add one unless the override behavior is explicitly desired.
- Keep new instructions concrete and verifiable. Prefer exact commands, file paths, ownership boundaries, and review expectations over broad preferences.
- Do not duplicate large blocks of instructions in nested files. Put common rules here and only add subtree-specific differences in nested files.
- If instructions become too large or specialized, split them into a closer scoped `AGENTS.md` and keep the root file focused on repository-wide rules.
- Do not use alternate instruction filenames unless Codex has been explicitly configured to discover them.

## Product Purpose

The primary purpose of Xel's Combat AI is to give BossMod Reborn better choices that make automated combat movement look more human for any job, while preserving BossMod Reborn as the source of encounter safety.

Agent work should optimize for plausible player-like choices among safe options, not for perfect automation, maximum theoretical uptime, or aggressive control.

Before changing behavior, evaluate whether the change:

- Keeps BossMod Reborn safety and encounter logic authoritative.
- Makes movement, targeting, or positioning look more like a competent human decision.
- Is job-aware where job range, AoE shape, cast behavior, party role, or mobility differs.
- Avoids excessive target switching, oscillation, snap movement, or overly perfect reactions.
- Respects manual player input and hands control back cleanly.
- Has bounded scope and does not add broad automation unrelated to human-like BossMod Reborn choices.

For behavior changes, include a short "Purpose fit" note in the final response:

- What human-like behavior does this improve?
- What BossMod Reborn authority or safety behavior is preserved?
- What could make this look unnatural, and how is that bounded?

A change is aligned when it improves one or more of:

- Natural combat spacing.
- Believable target selection.
- Sensible AoE positioning.
- Safe use of mobility tools.
- Human-like use of party utility zones.
- Avoiding awkward robotic positions, such as boss centers, frontals, or arena edges.
- Reducing movement jitter, target churn, or overcorrection.

A change is not aligned if it primarily:

- Bypasses BossMod Reborn safety.
- Automates unrelated gameplay decisions.
- Maximizes uptime at the cost of believable movement.
- Makes behavior more perfect, instant, or mechanical than a human player.
- Broadens combat control without a clear human-likeness reason.

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
- `.github/workflows/ci.yml` - build check on every PR (required status check for branch protection).
- `.github/workflows/bump-and-prerelease.yml` - bumps version on merge, pushes `v*-pre` tag.
- `.github/workflows/prerelease.yml` - builds and uploads a GitHub pre-release on `v*-pre` tags.
- `.github/workflows/promote-stable.yml` - manual `workflow_dispatch` to promote testing → stable.
- `.github/workflows/release.yml` - builds and uploads a stable GitHub release on `v*` tags (no `-pre`).
- `pluginmaster.json` - custom plugin repository metadata.
- `external/` - read-only external reference workspace. See `external/AGENTS.md`; its instructions override this file inside that directory.

## Build And Validation

Use these commands from the repository root:

```bash
dotnet restore XelsCombatAI/XelsCombatAI.csproj
dotnet build XelsCombatAI/XelsCombatAI.csproj -c Release -p:EnableWindowsTargeting=true
scripts/package-release.sh
dotnet format XelsCombatAI/XelsCombatAI.csproj --verify-no-changes
```

Notes:

- The project targets `net10.0-windows8.0` through `Dalamud.NET.Sdk/15.0.0`.
- On Linux, set `DALAMUD_HOME` to a directory containing Dalamud dev assemblies before building.
- Local non-CI builds reference ECommons at `../../AutoDuty/ECommons/ECommons/ECommons.csproj`.
- CI builds reference ECommons at `../ECommons/ECommons.csproj` after checkout by the release workflow.
- There is currently no test project. Prefer `dotnet build` as the minimum validation after code changes.
- Run `dotnet restore` when dependency, SDK, target framework, or project-file changes could affect restore output.
- Run `scripts/package-release.sh` only for release/package changes or when packaging behavior may have changed.
- Run `dotnet format --verify-no-changes` for broad C# edits when the local SDK supports it. If it cannot run cleanly because of environment issues, report that explicitly.
- Before finishing code changes, run the most relevant available validation command and report any command that could not be run.

## C# Style And Organization

- Use standard SDK-style C# organization for this plugin: one assembly, responsibility-based folders, and file-scoped namespaces matching folder paths under `XelsCombatAI` (`XelsCombatAI.Combat`, `XelsCombatAI.Runtime`, etc.).
- Keep `Plugin.cs` as the composition root only. Do not add combat policy, IPC details, game math, or UI drawing logic to `Plugin.cs`.
- Use the repository's existing folder names and boundaries. Do not introduce generic alternatives such as `Commands/`, `Windows/`, `Configuration/`, or `Core/` solely to match an external template.
- Use `internal` for implementation types by default. Add `public` only for plugin/config surface or when required by Dalamud serialization.
- Follow the existing style: nullable enabled, explicit access modifiers, `this.` for instance members, and small focused helper methods.
- Use PascalCase for public types, methods, properties, events, and constants.
- Use camelCase for local variables and parameters.
- Use `_camelCase` for private instance fields only if the surrounding file already uses that convention; otherwise match the local file style.
- Prefer clear names over abbreviations.
- Use four spaces for indentation and Allman braces, matching the existing C# files.
- Put one statement on each line.
- Prefer readable code over clever code.
- Use C# aliases such as `string`, `int`, and `bool` instead of `System.String`, `System.Int32`, and `System.Boolean`.
- Use `var` only when the type is obvious from the right-hand side.
- Use nullable reference types correctly. Do not silence warnings with `!` unless the lifecycle makes safety obvious at the use site.
- Avoid broad `catch (Exception)` unless the exception is logged and the plugin can safely recover.
- Prefer pure C# classes for logic that can be tested without Dalamud or the game running.
- Use injected `DalamudServices` or explicit service dependencies in implementation classes. Do not reference static `Plugin.*` services outside `Plugin.cs`.
- Keep runtime update work lightweight. `CombatRuntime.OnFrameworkUpdate` runs frequently and currently throttles strategy updates to 250 ms.
- Use `System.Globalization.CultureInfo.InvariantCulture` for serialized numeric IPC strings.

## Dalamud API And Lifecycle Rules

Do not guess Dalamud APIs. Before using a Dalamud service, method, event, or type:

- Prefer existing usage in this repository.
- Prefer installed package metadata, IDE completion, generated bindings, or build errors over memory.
- Use `external/` as a read-only API reference before guessing. Start with `external/Dalamud/` for framework services and generated bindings, `external/SamplePlugin/` for official plugin lifecycle patterns, `external/DalamudPackager/` for packaging behavior, and `external/DalamudPluginsD17/` for public repo metadata examples.
- For game data or native structures behind Dalamud services, inspect `external/FFXIVClientStructs/`, `external/Lumina/`, and `external/FfxivDatamining/` as applicable.
- If a checkout is missing or stale and the answer depends on current upstream behavior, run `external/fetch-sources.sh` before drawing conclusions.
- If unsure, inspect the current API in the installed package or say that the API needs verification.

Follow these Dalamud plugin conventions:

- The plugin entrypoint must implement `IDalamudPlugin`.
- `Dispose()` must fully clean up plugin-owned resources.
- Register Dalamud services through the existing project pattern.
- Prefer constructor-injected services where the project already uses that style.
- Use `IPluginLog` for logging. Do not use `Console.WriteLine` in plugin runtime code.
- Register slash commands through `ICommandManager` and remove all registered commands in `Dispose()`.
- Unsubscribe all event handlers in `Dispose()`.
- Dispose windows, textures, hooks, IPC providers/subscribers, and other disposable resources.
- Do not block the framework/update thread.
- Keep ImGui draw methods lightweight.
- Do not do expensive scanning, file I/O, network I/O, or reflection inside draw/update loops.
- Do not change plugin metadata, manifest fields, packaging, release workflow, or Dalamud API level unless explicitly asked.

## Documentation Policy

- `README.md` is user-facing only: installation, requirements, commands, configuration, behavior, and user safety notes.
- Do not put architecture, contributor, build, release, or agent instructions in `README.md`.
- Put repo structure, coding standards, validation commands, and agent workflow rules in `AGENTS.md`.
- When adding or changing user-visible settings, update `UI/ConfigWindow.cs`, `Config/Configuration.cs`, defaults/migrations as needed, and the user-facing `README.md`.

## Config UI Style

- Keep the config window organized around the current tab layout: `Main`, `Positioning`, `Distance`, `Gap Closers`, and `Chat & Reset`.
- Put only broad, frequently used controls in `Main`. Keep positional behavior in `Positioning`, range and forbidden-zone behavior in `Distance`, gap closer behavior in `Gap Closers`, and feedback/reset controls in `Chat & Reset`.
- Use `DrawSectionHeader` for plain groups and `DrawToggleSectionHeader` for master toggles that own a group of dependent controls.
- Use existing UI helper methods (`Checkbox`, `Combo`, `SliderFloat`, `SliderInt`, `DrawToggleSectionHeader`) instead of drawing one-off controls, so reset behavior, disabled-state tooltips, and info icons stay consistent.
- For options whose label alone may be unclear, add a `FontAwesomeIcon.InfoCircle` info icon through the existing tooltip helper path. The icon tooltip should explain the user-visible behavior in clear, direct language.
- Do not put long explanations on the main label hover when an info icon is present. Label/control hover should remain available for disabled-state explanations and reset behavior.
- Keep tooltip text concise and scannable. Use short sentences and explicit line breaks for multi-part explanations. All config tooltips should go through the wrapped tooltip helper rather than raw `ImGui.SetTooltip`.
- Do not mention `XCAI`, "this plugin", or similar self-references in config tooltips unless the distinction is necessary to avoid ambiguity. The user already knows which plugin they are configuring.
- Use disabled-state tooltips to explain why a control is unavailable, usually by naming the controlling option and tab.
- Use skull icons only for combat-risk warnings. When a feature can put the player in danger during combat, keep the normal info icon focused on what the option does and put the risk warning on the skull icon tooltip.
- Do not make config text patronizing or alarmist. Be plain about risk, limitations, and dependencies without blaming the user.

## Integration Safety

- Keep plugin behavior conservative. The code runs during combat and writes BossMod transient strategies, so avoid broad refactors or timing changes unless the task requires them.
- Preserve IPC names, track names, option strings, preset payload module names, and BossMod preset name unless you have verified the upstream contract in `external/` or the relevant upstream project.
- Required runtime dependencies are BossMod Reborn and Avarice. Do not remove or loosen those dependency checks unless the requested behavior explicitly requires it.
- RotationSolver Reborn is optional and only required for `Manage True North`. Do not loosen this behavior unless explicitly requested.
- Do not introduce network calls or background tasks in the combat update path.
- Log recoverable integration failures with `IPluginLog.Verbose` where the plugin should keep running.
- The plugin should fully hand control back out of combat by disabling movement, resetting range/role/positional strategy state, and clearing the active preset when appropriate.
- Death/resurrection handling matters because BossMod can clear active presets on death.

## Combat Automation Safety

This repository is for a private/custom Dalamud plugin that changes combat movement policy through BossMod Reborn strategies. Treat combat automation changes as high-risk even when requested.

Before changing combat automation, server interaction, memory writing, hooks, or behavior that may violate Dalamud plugin restrictions:

- Stop and explain the risk if the task is ambiguous, broader than the plugin's stated purpose, or not clearly requested as private/custom plugin behavior.
- State the risk in the implementation notes or final response when the change proceeds and the risk is material.
- Prefer read-only information display when it satisfies the task.
- Do not implement behavior that performs actions a normal player could not perform manually.
- Do not bypass BossMod Reborn safety, force unsafe movement, or make decisions outside the plugin's stated human-like movement purpose.
- Keep the change bounded to the requested behavior and preserve manual player control handoff.

## AI-Generated Code Review Expectations

All agent-produced code must be understandable, explainable, and manually reviewable.

After larger changes, include in the final response:

- What changed.
- Why the design was chosen.
- What was validated.
- What must be manually tested in-game.
- Any assumptions about Dalamud, BossMod Reborn, Avarice, RotationSolver Reborn, or game APIs.

## Release And Metadata

### Automated release pipeline

All changes flow through pull requests to `master`. Never commit directly to `master` after branch protection is enabled.

**PR labels** control which version component is bumped on merge:

| Label | Effect |
|-------|--------|
| `release:patch` | Bumps 4th component — default if no label |
| `release:minor` | Bumps 3rd component, zeros 4th |
| `release:major` | Bumps 2nd component, zeros 3rd+4th |
| `no-release` | Skips version bump (docs, chores) |

On merge, `bump-and-prerelease.yml` automatically updates the version files, commits them (`[skip ci]`), and pushes a `v{version}-pre` tag. `prerelease.yml` picks up the tag and publishes a GitHub pre-release — visible only to Dalamud users with **Receive plugin testing versions** enabled.

To cut a stable release, run the **Promote to Stable** workflow manually from the GitHub Actions UI. It reads the current `TestingAssemblyVersion`, updates `AssemblyVersion` and the stable download URLs in `pluginmaster.json`, then pushes a `v{version}` tag (no `-pre`) which triggers `release.yml`.

### Version file sync

The automation keeps these in sync, but if you touch them manually they must match:

- `XelsCombatAI/XelsCombatAI.csproj` → `<Version>` (build version)
- `pluginmaster.json` → `AssemblyVersion` (stable), `TestingAssemblyVersion` (testing)

Download URLs in `pluginmaster.json` must point to specific tag releases (not `/latest/`) so stable and testing can coexist independently.

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

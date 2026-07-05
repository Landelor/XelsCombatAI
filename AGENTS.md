# AGENTS.md

## Scope

These instructions apply to the entire repository. Nested instruction files apply to their subtree and may add or override guidance for that scope.

Codex instruction discovery for this repository should be treated as:

- Start at the repository root and walk down to the working directory.
- In each directory, load at most one instruction file in this order: `AGENTS.override.md`, then `AGENTS.md`.
- If `AGENTS.override.md` exists in a directory, it replaces `AGENTS.md` for that same directory.
- Later files in the root-to-working-directory chain override earlier guidance when they conflict.
- Keep the combined instruction set small enough for Codex to load; the default project instruction cap is 32 KiB.

Xel's Combat AI is a C# Dalamud plugin for Final Fantasy XIV. It manages a dedicated BossMod Reborn preset during combat, using BossMod IPC for movement/range/positioning strategies, RotationSolver Reborn action prediction for primary positional intent, and optional RotationSolver Reborn IPC for True North behavior.

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

BossMod Reborn owns encounter safety: its job is to get the player to a safe place on time. XCAI owns bounded uptime and comfort preferences inside that safety envelope: keeping jobs in useful attack range, avoiding over-dense trash-pack positions, improving AoE coverage, preserving positionals, using safe mobility, and choosing more player-like locations among BossMod-safe options.

Use job and role context to decide which uptime or comfort problem matters:

- Tanks should bias safe choices toward party safety, such as keeping cleaves away from the group, recovering trash aggro without unnecessary walking, and holding sensible tanking positions. When reliable encounter or telegraph data exists, tank-facing choices may also pre-align large slices or half-room cleaves so the party has an easy safe side, or turn the boss to avoid most visible party members.
- Melee should reduce wasted ranged GCDs, preserve positional access, recover melee uptime through safe movement or mobility, and avoid trash-pack positions that hurt AoE coverage or create unnatural crowding.
- Casters and physical ranged should avoid being stranded out of attack range after mechanics when BossMod-safe space exists, even if the usable space is small, while respecting cast locks and manual correction.
- Healers should balance healing coverage and DPS uptime. Healing needs are often pre-emptive because damage can arrive in surges, so safe positioning for upcoming healing coverage can matter before health is already low.

Agent work should optimize for plausible player-like choices among safe options, not for perfect automation, maximum theoretical uptime, or aggressive control.

Before changing behavior, evaluate whether the change:

- Keeps BossMod Reborn safety and encounter logic authoritative.
- Improves uptime, range, target coverage, or comfort only within BossMod-safe choices.
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
- `XelsCombatAI/Integrations/` - BossMod, RotationSolver, vnavmesh, reflection, IPC, and dependency wrappers.
- `XelsCombatAI/Services/` - injected Dalamud service container/wrappers.
- `XelsCombatAI/Models/` - small shared enums and simple types.
- `XelsCombatAI/GlobalUsings.cs` - global imports for internal XCAI namespaces.
- `scripts/test-and-build.sh` - local validation helper. Its `--package` mode delegates to the reusable package script from `XelsDalamudRepo`.
- `.github/workflows/validate.yml` - thin wrapper calling reusable validation from `XelsPlugins/XelsDalamudRepo`.
- `.github/workflows/publish-testing.yml` - thin wrapper calling reusable manual testing publication from `XelsPlugins/XelsDalamudRepo`.
- `.github/workflows/release.yml` - thin wrapper calling reusable manual stable release automation from `XelsPlugins/XelsDalamudRepo`.
- The active plugin feed lives in `XelsPlugins/XelsDalamudRepo`; do not add a local `pluginmaster.json` to this repository.
- `../XelsCombatAI.code-workspace` - workspace file that opens this repo with sibling references.
- `../XelsCombatAIReferences/` - default sibling folder for build and API reference clones. Set `XCAI_REFERENCES_DIR` to use a different location.
- `../XelsDalamudRepo/` - default sibling checkout for package/feed tooling. Set `XELS_DALAMUD_REPO_DIR` to use a different location.

## Build And Validation

Use these commands from the repository root:

```bash
dotnet restore XelsCombatAI/XelsCombatAI.csproj
dotnet build XelsCombatAI/XelsCombatAI.csproj -c Debug -p:EnableWindowsTargeting=true
dotnet build XelsCombatAI/XelsCombatAI.csproj -c Release -p:EnableWindowsTargeting=true
scripts/test-and-build.sh --skip-tools --package
dotnet format XelsCombatAI/XelsCombatAI.csproj --verify-no-changes
```

Notes:

- The project targets `net10.0-windows8.0` through `Dalamud.NET.Sdk/15.0.0`.
- On Linux, set `DALAMUD_HOME` to a directory containing Dalamud dev assemblies before building.
- The default workspace root is the parent folder of this repo. Set `XELS_WORKSPACE_DIR` if this repo is not cloned beside `XelsCombatAIReferences` and `XelsDalamudRepo`.
- Builds reference ECommons at `$XCAI_REFERENCES_DIR/ECommons/ECommons/ECommons.csproj`, defaulting to `$XELS_WORKSPACE_DIR/XelsCombatAIReferences/ECommons/ECommons/ECommons.csproj`.
- `tools/FightReview` builds against BossMod Reborn at `$XCAI_REFERENCES_DIR/BossmodReborn/BossMod/BossModReborn.csproj`, defaulting to `$XELS_WORKSPACE_DIR/XelsCombatAIReferences/BossmodReborn/BossMod/BossModReborn.csproj`.
- Run `scripts/update-build-dependencies.sh` after cloning or when build reference checkouts are missing. Set `XCAI_REFERENCES_DIR` first if references should live somewhere other than `$XELS_WORKSPACE_DIR/XelsCombatAIReferences`.
- `tools/FightReview.Tests` is a custom executable test harness, not a `dotnet test` project. The reusable validation workflow skips it; run `scripts/test-and-build.sh` when tool or review-log behavior changes.
- Run `dotnet restore` when dependency, SDK, target framework, or project-file changes could affect restore output.
- Run `scripts/test-and-build.sh --skip-tools --package` for release/package changes or when packaging behavior may have changed. This requires `XelsDalamudRepo` cloned beside this repo, or `XELS_DALAMUD_REPO_DIR` set to that checkout.
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
- Use the sibling workspace checkouts as read-only API references before guessing. Resolve them from `$XCAI_REFERENCES_DIR`, or from `$XELS_WORKSPACE_DIR/XelsCombatAIReferences` when `XCAI_REFERENCES_DIR` is unset.
- Start with `Dalamud/` for framework services and generated bindings, `BossmodReborn/` for BossMod contracts, `ECommons/` for ECommons APIs, `RotationSolverReborn/` for RSR behavior, and `VNavmesh/` for vnavmesh IPC.
- For game data or native structures behind Dalamud services, inspect `FFXIVClientStructs/`, `Lumina/`, and `FfxivDatamining/` under the reference checkout as applicable.
- If a checkout is missing or stale and the answer depends on current upstream behavior, run `scripts/update-build-dependencies.sh` before drawing conclusions.
- If unsure, inspect the current API in the installed package or say that the API needs verification.

## XelsDevBridge Runtime Inspection

When live runtime state would reduce guessing for XCAI work, use the `xels-tweaks-devbridge` skill instead of duplicating local bridge routes, commands, environment variables, or endpoint assumptions here.

Use DevBridge only for narrow, reviewable investigations such as validating combat state assumptions, target/object visibility, condition flags, addon/UI behavior, or small in-game experiments that cannot be proven from source alone. Preserve the product-purpose and combat-automation safety rules in this file: do not use DevBridge to bypass BossMod Reborn authority, automate broad gameplay, or make behavior more perfect or robotic than a human player.

If DevBridge is unavailable, continue from static code when reasonable and state the limitation. If runtime state is essential, propose the smallest missing bridge capability.

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

- Keep the config window organized around the current tab layout: `General`, `Movement`, `AoE & Trash`, `Positionals`, `Job Specific`, `Dashes`, and `Troubleshooting`.
- Put broad enable/reset controls in `General`. Keep movement timing, danger-zone spacing, healer/utility zones, and edge/facing preferences in `Movement`; trash and AoE target behavior in `AoE & Trash`; positional behavior in `Positionals`; job-specific behavior in `Job Specific`; gap closer behavior in `Dashes`; and feedback, overlay, debug, and run-review controls in `Troubleshooting`.
- Use `DrawSectionHeader` for plain groups and `DrawToggleSectionHeader` for master toggles that own a group of dependent controls.
- Use existing UI helper methods (`Checkbox`, `Combo`, `SliderFloat`, `SliderInt`, `DrawToggleSectionHeader`) instead of drawing one-off controls, so reset behavior, disabled-state tooltips, and info icons stay consistent.
- For options whose label alone may be unclear, add a `FontAwesomeIcon.InfoCircle` info icon through the existing tooltip helper path. The icon tooltip should explain the user-visible behavior in clear, direct language.
- Do not put long explanations on the main label hover when an info icon is present. Label/control hover should remain available for disabled-state explanations and reset behavior.
- Keep tooltip text concise and scannable. Use short sentences and explicit line breaks for multi-part explanations. All config tooltips should go through the wrapped tooltip helper rather than raw `ImGui.SetTooltip`.
- Do not mention `XCAI`, "this plugin", or similar self-references in config tooltips unless the distinction is necessary to avoid ambiguity. The user already knows which plugin they are configuring.
- Use disabled-state tooltips to explain why a control is unavailable, usually by naming the controlling option and tab.
- Use skull icons only for combat-risk warnings. When a feature can put the player in danger during combat, keep the normal info icon focused on what the option does and put the risk warning on the skull icon tooltip.
- Do not make config text patronizing or alarmist. Be plain about risk, limitations, and dependencies without blaming the user.

## Decision Overlay Policy

- When adding or updating a movement, targeting, positioning, or party-utility feature, update `UI/DecisionOverlayController.cs` and any relevant `Combat/*Overlay*` snapshot model so the movement overlay stays accurate for player-visible decisions.
- If a feature affects combat decisions but should not appear in the overlay, state why in the final response. Valid reasons include no actionable player value, duplicate information already shown by another marker, or no stable in-world position to render.
- Do not add overlay items just because internal state exists. Overlay entries should help a player understand a current, candidate, rejected, or near-future decision that could affect their movement, target choice, safety, or party utility.
- Preserve the existing in-world marker, line, shape, badge, color-state, and label style. Style changes or new overlay visual primitives are allowed only after consulting the user.
- Do not add or restore a separate overlay status HUD or text panel for decision summaries. Prefer concise in-world markers and badges.

## Integration Safety

- Keep plugin behavior conservative. The code runs during combat and writes BossMod transient strategies, so avoid broad refactors or timing changes unless the task requires them.
- Preserve IPC names, track names, option strings, preset payload module names, and BossMod preset name unless you have verified the upstream contract in the sibling reference checkout or the relevant upstream project.
- BossMod Reborn is the required runtime dependency.
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
- Any assumptions about Dalamud, BossMod Reborn, ECommons, RotationSolver Reborn, vnavmesh, or game APIs.

## Commit Message Standards

Use Conventional Commits for all agent-authored commits. The shared validation workflow checks non-merge commit subjects on pushes to `main`, and release automation validates the commit range before publishing.

- Use `fix:` or `perf:` for patch-level user-facing changes.
- Use `feat:` for minor user-facing additions.
- Use `type!:` or a `BREAKING CHANGE:` footer for major changes.
- Use `docs:`, `style:`, `refactor:`, `test:`, `build:`, `ci:`, or `chore:` for changes that should not create a user-facing stable bump unless breaking.
- Do not prefix commit subjects with `[codex]`.
- Keep the subject concise, imperative, and clear about the user impact.

Examples:

- `fix: reduce boss center overcorrection`
- `feat: add healer zone movement preference`
- `ci: validate release commit messages`
- `docs: clarify combat safety behavior`
- `chore: update plugin metadata`

## Release And Metadata

Direct commits to `main` are allowed for solo/agent work when appropriate; use pull requests when review or staging helps.

Testing builds are published only by manually running `.github/workflows/publish-testing.yml`. Testing releases use unique prerelease tags like `vX.Y.Z-testing.N` and may only update central feed testing fields:

- `TestingAssemblyVersion`
- `TestingDalamudApiLevel`
- `DownloadLinkTesting`

Testing and release feed updates require the `XELS_DALAMUD_FEED_TOKEN` Actions secret to be available to this repository. The token must have contents write access to `XelsPlugins/XelsDalamudRepo`.
Generated release notes belong on GitHub release and prerelease pages, not in `pluginmaster.json` changelog fields. The custom plugin feed should only carry version, API, and public download metadata.

Stable releases are published only by manually running `.github/workflows/release.yml`. Stable releases use immutable `vX.Y.Z` tags and may update central feed stable fields:

- `AssemblyVersion`
- `DownloadLinkInstall`
- `DownloadLinkUpdate`

Do not manually edit versions unless explicitly instructed. Do not use timestamp versions or CI run numbers as stable public versions. Do not publish to the official Dalamud repo.

The active custom feed is `XelsPlugins/XelsDalamudRepo`. Keep this repository listed in that repo's `repos.txt`. Do not add, update, or restore a local `pluginmaster.json`; feed entries are generated centrally.

When changing plugin description, tags, icon URL, name, or Dalamud API metadata, check `XelsCombatAI/XelsCombatAI.json` and the generated feed output.

The reusable release workflow uses `XelsPlugins/XelsDalamudRepo/scripts/package-plugin.py` and writes `artifacts/XelsCombatAI.zip`. Local packaging should use the same script through `scripts/test-and-build.sh --skip-tools --package`. Treat `artifacts/`, `bin/`, and `obj/` as generated output.

## External References And Generated Files

- Do not edit files under sibling reference checkouts, including `$XELS_WORKSPACE_DIR/XelsCombatAIReferences/BossmodReborn/`, `$XELS_WORKSPACE_DIR/XelsCombatAIReferences/ECommons/`, `$XELS_WORKSPACE_DIR/XelsCombatAIReferences/RotationSolverReborn/`, or `$XELS_WORKSPACE_DIR/XelsCombatAIReferences/VNavmesh/`.
- Do not commit or package external plugin source.
- Build references are cloned and refreshed by `scripts/update-build-dependencies.sh`.
- Reference checkouts should track the latest remote branch heads unless a task explicitly needs a release tag or historical commit.
- Agents may search sibling workspace folders for context, but should treat them as separate read-only repositories unless the user explicitly asks to change them.
- Do not hand-edit generated build output under `artifacts/`, `bin/`, or `obj/`.

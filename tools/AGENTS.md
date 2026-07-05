# AGENTS.md

## Scope

These instructions apply to everything under `tools/`.

The tools subtree contains local developer utilities. These projects are not runtime plugin features and must not be packaged with the Dalamud plugin unless explicitly requested.

Root repository instructions still apply here unless this file says otherwise. In particular, keep changes reviewable, verify APIs instead of guessing, and run the most relevant validation command before finishing.

## Tool Code Standards

- Tool code may depend on local analyzer and replay-parsing libraries, but must not introduce runtime plugin dependencies back into `XelsCombatAI/`.
- Keep CLI parsing, file I/O, replay normalization, detector logic, and report rendering separated enough that detector behavior can be tested without launching the game.
- Prefer pure C# detector and normalization logic with focused tests in `tools/FightReview.Tests`.
- Use `IPluginLog` rules only for runtime plugin code. Tool CLIs may write normal command output to the console.
- Do not put expensive analyzer work in the Dalamud plugin runtime path.

## FightReview Tool

- `tools/FightReview` is a local CLI that combines fresh XCAI schema v3 JSONL logs with BossMod Reborn replay logs.
- Fight review exists to find bad choices that caused danger, downtime, or unhuman-like behavior.
- Use the Final Fantasy XIV ABC premise: when BossMod-safe and job-feasible, the player should be casting or fighting. Unnecessary time out of range, idle, over-moving, or unable to act is a failure to investigate.
- Treat BMR safety pressure as required context, not as proof that an outcome was good. Preserve BossMod authority, but still flag avoidable downtime, edge hugging, jitter, or poor recovery that occurs during or after BMR-directed mechanics.
- Treat manual movement takeovers during combat as correction labels from the user. Do not blame automation for frames where manual suppression is active, but inspect the immediately preceding behavior to identify what likely forced intervention.
- Treat defensive ground zones as coverage areas, not mandatory center targets. Being inside the zone is usually sufficient; flag movement that pulls toward zone center when it causes boss-center, stuck, or uptime issues.
- Treat slow trailing during fluid trash pulls as separate from hard stuck movement. Compare player speed against observed normal run speed and visible party movement when available; flag pack/range movement that inches along while the party is moving normally.
- For trash pulls, evaluate AoE target choice, AoE positioning for hit count, and ABC uptime. Do not treat frontal arcs, positionals, or standing inside a trash hitbox as problems by themselves.
- Do not emit frontal-position as a standalone FightReview incident. Frontal placement can be supporting context only when tied to a supported failure such as manual correction, range loss, stuck movement, jitter, BMR conflict, or unsafe recovery.
- Treat single-target or ranged fallback GCDs during trash as late pack-engagement failures when the player is still outside useful pack range and BMR forced/forbidden safety pressure is not blocking entry.
- Treat indecisiveness, walking into walls or other zero-momentum pathing, and jittery target changes as review failures even when each individual destination was safe.
- Use vnavmesh query diagnostics as movement-path evidence. Prefer bounded per-frame facts such as reachable status, path detour, waypoint distance, off-mesh probes, and query stalls over copying or parsing cached navmesh files.
- Do not optimize uptime by bypassing BossMod safety or making movement look instant, perfect, or robotic. ABC goals must stay inside safe, human-plausible choices.
- If a recurring behavior or pattern needs a new user-facing config option because no current option covers it cleanly, call that out separately and ask the user before implementing the option. Do not silently add config surface as part of an analyzer-driven fix.
- Keep schema support focused on v3. Do not add schema v2 compatibility unless explicitly requested.
- Keep reports redacted by default. Raw IDs/OIDs can remain in normalized machine data for matching.
- Treat `$XCAI_REFERENCES_DIR/BossmodReborn`, defaulting to `$XELS_WORKSPACE_DIR/XelsCombatAIReferences/BossmodReborn`, as read-only. Use BMR public types such as `ReplayParserLog.Parse(...)` instead of copying upstream code.
- Keep analyzer logic out of `XelsCombatAI/Plugin.cs` and out of the runtime combat update path.
- Write generated review output outside the repo or under an ignored scratch path unless a fixture is intentionally being added.

## Environment

On Linux, analyzer builds need Dalamud dev assemblies:

```bash
export DALAMUD_HOME="$HOME/.xlcore/dalamud/Hooks/dev"
```

BMR replay parsing needs FFXIV game data. Prefer the local Steam install root:

```bash
export FFXIV_GAME_PATH="$HOME/Games/steam/debian-installation/steamapps/common/FINAL FANTASY XIV Online"
```

The analyzer accepts the install root containing `game/sqpack`, the `game` directory, or the `sqpack` directory itself.

## Validation

From the repository root:

```bash
DALAMUD_HOME="$HOME/.xlcore/dalamud/Hooks/dev" dotnet build tools/FightReview/FightReview.csproj -c Release -p:EnableWindowsTargeting=true
DALAMUD_HOME="$HOME/.xlcore/dalamud/Hooks/dev" FFXIV_GAME_PATH="$HOME/Games/steam/debian-installation/steamapps/common/FINAL FANTASY XIV Online" dotnet run --project tools/FightReview.Tests/FightReview.Tests.csproj -c Release
dotnet format tools/FightReview/FightReview.csproj --verify-no-changes
```

When touching both plugin logging and analyzer code, also run the plugin build required by the root `AGENTS.md`.

If local game data or Dalamud assemblies are unavailable, report the missing environment variable or path and run the build/test subset that can still execute.

## Test Fixtures

- Keep fixtures small, deterministic, and redacted.
- Use schema v3 JSONL fixtures for XCAI logs.
- Use the smallest BMR replay fixture that exercises `ReplayParserLog.Parse(...)`.
- Prefer adding focused detector fixtures over large captured fight logs.

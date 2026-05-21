---
name: xcai-devbridge
description: Use when working in the XelsCombatAI repo and live game state from XelsDevBridge would reduce guessing about combat runtime state, targets, objects, condition flags, addon/UI behavior, or safe in-game validation.
---

# XCAI DevBridge

Use this skill when a XelsCombatAI change needs live local game evidence in addition to source inspection.

## Workflow

1. Read root `AGENTS.md` first, especially product purpose, combat automation safety, and the XelsDevBridge section.
2. Preserve BossMod Reborn as the encounter-safety authority. Use DevBridge to observe or validate narrow behavior, not to bypass safety or broaden automation.
3. Discover the current bridge surface before assuming endpoint shapes:

   ```bash
   python /home/xeltor/.codex/skills/xels-tweaks-devbridge/scripts/devbridge.py health
   python /home/xeltor/.codex/skills/xels-tweaks-devbridge/scripts/devbridge.py capabilities
   python /home/xeltor/.codex/skills/xels-tweaks-devbridge/scripts/devbridge.py routes
   python /home/xeltor/.codex/skills/xels-tweaks-devbridge/scripts/devbridge.py actions
   ```

4. Prefer read-only observations first: `/v1/snapshot`, `/v1/conditions`, `/v1/objects`, `/v1/target`, `/v1/addons`, and `/v1/addon/{name}` when available.
5. Use mutation actions only for a specific low-risk experiment. Keep payloads minimal and mention the action name and payload summary in the final response.
6. Treat runtime observations as current-state evidence, not stable API contracts. Verify source/API contracts through `external/`, `third_party/`, installed assemblies, or build results.
7. If DevBridge is down or missing the needed route, continue from static code when reasonable and state the limitation.

## Environment

- Default base URL: `http://127.0.0.1:47777`.
- Overrides: `XELS_DEVBRIDGE_URL`, `XELS_DEVBRIDGE_TOKEN`, or `~/.config/xels-dev-bridge/connection.json`.
- Local source workspace: `../XelsDevBridge`.
- If the helper script is unavailable, use `curl -fsS "${XELS_DEVBRIDGE_URL:-http://127.0.0.1:47777}/v1/health"` and then `/v1/capabilities`.

## Validation

Run the relevant XCAI validation from `AGENTS.md` after code changes. For behavior changes, include the required Purpose fit note and any DevBridge observations used.

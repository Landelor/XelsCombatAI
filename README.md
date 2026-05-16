# Xel's Combat AI

A Dalamud plugin that automatically manages your BossMod Reborn movement and positioning settings during combat so you don't have to think about them.

## Requirements

- [BossMod Reborn](https://github.com/FFXIV-CombatReborn/BossmodReborn)
- [Avarice](https://puni.sh/api/repository/veyn) (for positional management)
- [RotationSolver Reborn](https://github.com/FFXIV-CombatReborn/RotationSolverReborn) (optional, required for Manage True North, AoE pack positioning, and Red Mage melee combo movement)

## What it does

While you are in combat, the plugin automatically:

- **Moves you to the correct positional** (rear/flank) based on your autorotation
- **Maintains target uptime** using internal melee/ranged action-range behavior, including stable mechanic-exit nudges when a safe exit can still hit the target
- **Manages your Ley Lines** — returns to them when safe, uses Between the Lines and Retrace if available
- **Can move Red Mage for its enchanted melee combo** — an optional job-specific setting uses RDM mana state and RSR target context to move in for safe enchanted melee range, then prefers a safe Displacement out after the finisher
- **Stays clear of forbidden zones** with a configurable buffer distance
- **Uses BossMod-checked gap closers** to re-engage after being knocked away or escape a dangerous spot (optional, off by default). Greedy movement timing can also use a tightly guarded emergency target dash when you are already in danger.
- **Moves for better AoE hits** when your next AoE can hit more enemies from another spot
- **Picks better AoE and trash targets** when target choice affects how many enemies you hit
- **Moves closer to trash packs** when you are too far away to hit them well
- **Follows tank-led trash pulls** when you fall behind the moving pack, with party-route fallback when the tank path is unavailable
- **Keeps healers in comfortable party-healing range** by pre-positioning toward safe spots that preserve more AoE heal coverage
- **Avoids awkward boss-center positions** and small enemy hitboxes when choosing movement goals
- **Prefers helpful defensive ground effects** such as Asylum, Sacred Soil, Earthly Star, and Collective Unconscious
- **Prefers Paladin Passage of Arms protection** by letting BossMod prefer the protected cone behind a party Paladin while the buff is active
- **Brings stray aggro to a party tank** when a non-tank is targeted by a mob for more than 3 seconds
- **Avoids hugging the arena edge** as a weak preference when stronger movement goals do not matter
- **Shows a decision overlay** with projected in-world markers for current movement decisions, candidates, and debug context, plus an optional movable debug HUD (off by default)
- **Can write opt-in run-review logs** for offline analysis with BossMod Reborn replay files (off by default)
- **Pauses automated movement** briefly when you move manually, including remapped movement or gamepad input reported by BossMod
- **Manages True North** usage and disables RSR's Auto True North to prevent conflicts (optional, requires RSR)

Out of combat, the plugin stops managing movement entirely and hands control back to you. Settings are automatically re-applied after death and resurrection.

## Installation

Add the following URL to Dalamud's custom plugin repositories:

```
https://raw.githubusercontent.com/XelsPlugins/XelsDalamudRepo/main/pluginmaster.json
```

Stable builds are published manually. Testing builds are generated from PR previews and require Dalamud's plugin testing versions option.

## Commands

| Command | Description |
|---|---|
| `/xcai` | Toggle the plugin on/off |
| `/xcai on` | Enable |
| `/xcai off` | Disable |
| `/xcai config` | Open settings |
| `/xcai logs on` | Enable run-review logging |
| `/xcai logs off` | Disable run-review logging |
| `/xcai logs status` | Show whether run-review logging is enabled and where logs are written |

## Configuration

Open the settings window with `/xcai config` or through the Dalamud plugin list. The window is split by intent so related options stay together.

### General tab

**General** — Toggle the plugin, command chat messages, and reset settings.

### Movement tab

**Movement** — Control automatic movement, social facing during downtime, manual movement pause, movement timing, attack range, danger-zone spacing, healer party coverage, defensive ground effects, Passage of Arms, aggro safety, unknown-boss vnavmesh reachability guarding, and weak edge-avoidance preferences.

**Follow party facing during downtime** turns roughly toward nearby party members when the target is gone or BossMod reports downtime, without changing facing during manual input, casting, animation lock, or BossMod movement pressure.

### AoE & Trash tab

**AoE & Trash** — Move for better AoE hits, follow tank-led trash pulls with party-route fallback when you fall behind, pick better AoE targets, keep a trash target selected, move closer to trash packs, and avoid standing inside enemies.

### Positionals tab

**Positionals** — Move to the correct rear/flank position and optionally use True North when the correct side cannot be reached.

### Job Specific tab

**Black Mage** — Stay in existing Ley Lines and choose whether to use Between the Lines, Retrace, or walking to return to them.

**Red Mage** — Optionally move for the enchanted melee combo. Boss/single-target behavior waits for `50 Black Mana / 50 White Mana`, stays ranged during Manafication's extended-range sword combo, and moves into melee range when the RDM gauge state says Enchanted Riposte or a melee continuation is needed. Trash AoE behavior uses RSR target context, moves close enough for Enchanted Moulinet's cone when there are 3+ affected targets and the combo is available, and stays in cone range through Moulinet continuations. After Enchanted Redoublement or Enchanted Moulinet Trois lands, it prefers a safe Displacement out; if Displacement or its facing setup is unsafe, unavailable, or blocked by navigation checks, it stays close unless BossMod moves you for safety.

### Dashes tab

**Dashes** — Optional gap-closer automation for reaching safety faster, returning to a target, or recovering after forced movement. This option is off by default and can very likely kill you in some fights. A single job allow-list covers enemy-target dashes, ally-target dashes, location dashes, forward dashes, backsteps, and return anchors.

Fixed-direction dashes can make a short setup turn when that turn is required for a safe, useful dash. Greedy movement timing also gives safe dash choices a style pass, preferring better ally anchors, precision Shukuchi landings, paired out-and-back returns, knockback recovery dashes, capped-charge spends, trash-pack dash anchors, and cleaner fixed-direction dash angles while still requiring BossMod-safe landings.

During confident multi-target trash pulls, re-engage gap closers are conserved unless the target or trash-pull destination is close to falling out of dash range. If a pack dash chooses a different attackable target, that target is selected before the dash so movement does not immediately walk back to another enemy. Safety dashes are still evaluated separately.

Ninja gap closers and managed True North are held while Mudra, Ten Chi Jin, or Three Mudra is active to avoid interrupting ninjutsu resolution.

When the Gap closers option is enabled, safe-position escape dashes follow Movement timing. Safe first walks if the current spot is safe. Greedy can assist BossMod movement earlier, Greedy until next GCD waits for BossMod's movement timing to become short, and Last second only assists when BossMod reports urgent movement. Some jobs can use an emergency target dash such as Gyoten, Winged Glide, Slither, or Thunderclap even if the landing is still inside the current danger zone. This only applies when you are already in danger, BossMod has a confirmed safe movement direction, the dash meaningfully shortens that escape path, and vnavmesh confirms the landing is reachable ground without a large vertical drop. Normal movement timing never intentionally lands a safety dash in danger.

### Troubleshooting tab

**Troubleshooting** — Show the movement overlay, toggle the movable debug HUD, copy a debug snapshot, or enable run-review logging.

Run-review logging is off by default. When enabled, the plugin writes one detailed JSONL file for the current duty, matching BossMod Reborn's whole-replay style so dungeon pulls can be compared in one analyzer run. If no duty is active, it falls back to a single combat log. Files are written to the plugin config directory under `XelsCombatAI/combat-logs`. Combat is sampled at the normal review cadence and downtime is sampled slower to keep resource cost bounded. Movement review data includes BossMod movement, goal-zone, and safety-raster diagnostics; it no longer emits the removed movement-intent planner candidate model.

Use `/xcai logs on` before a run to enable capture quickly. Logging remains active while movement control is disabled with `/xcai off`, so erratic behavior can be reviewed without allowing automated movement. Each successful write is also recorded in the plugin log with the JSONL path, frame count, and duration.

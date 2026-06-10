# Xel's Combat AI

A Dalamud plugin that automatically manages your BossMod Reborn movement and positioning settings during combat so you don't have to think about them.

## Requirements

- [BossMod Reborn](https://github.com/FFXIV-CombatReborn/BossmodReborn)
- [RotationSolver Reborn](https://github.com/FFXIV-CombatReborn/RotationSolverReborn) (optional, used for primary positional intent and required for Manage True North, AoE pack positioning, and Red Mage melee combo movement)

## What it does

While you are in combat, the plugin automatically:

- **Moves you to the correct positional** (rear/flank) based on your autorotation
- **Manages your Ley Lines** — returns to them when safe, uses Between the Lines and Retrace if available
- **Can move Red Mage for its enchanted melee combo** — an optional job-specific setting uses RDM mana state and RSR target context to move in for safe enchanted melee range, then prefers a safe Displacement out after the finisher
- **Stays clear of forbidden zones** with a configurable buffer distance
- **Uses BossMod-checked gap closers** to re-engage after being knocked away or escape a dangerous spot (optional, off by default). Greedy movement timing can also use a tightly guarded emergency target dash when you are already in danger.
- **Moves for better AoE hits** when your next AoE can hit more enemies from another spot, while yielding to active BossMod mechanic safety
- **Picks better AoE and trash targets** when target choice affects how many enemies you hit
- **Can help tanks recover trash aggro** by selecting nearby enemies that peel to party members, or by using ranged attacks/Provoke without walking to them
- **Can adjust tank behavior for persistent front cleaves** by ignoring BossMod cone movement and preferring tank spots that keep the cleave away from the visible party
- **Keeps party members in healer range** by pre-positioning healers toward safe spots that preserve AoE heal coverage, and by moving DPS into a visible healer's AoE healing range before raidwide or shared raid damage. Tanks keep their tanking position.
- **Avoids awkward boss-center positions** and small enemy hitboxes when choosing movement goals
- **Prefers helpful defensive ground effects for non-tanks** such as Asylum, Sacred Soil, Earthly Star, and Collective Unconscious when raid damage, shared damage, heavy personal damage, or low health makes them useful. Tanks keep their tanking position instead of moving for healing zones
- **Prefers Paladin Passage of Arms protection** by letting BossMod prefer the protected cone behind a party Paladin while the buff is active
- **Avoids hugging the arena edge** as a weak preference when stronger movement goals do not matter
- **Avoids pixel-perfect player stacks** by preferring a tiny safe offset from visible player party members, while staying out of intentional party clumps during mechanics
- **Shows a decision overlay** with projected in-world markers for current movement decisions and candidates (off by default)
- **Can write opt-in run-review logs** for offline analysis with BossMod Reborn replay files (off by default)
- **Pauses automated movement** briefly when you move manually, including remapped movement or gamepad input reported by BossMod, and briefly lowers the same advisory movement preference if your input looks like a correction
- **Manages True North** usage and disables RSR's Auto True North to prevent conflicts (optional, requires RSR)

Out of combat, the plugin stops managing movement entirely and hands control back to you. Settings are automatically re-applied after death and resurrection.

## Installation

Add the following URL to Dalamud's custom plugin repositories:

```
https://raw.githubusercontent.com/XelsPlugins/XelsDalamudRepo/main/pluginmaster.json
```

Stable and testing builds are published manually. Testing builds require Dalamud's plugin testing versions option.

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

**General** — Toggle the plugin, party intent discovery, command chat messages, and reset settings.

**Party intent discovery** is on by default and announces blinded party-context availability to the hosted party-intent server. The server endpoint is fixed by the plugin, and missing or unreachable servers are ignored without changing movement behavior.

When another party member is also connected, cooperative social destack uses a direct peer data channel after server rendezvous. It shares only short-lived blinded destack bias, and only as a weak local scoring hint when social spacing is already active.

When party intent is connected, a player who cannot reach BossMod-safe ground in time may send a short Rescue SOS. Healer clients recheck local safety, range, and Rescue availability before showing an advisory in the movement overlay. **Auto Rescue SOS** is off by default; when enabled by a healer, it may automatically interrupt the healer's current cast and use Rescue only after winning the SOS claim and rechecking local safety, visibility, range, animation lock, and Rescue availability.

### Movement tab

**Movement** — Control automatic movement, social facing and spacing, manual movement pause, auto-face behavior during manual movement, movement timing, danger-zone spacing, healer party coverage, defensive ground effects, Passage of Arms, and weak edge-avoidance preferences.

While you are casting, advisory movement goals are suppressed so comfort, uptime, AoE, party utility, and style preferences do not interrupt casts. Magic-ranged and healer jobs may still use the final slidecast window for those small adjustments. BossMod Reborn's own mechanic movement still applies, and the plugin may still add a raw mechanic-exit margin when BossMod is already moving you out.

**Disable auto-face when I move** turns off the game's Auto-face target option while movement input is active, then restores your previous value after manual movement ends.

**Follow party facing during downtime** turns roughly toward nearby party members when the target is gone or BossMod reports downtime, without changing facing during manual input, casting, animation lock, or BossMod movement pressure.

**Avoid exact player stacks** lightly prefers a nearby safe offset when you are almost exactly overlapping a visible player party member. During mechanics, it waits until BossMod reports your current position safe, yields to active BossMod movement, and avoids breaking up intentional party clumps.

### AoE & Trash tab

**AoE & Trash** — Move for better AoE hits, pick better AoE targets, keep a trash target selected, and avoid standing inside enemies.

### Positionals tab

**Positionals** — Move to the correct rear/flank position and optionally use True North when the correct side cannot be reached.

### Job Specific tab

**Black Mage** — Stay in existing Ley Lines and choose whether to use Between the Lines, Retrace, or walking to return to them.

**Pictomancer** — Stay in your existing Starry Muse ground effect when safe. When Gap closers and the PCT dash allow-list are enabled, Smudge can be used as a return tool only when walking cannot reasonably get back within the current GCD, the fixed 15y landing ends inside the circle, BossMod accepts the dash as safe, and navigation confirms the landing is reachable.

**Red Mage** — Optionally move for the enchanted melee combo. Boss/single-target behavior waits for `50 Black Mana / 50 White Mana`, stays ranged during Manafication's extended-range sword combo, and moves into melee range when the RDM gauge state says Enchanted Riposte or a melee continuation is needed. Trash AoE behavior uses RSR target context, moves close enough for Enchanted Moulinet's cone when there are 3+ affected targets and the combo is available, and stays in cone range through Moulinet continuations. After Enchanted Redoublement or Enchanted Moulinet Trois lands, it prefers a safe Displacement out; if Displacement or its facing setup is unsafe, unavailable, or blocked by navigation checks, it stays close unless BossMod moves you for safety.

**Tanks** — Optional tank controls can ignore BossMod Reborn movement from persistent hollow cleave cones on the current target, prefer safe tanking spots that point front cleaves and cone tankbusters at the fewest visible party members, select nearby trash enemies that are attacking party members, use ranged attacks or Provoke on peeled trash outside melee range without moving, and coordinate stance in BossMod encounters by dropping it for a visible party co-tank or restoring it when no visible party tank has stance. These options are off by default.

### Dashes tab

**Dashes** — Optional gap-closer automation for reaching safety faster, returning to a target, or recovering after forced movement. This option is off by default and can very likely kill you in some fights. A single job allow-list covers enemy-target dashes, ally-target dashes, location dashes, forward dashes, backsteps, and return anchors.

Fixed-direction dashes can make a short setup turn when that turn is required for a safe, useful dash. Greedy movement timing also gives safe dash choices a style pass, preferring better ally anchors, precision Shukuchi landings, paired out-and-back returns, knockback recovery dashes, capped-charge spends, trash-pack dash anchors, and cleaner fixed-direction dash angles while still requiring BossMod-safe landings. In BossMod-known boss fights, greedy timing may also spend a safe re-engage dash from normal job range when it saves the configured minimum movement distance.

Phantom duty dashes have a separate opt-in checkbox. When enabled, Phantom dash actions follow the current job's dash-type rules and archetype: Phantom Kick is treated as a 15y target dash, and Occult Featherfoot is treated as a fixed-forward dash. Phantom Kick can be used as a safety target dash when it passes the same BossMod safe-movement progress checks as other emergency target dashes, and close-range AoE jobs can use it to move into trash packs for AoE damage. If a Phantom action and native dash both satisfy the same movement purpose, the Phantom action is tried first because it may carry useful side effects. Ranged/healer boss re-engage reservations are not overridden. Jobs with native dashes use their job allow-list entry; jobs without native dashes use their range archetype. The normal Gap closers option still applies.

During confident multi-target trash pulls, re-engage gap closers are conserved unless the target or trash-pull destination is close to falling out of dash range. If a pack dash chooses a different attackable target, that target is selected before the dash so movement does not immediately walk back to another enemy. Safety dashes are still evaluated separately.

Ninja gap closers and managed True North are held while Mudra, Ten Chi Jin, or Three Mudra is active to avoid interrupting ninjutsu resolution.

When the Gap closers option is enabled, safe-position escape dashes follow Movement timing. Safe first usually walks if the current spot is safe, but can assist far BossMod safe-zone movement when walking time is close to the reported mechanic timing. Greedy can assist BossMod movement earlier, Greedy until next GCD waits for BossMod's movement timing to become short, and Last second only assists when BossMod reports urgent movement. Some jobs can use an emergency target dash such as Gyoten, Winged Glide, Slither, or Thunderclap even if the landing is still inside the current danger zone. This only applies when you are already in danger, BossMod has a confirmed safe movement direction, the dash meaningfully shortens that escape path, and vnavmesh confirms the landing is reachable ground without a large vertical drop. Normal movement timing never intentionally lands a safety dash in danger.

### Troubleshooting tab

**Troubleshooting** — Show the movement overlay, copy a debug snapshot, or enable run-review logging.

Run-review logging is off by default. When enabled, the plugin writes one detailed JSONL file for the current duty, matching BossMod Reborn's whole-replay style so dungeon pulls can be compared in one analyzer run. If no duty is active, it falls back to a single combat log. Files are written to the plugin config directory under `XelsCombatAI/combat-logs`. Combat is sampled at the normal review cadence and downtime is sampled slower to keep resource cost bounded. Movement review data includes BossMod movement, goal-zone, and safety-raster diagnostics; it no longer emits the removed movement-intent planner candidate model.

Use `/xcai logs on` before a run to enable capture quickly. Logging remains active while movement control is disabled with `/xcai off`, so erratic behavior can be reviewed without allowing automated movement. Each successful write is also recorded in the plugin log with the JSONL path, frame count, and duration.

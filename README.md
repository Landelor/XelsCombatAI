# Xel's Combat AI

A Dalamud plugin that automatically manages your BossMod Reborn movement and positioning settings during combat so you don't have to think about them.

## Requirements

- [BossMod Reborn](https://github.com/FFXIV-CombatReborn/BossmodReborn)
- [Avarice](https://puni.sh/api/repository/veyn) (for positional management)
- [RotationSolver Reborn](https://github.com/FFXIV-CombatReborn/RotationSolverReborn) (optional, required for Manage True North and AoE pack positioning)

## What it does

While you are in combat, the plugin automatically:

- **Moves you to the correct positional** (rear/flank) based on your autorotation
- **Maintains target uptime** using internal melee/ranged action-range behavior
- **Manages your Ley Lines** — returns to them when safe, uses Between the Lines and Retrace if available
- **Stays clear of forbidden zones** with a configurable buffer distance
- **Uses BossMod-checked gap closers** to re-engage after being knocked away or escape a dangerous spot (optional, off by default)
- **Moves for better AoE hits** when your next AoE can hit more enemies from another spot
- **Picks better AoE and trash targets** when target choice affects how many enemies you hit
- **Moves closer to trash packs** when you are too far away to hit them well
- **Avoids awkward boss-center positions** and small enemy hitboxes when choosing movement goals
- **Prefers the party cluster** by letting BossMod gently gravitate toward your current 4-player or 8-player party during combat, while magic ranged jobs avoid party-gravity movement when they can keep casting at a target
- **Prefers helpful defensive ground effects** such as Asylum, Sacred Soil, Earthly Star, and Collective Unconscious
- **Prefers Paladin Passage of Arms protection** by letting BossMod prefer the protected cone behind a party Paladin while the buff is active
- **Avoids hugging the arena edge** as a weak preference when stronger movement goals do not matter
- **Shows a decision overlay** with projected in-world markers for current movement decisions and candidates (optional, off by default)
- **Pauses automated movement** briefly when you move manually, including remapped movement or gamepad input reported by BossMod
- **Manages True North** usage and disables RSR's Auto True North to prevent conflicts (optional, requires RSR)

Out of combat, the plugin stops managing movement entirely and hands control back to you. Settings are automatically re-applied after death and resurrection.

## Installation

Add the following URL to Dalamud's custom plugin repositories:

```
https://raw.githubusercontent.com/Xeltor/XelsCombatAI/master/pluginmaster.json
```

## Commands

| Command | Description |
|---|---|
| `/xcai` | Toggle the plugin on/off |
| `/xcai on` | Enable |
| `/xcai off` | Disable |
| `/xcai config` | Open settings |

## Configuration

Open the settings window with `/xcai config` or through the Dalamud plugin list. The window is split by intent so related options stay together.

### General tab

**General** — Toggle the plugin, command chat messages, and reset settings.

### Movement tab

**Movement** — Control automatic movement, manual movement pause, movement timing, attack range, danger-zone spacing, party positioning, defensive ground effects, Passage of Arms, and weak edge-avoidance preferences.

### AoE & Trash tab

**AoE & Trash** — Move for better AoE hits, pick better AoE targets, keep a trash target selected, move closer to trash packs, and avoid standing inside enemies.

### Positionals tab

**Positionals** — Move to the correct rear/flank position and optionally use True North when the correct side cannot be reached.

### Black Mage tab

**Black Mage** — Stay in existing Ley Lines and choose whether to use Between the Lines, Retrace, or walking to return to them.

### Dashes tab

**Dashes** — Optional dash automation for returning to a target or reaching safety faster. These options are off by default and can very likely kill you in some fights. Each dash type has its own minimum distance and job allow-list.

### Troubleshooting tab

**Troubleshooting** — Show the movement overlay, copy a debug snapshot, or copy recent combat decisions.

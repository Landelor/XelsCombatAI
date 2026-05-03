# Xel's Combat AI

A Dalamud plugin that automatically manages your BossMod Reborn movement and positioning settings during combat so you don't have to think about them.

## Requirements

- [BossMod Reborn](https://github.com/FFXIV-CombatReborn/BossmodReborn)
- [Avarice](https://puni.sh/api/repository/veyn) (for positional management)
- [RotationSolver Reborn](https://github.com/FFXIV-CombatReborn/RotationSolverReborn) (optional, required for Manage True North)

## What it does

While you are in combat, the plugin automatically:

- **Moves you to the correct positional** (rear/flank) based on your autorotation
- **Keeps you at the right distance** from your target based on your role
- **Switches to AoE distance** when there are multiple enemies nearby
- **Stays close to a tank** when your target doesn't have a boss module
- **Manages your Ley Lines** — returns to them when safe, uses Between the Lines and Retrace if available
- **Stays clear of forbidden zones** with a configurable buffer distance
- **Uses BossMod-managed gap closers** to re-engage after being knocked away (optional, off by default)
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
| `/xcai status` | Print current state to chat |

## Configuration

Open the settings window with `/xcai config` or through the Dalamud plugin list. The window is split into two tabs.

### General tab

**Movement** — Toggle movement management and whether to follow the tank on trash pulls (targets with no boss module).

**Positioning** — Manage positionals, True North, Ley Lines, and gap closers.

- *Manage positionals* — Moves you to the correct rear/flank position for your rotation.
- *Manage True North* — Uses True North automatically and disables RSR's Auto True North via IPC to prevent conflicts. Requires RSR.
- *Manage Ley Lines* — Helps BLM stay on Ley Lines and use Between the Lines / Retrace when available. Does not place Ley Lines.
- *Use BMR-managed gap closer to re-engage* — Lets BossMod queue supported utility gap closers with its dash safety checks. Disabled by default. Supported actions are MNK Thunderclap, DRG Winged Glide, NIN Shukuchi, and VPR Slither.

**Combat Behavior** — Choose between *Normal* (BossMod moves directly to its destination) and *Greed* (BossMod balances uptime against mechanic safety).

**Feedback** — Toggle whether enable/disable commands are echoed to chat.

### Ranges tab

**Manage range** — Master toggle for all distance management.

**Single target distance** — Set your preferred max distance per role (melee, physical ranged, healer, magic ranged). Disable to stop managing single-target distance.

**AoE target distance** — When multiple enemies are nearby the target, the plugin switches to these distances instead. The threshold controls how many enemies must be present to trigger AoE mode. You can also enable WHM/SCH/SGE to use melee AoE distance so they stay in range of their ground targets.

**Forbidden zone** — Keeps you a set distance back from forbidden zones to avoid clipping into them.

Use **Reset ranges** to restore all distance values to defaults, or **Reset all** to restore the full configuration.

# Xel's Combat AI

A Dalamud plugin that automatically manages your BossMod Reborn movement and positioning settings during combat so you don't have to think about them.

## Requirements

- [BossMod Reborn](https://github.com/FFXIV-CombatReborn/BossmodReborn)
- [Avarice](https://github.com/ToxicStar8/Avarice) (for positional management)

## What it does

While you are in combat, the plugin automatically:

- **Moves you to the correct positional** (rear/flank) based on your autorotation
- **Keeps you at the right distance** from your target based on your role
- **Switches to AoE distance** when there are multiple enemies nearby
- **Stays close to a tank** when your target doesn't have a boss module
- **Manages your Ley Lines** — returns to them when safe, uses Between the Lines and Retrace if available
- **Stays clear of forbidden zones** with a configurable buffer distance

Out of combat, the plugin stops managing movement entirely and hands control back to you.

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

Open the settings window with `/xcai config` or through the Dalamud plugin list.

**Single target distance** — Set your preferred max distance per role (melee, physical ranged, healer, magic ranged). If disabled, all roles use the melee distance.

**AoE target distance** — When multiple enemies are nearby, the plugin switches to these distances instead. You can also set WHM/SCH/SGE to use melee distance during AoE so they stay in range of their ground targets.

**Manage forbidden-zone distance** — Keeps you a set distance back from forbidden zones to avoid clipping into them.

**Manage Ley Lines** — Helps you stay on your Ley Lines and use Between the Lines / Retrace when available. Does not place Ley Lines for you.

Use **Reset ranges** to restore all distance values to defaults, or **Reset all** to restore the full configuration.

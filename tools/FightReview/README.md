# XCAI Fight Review

Local developer CLI for analyzing schema v3 XCAI run-review JSONL logs.

The review goal is to find bad movement or targeting choices that caused danger, downtime, or unhuman-like behavior across a whole duty run when available. The analyzer treats ABC, Always Be Casting, as the baseline: when BossMod-safe and job-feasible, unnecessary time unable to cast or fight is a failure to investigate. Indecisive safe-zone bouncing, walking into walls with zero momentum, and jittery movement retargeting are failures even if every individual target point was technically safe.

## Usage

```bash
dotnet run --project tools/FightReview -- \
  --xcai /path/to/xcai.jsonl
```

If `--out` is omitted, output is written beside the XCAI log in a folder named `<xcai-log-name>-review`.

Output is `agent.improvement.json`, an AI-agent-readable packet with run scores, uptime signals, incidents, compact frame slices, route segments, code areas, and test focus. Higher scores are better; uptime is the primary positive signal.

The uptime score uses logged target range as the RSR proxy: if the player is in useful range, RSR has more legal rotation choices. Melee and tanks treat ranged fallback as a missed melee uptime window: it shows the player avoided total inactivity, but it does not score as successful melee uptime. Trash pulls score pack hit quality so positions that hit more targets are rewarded. Healers score target uptime together with visible party coverage. BMR pressure from the XCAI log remains safety context: Normal profile movement away from mechanics is not automatically bad, while Greed profiles are rewarded for staying useful until BMR actually requires movement.

## Requirements

No BossMod replay log or FFXIV game data is required. The XCAI JSONL is the source of truth for review input.

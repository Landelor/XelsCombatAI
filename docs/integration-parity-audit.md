# XCAI Integration Parity Audit

This audit tracks the remaining work to move XCAI away from reflection while preserving behavior parity. Avarice shared data has been removed.

Status meanings:

- `Done`: XCAI already prefers stable IPC or local state and keeps fallback behavior.
- `Measurable`: XCAI logs enough source data to prove runtime parity, but live logs still need to show the fallback is unused.
- `Blocked upstream`: current BMR or RSR does not expose the required data through stable IPC.
- `Do not use`: an apparent IPC exists, but using it would create stale feedback or lose semantics.

## BossMod Reborn

Current BMR checkout inspected: sibling reference checkout `BossmodReborn` commit `ae39a1e40d9900d094f3799f9ea7499a665f8461`.

| Decision area | Current XCAI primary source | Remaining fallback | Status | Evidence / next step |
| --- | --- | --- | --- | --- |
| Dash destination safety | `BossMod.Hints.IsDashSafe`, `Hints.IsFixedDashSafe`, `Hints.IsBackdashSafe` | none | Done | Mobility dash safety no longer falls back to reflected `ActionDefinitions.IsDashDangerous`; if IPC cannot answer, the dash is treated as unsafe/unknown. |
| Position safety | `BossMod.Hints.IsPositionSafe` | none | Done | Mobility position validation no longer falls back to reflected `ActionDefinitions.IsDashDangerous(from=to)`; if IPC cannot answer, safety is unknown. |
| Special movement modes | `BossMod.Hints.SpecialModeIn`, `Hints.SpecialModeType` | reflected `AIHints.ImminentSpecialMode` | Done, measurable | Mechanic pressure and facing source summaries identify IPC vs reflection fallback. |
| Typed mechanic pressure | BMR timeline and hint IPC: raidwide, tankbuster, knockback, downtime, vulnerable, damage type, special mode | none for the exposed timer fields | Done | Holds report pyretic/no-move/freezing/misdirection/knockback/raidwide/tankbuster/shared/damage/downtime/vulnerable instead of generic damage. |
| Active module and timeline diagnostics | `BossMod.HasActiveModule`, `BossMod.ActiveModuleName`, `BossMod.Debug.TimelineWalk` | none | Done | Runtime/FightReview logs include module, zone module, pressure summary, and timeline walk summary. |
| Facing passive danger pressure | `BossMod.Hints.ForbiddenZonesCount`, `Hints.ForbiddenDirectionsCount` | reflected hint counts | Done, measurable | Current BMR exposes these endpoints; XCAI should now classify these as `BMR IPC` before reflected fallback. |
| BossMod navigation target | `BossMod.AI.NaviTargetPos` | reflected controller/navigation fields and forced movement vector | Partial | IPC gives target position only. Reflection is still required for next waypoint, desired/actual movement direction, forced movement vector, and navigation map/raster details. |
| Goal-zone contributors | none | `BossModGoalZoneHook` reflection | Blocked upstream | AoE pack, healer coverage, Passage of Arms, survivability zones, arena edge, boss-center avoidance, and social spacing need structured goal-zone/contributor IPC. Current BMR IPC does not expose goal-zone candidates, priorities, contributor identity, or scoring details. |
| Navigation map / line checks | none | `BossModReflectionSafety.TryCheckNavigationLine` and pathfind-bound reflection | Blocked upstream | Current BMR IPC exposes obstacle map generation/status, not the active normal-movement map cells or line-evaluation details XCAI logs and reviews. |
| BMR recommended positional | `BossMod.Hints.RecommendedPositional` exists | RSR reflected positional source | Do not use | XCAI writes `GoToPositional`, which also sets `Hints.RecommendedPositional`; reading it as an input risks echoing stale XCAI output as if it were independent BMR/RSR intent. |

## RotationSolver Reborn

Current RSR external checkout exposes `RotationSolverReborn.ActionUpdater.NextGCDActionChanged`, which carries only adjusted action id. XCAI does not use that event for positional intent because it was unusable in parity logs and lacks target/timing data.

| Decision area | Current XCAI primary source | Remaining fallback | Status | Evidence / next step |
| --- | --- | --- | --- | --- |
| Positional intent | reflected `ActionUpdater.NextGCDAction` plus XCAI positional action map | none | Done with current constraints | Reflection gives target, adjusted id, action id, and timing. The adjusted-id-only IPC event is deliberately not used for positionals. |
| True North walk-vs-use decision | reflected next-GCD timing and target id | local rejection when timing unavailable | Partial, measurable | Runtime and FightReview now log `TrueNorthDecisionSource` and reason separately from positional intent. |
| AoE action geometry | reflected next-GCD action target/preview target/target info/action sheet data | local fallback | Blocked upstream | Needs stable IPC for next GCD action id, adjusted id, target id, preview target, target info, affected target count or enough fields to calculate it. |
| Target uptime range | reflected next-GCD action timing/source | local job range fallback | Partial, measurable | FightReview source summary tracks RSR reflected/local/none target-uptime frames. |
| Red Mage melee combo movement | reflected current rotation/action data | RSR IPC next-GCD event can only identify adjusted action id | Blocked upstream for full parity | Needs current rotation name/mode or explicit RDM melee intent IPC, plus next action target/timing. |
| RSR reflection isolation | `RotationSolverActionReflection` adapter only | none outside adapter | Done | Reflection surface is centralized behind one adapter with diagnostics and an explicit `NeedsIPC=...` migration contract. |

Required upstream RSR IPC to finish reflection removal:

- Next GCD action id and adjusted id.
- Next GCD primary target object id, position, and hitbox radius.
- Preview target object id, position, and hitbox radius.
- Target info fields needed to distinguish target-area, target-centered circle, friendly target, cast type, effect range, and x-axis modifier.
- Affected target count or enough stable target-list data to reproduce it.
- GCD remain, elapsed, total, and calculated action-ahead.
- Current rotation identity and movement/combat state used by RDM melee logic.

## Avarice

Removed. Positional intent now uses reflected RSR next-GCD data only. If reflected RSR cannot identify a positional GCD, XCAI falls back to no positional intent instead of reading Avarice shared data.

## Completion Criteria Remaining

The goal is not complete until all of the following are true:

1. FightReview logs from real combat show mobility and facing safety source summaries dominated by `BMR IPC`, with `BMR reflection fallback` only where current BMR IPC has no exposed data.
2. RSR reflection cannot be removed until upstream IPC exposes the next-action/timing/target payload listed above, or XCAI deliberately drops the dependent behavior.
3. BMR goal-zone reflection cannot be removed until upstream IPC exposes structured goal-zone/contributor data, or XCAI deliberately drops those goal contributors.
4. Any fallback removal must be backed by FightReview source summaries and in-game parity logs, not just passing builds.

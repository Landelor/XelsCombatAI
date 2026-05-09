using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq.Expressions;
using System.Numerics;
using System.Reflection;
using System.Linq;
using Dalamud.Game.ClientState.Conditions;
using ECommons.GameFunctions;
using Dalamud.Game.ClientState.Objects.Enums;
using Dalamud.Game.ClientState.Objects.Types;
using XelsCombatAI.Game;
using XelsCombatAI.Integrations;

namespace XelsCombatAI.Combat;

internal sealed record AoePackPositioningStatus(
    string HookState,
    string LastReason,
    string RsrStatus,
    string RsrReflectionDiagnostics,
    uint ActionId,
    string ActionName,
    string Shape,
    int CurrentHits,
    int BestHits,
    bool Injected,
    bool RsrHenchedActive,
    StateCommandType RsrSnapshotMode,
    string RsrLastRestoreStatus,
    int PriorityTargetCount);

internal sealed record AoePackOverlaySnapshot(
    uint ActionId,
    string ActionName,
    string Shape,
    Vector3 Candidate,
    Vector3 PrimaryTarget,
    float Radius,
    float HalfWidth,
    int CurrentHits,
    int BestHits,
    IReadOnlyList<AoePackOverlayTarget> Targets);

internal sealed record AoePackOverlayTarget(Vector3 Position, float Radius, bool Hit, bool InsideAvoidedHitbox = false, string? AvoidanceLabel = null);

internal sealed class AoePackPositioningController(
    Configuration config,
    DalamudServices services,
    RotationSolverActionReflection rotationSolverActions,
    Func<bool> automatedMovementSuppressed,
    RotationSolverIpc rotationSolverIpc,
    Func<bool> currentTargetHasBossModule,
    JobRangeProvider jobRangeProvider)
    : IBossModGoalZoneContributor
{
    private FieldInfo? goalZonesField;
    private PropertyInfo? priorityTargetsProperty;
    private FieldInfo? enemyActorField;
    private MemberInfo? actorPositionField;
    private MemberInfo? actorHitboxRadiusField;
    private MemberInfo? actorInstanceIdField;
    private FieldInfo? wposXField;
    private FieldInfo? wposZField;
    private Type? resolvedHintsType;
    private Type? resolvedWPosType;
    private string hookState = "unresolved";
    private string lastReason = "not evaluated";
    private string rsrRestoreStatus = "not active";
    private string rsrLastRestoreStatus = "not active";
    private RsrAoeActionSnapshot? lastAction;
    private AoePackOverlaySnapshot? lastOverlay;
    private AoePackOverlaySnapshot? lastSuggestion;
    private int lastCurrentHits;
    private int lastBestHits;
    private bool lastInjected;
    private ulong lastBestPrimaryId;
    private bool rsrHenchedActive;
    private StateCommandType rsrSnapshotMode;
    private int lastPriorityTargetCount;
    private Vector2 lastInjectedCandidate;
    private int lastInjectedHits;
    private Delegate? lastInjectedGoalDelegate;
    private Vector2 pendingCandidate;
    private int pendingCandidateHits;
    private DateTime pendingCandidateSince = DateTime.MinValue;
    private Vector2 lastInjectedCentroid;
    private Delegate? lastCentroidGoalDelegate;
    private DateTime lastTargetSwitchAt = DateTime.MinValue;
    private static readonly TimeSpan CandidateDebounce = TimeSpan.FromMilliseconds(300);
    private static readonly TimeSpan TargetSwitchCooldown = TimeSpan.FromMilliseconds(1500);
    private const float CandidateMovementThreshold = 0.5f;
    private const float CentroidMovementThreshold = 1f;
    private const float AvoidancePenaltyFreeRadius = 4f;

    public AoePackPositioningStatus Status => new(
        this.hookState,
        this.lastReason,
        rotationSolverActions.Status,
        $"{rotationSolverActions.Diagnostics}; Restore={this.rsrRestoreStatus}",
        this.lastAction?.AdjustedActionId ?? 0,
        this.lastAction?.ActionName ?? "<none>",
        this.lastAction?.Shape.ToString() ?? "<none>",
        this.lastCurrentHits,
        this.lastBestHits,
        this.lastInjected,
        this.rsrHenchedActive,
        this.rsrSnapshotMode,
        this.rsrLastRestoreStatus,
        this.lastPriorityTargetCount);

    public AoePackOverlaySnapshot? Overlay => this.lastInjected ? this.lastOverlay : null;
    public AoePackOverlaySnapshot? SuggestedCandidate => this.lastInjected ? null : this.lastSuggestion;
    public bool RsrHenchedActive => this.rsrHenchedActive;
    public bool CurrentTargetHasBossModule => currentTargetHasBossModule();

    public void SetHookState(string state)
    {
        this.hookState = state;
    }

    public void Reset()
    {
        this.goalZonesField = null;
        this.priorityTargetsProperty = null;
        this.enemyActorField = null;
        this.actorPositionField = null;
        this.actorHitboxRadiusField = null;
        this.actorInstanceIdField = null;
        this.wposXField = null;
        this.wposZField = null;
        this.resolvedHintsType = null;
        this.resolvedWPosType = null;
        this.lastReason = "reset";
        this.rsrRestoreStatus = "reset";
        this.lastAction = null;
        this.lastOverlay = null;
        this.lastSuggestion = null;
        this.lastCurrentHits = 0;
        this.lastBestHits = 0;
        this.lastInjected = false;
        this.lastBestPrimaryId = 0;
        this.lastPriorityTargetCount = 0;
        this.lastInjectedCandidate = default;
        this.lastInjectedHits = 0;
        this.lastInjectedGoalDelegate = null;
        this.pendingCandidate = default;
        this.pendingCandidateHits = 0;
        this.pendingCandidateSince = DateTime.MinValue;
        this.lastInjectedCentroid = default;
        this.lastCentroidGoalDelegate = null;
        this.lastTargetSwitchAt = DateTime.MinValue;
        this.RestoreRsrIfNeeded();
        rotationSolverActions.Reset();
    }

    public void TryInjectGoal(object hints, ICollection<BossModGoalContribution> contributions)
    {
        this.lastInjected = false;
        this.lastOverlay = null;
        var packCenterMovementEnabled = this.IsPackCenterMovementEnabled();
        if (!config.Enabled || (!config.ManageAoePackPositioning && !config.KeepTrashTargetSelected && !packCenterMovementEnabled && !(config.AvoidStandingInsideEnemies && config.ManageMovement)))
        {
            this.lastReason = "disabled";
            this.RestoreRsrIfNeeded();
            return;
        }

        if (!services.Condition[ConditionFlag.InCombat] || services.Condition[ConditionFlag.Unconscious])
        {
            this.lastReason = "not active in combat";
            this.RestoreRsrIfNeeded();
            return;
        }

        if (!this.EnsureResolved(hints.GetType()))
        {
            this.RestoreRsrIfNeeded();
            return;
        }

        var targets = this.ReadPriorityTargets(hints);
        this.lastPriorityTargetCount = targets.Count;
        var inAoeSituation = targets.Count >= 2 && !currentTargetHasBossModule();
        if (config.KeepTrashTargetSelected && inAoeSituation)
        {
            this.ApplyRsrTargeting(this.SelectPackPrimaryTarget(targets), targets);
        }

        if (!config.ManageMovement)
        {
            if (config.PickBetterAoeTarget && inAoeSituation && services.ObjectTable.LocalPlayer is { } localPlayer &&
                rotationSolverActions.TryGetUpcomingGcd(requirePreview: false, out var targetAction, out _) &&
                this.TrySelectBestAoePrimaryForCurrentPosition(targetAction, targets, localPlayer, out var movementOffPrimaryId))
            {
                this.ApplyRsrTargeting(movementOffPrimaryId, targets);
            }

            if (!config.KeepTrashTargetSelected && !config.PickBetterAoeTarget)
                this.RestoreRsrIfNeeded();

            this.lastReason = "movement management disabled";
            return;
        }

        if (automatedMovementSuppressed())
        {
            if (!config.KeepTrashTargetSelected && !config.PickBetterAoeTarget)
                this.RestoreRsrIfNeeded();

            this.lastReason = "manual movement suppression active";
            return;
        }

        if (this.TryInjectBossHitboxAvoidance(hints, targets, contributions))
        {
            return;
        }

        if (!config.ManageAoePackPositioning)
        {
            if (packCenterMovementEnabled && inAoeSituation && this.TryInjectPackCenterMovement(hints, targets, "moving closer to trash pack", aoeRange: null, inAoeSituation: inAoeSituation, contributions: contributions))
            {
                return;
            }

            this.lastReason = inAoeSituation && config.KeepTrashTargetSelected ? "trash targeting active" : inAoeSituation ? "pack movement unavailable" : "not enough priority targets";
            return;
        }

        var hasAoeAction = rotationSolverActions.TryGetUpcomingGcd(requirePreview: false, out var action, out var reason);
        var isTargetCenteredCircle = hasAoeAction && action.Shape == RsrAoeShape.Circle && action.Range > action.EffectRange + 3f && !action.IsTargetArea;

        if (packCenterMovementEnabled && inAoeSituation)
        {
            if (config.PickBetterAoeTarget || config.KeepTrashTargetSelected)
            {
                this.ApplyRsrHenched();
                // Only update target if the current one is invalid — don't flip targets mid-stabilisation
                // as that resets the debounce and prevents committing to a candidate position.
                var currentIsValid = services.TargetManager.Target is IBattleNpc t &&
                                     t.StatusFlags.HasFlag(StatusFlags.InCombat) &&
                                     !t.IsDead && t.CurrentHp > 0 && t.IsHostile();
                if (!currentIsValid)
                    this.UpdateTarget(this.lastBestPrimaryId, targets);
            }

        }
        if (!config.KeepTrashTargetSelected && !config.PickBetterAoeTarget)
        {
            this.RestoreRsrIfNeeded();
        }

        // --- Common early-exit for non-AoE actions ---
        if (!hasAoeAction || isTargetCenteredCircle)
        {
            if (packCenterMovementEnabled &&
                inAoeSituation &&
                this.TryInjectPackCenterMovement(hints, targets, "moving closer to trash pack", aoeRange: null, inAoeSituation: inAoeSituation, contributions: contributions))
            {
                return;
            }

            this.lastAction = hasAoeAction ? action : null;
            this.lastReason = isTargetCenteredCircle ? "target-centered circle AoE skipped" : reason;
            this.lastCurrentHits = 0;
            this.lastBestHits = 0;
            this.lastInjectedGoalDelegate = null;

            // While Henched with no AoE queued, keep the centroid pull active so BMR
            // doesn't fall back to range management and oscillate.
            if (this.rsrHenchedActive && targets.Count >= 2 && config.ManageTargetUptime)
                this.InjectCentroidHold(hints, targets, this.lastAction, inAoeSituation, contributions);

            return;
        }

        this.lastAction = action;

        // Only reposition for self-origin AoEs (Range == 0, no TargetArea flag) — the player's
        // body is the blast origin and standing inside the pack is required to hit more targets.
        // Ground-placed or target-centered AoEs don't benefit from repositioning; the player can
        // stand at any range and still place the effect. Running in for those is harmful.
        if (action.Range > 1f || action.IsTargetArea)
        {
            this.lastReason = "non-self-origin AoE — no repositioning";
            this.lastCurrentHits = 0;
            this.lastBestHits = 0;
            this.lastInjectedGoalDelegate = null;
            if (packCenterMovementEnabled && inAoeSituation)
                this.TryInjectPackCenterMovement(hints, targets, "non-self-origin AoE — closing to attack range", aoeRange: null, inAoeSituation: inAoeSituation, contributions: contributions);
            return;
        }

        if (targets.Count < 2)
        {
            this.lastReason = "not enough BMR priority targets";
            this.lastCurrentHits = targets.Count;
            this.lastBestHits = targets.Count;
            return;
        }

        var player = services.ObjectTable.LocalPlayer;
        if (player == null)
        {
            this.lastReason = "local player unavailable";
            return;
        }

        var radius = Math.Max(1f, action.EffectRange);
        var castRange = Math.Min(Math.Max(action.Range, radius), 30f);
        var playerPos = new Vector2(player.Position.X, player.Position.Z);

        // For directional shapes try every in-combat target as primary to find the best orientation.
        // For circles the primary target only matters for target resolution, not direction.
        var primaryCandidates = action.Shape == RsrAoeShape.Circle
            ? [this.ResolvePrimaryTarget(action, targets) ?? targets[0]]
            : targets.ToArray();

        var targetPickingEnabled = (config.PickBetterAoeTarget || config.KeepTrashTargetSelected) && action.Shape != RsrAoeShape.Circle;
        var currentTargetId = services.TargetManager.Target?.GameObjectId ?? 0;
        GoalPlan? bestPlan = null;
        GoalPlan.CandidateScore bestScore = default;
        int bestCurrentHits = 0;
        ulong bestPrimaryId = 0;
        GoalPlan? bestStationaryPlan = null;
        int bestStationaryHits = 0;
        int currentTargetHits = -1;
        ulong bestStationaryPrimaryId = 0;

        foreach (var primary in primaryCandidates)
        {
            var plan = new GoalPlan(
                action.Shape,
                targets.ToArray(),
                primary,
                radius,
                castRange,
                action.HalfWidth,
                minHits: 2);

            var currentHits = plan.ScoreHits(playerPos);
            var candidate = plan.FindBestCandidate(playerPos);
            var primaryIsCurrentTarget = primary.InstanceId == currentTargetId;

            if (bestPlan == null || candidate.Hits > bestScore.Hits ||
                (candidate.Hits == bestScore.Hits && currentHits > bestCurrentHits))
            {
                bestPlan = plan;
                bestScore = candidate;
                bestCurrentHits = currentHits;
                bestPrimaryId = primary.InstanceId;
            }

            if (primaryIsCurrentTarget)
            {
                currentTargetHits = currentHits;
            }

            if (targetPickingEnabled &&
                (bestStationaryPlan == null ||
                 currentHits > bestStationaryHits ||
                 (currentHits == bestStationaryHits && primaryIsCurrentTarget)))
            {
                bestStationaryPlan = plan;
                bestStationaryHits = currentHits;
                bestStationaryPrimaryId = primary.InstanceId;
            }
        }

        if (bestPlan == null)
        {
            this.lastReason = "primary target unavailable";
            this.lastCurrentHits = 0;
            this.lastBestHits = 0;
            return;
        }

        this.lastCurrentHits = bestCurrentHits;
        this.lastBestHits = bestScore.Hits;
        this.lastBestPrimaryId = bestPrimaryId;

        if (targetPickingEnabled &&
            bestStationaryPlan != null &&
            bestStationaryHits >= 2 &&
            bestStationaryHits >= this.lastBestHits)
        {
            this.lastCurrentHits = bestStationaryHits;
            this.lastBestHits = bestStationaryHits;
            this.lastBestPrimaryId = bestStationaryPrimaryId;
            this.lastSuggestion = null;
            this.pendingCandidateSince = DateTime.MinValue;
            this.lastInjectedGoalDelegate = null;
            this.lastReason = bestStationaryPrimaryId == currentTargetId ? "AoE target already optimal" : "AoE target selected";
            if (bestStationaryPrimaryId != currentTargetId)
            {
                this.ApplyRsrTargeting(
                    bestStationaryPrimaryId,
                    targets,
                    forcePreferred: bestStationaryHits > currentTargetHits);
            }
            return;
        }

        if (targetPickingEnabled && bestStationaryHits > this.lastCurrentHits)
        {
            this.lastCurrentHits = bestStationaryHits;
        }

        if (this.lastCurrentHits >= 2 && this.lastBestHits > this.lastCurrentHits)
        {
            this.lastSuggestion = bestPlan.CreateOverlay(action, bestScore.Position, this.lastCurrentHits, this.lastBestHits, player.Position.Y);
        }
        else
        {
            this.lastSuggestion = null;
        }

        if (this.lastBestHits <= this.lastCurrentHits)
        {
            this.lastReason = "no meaningful AoE improvement";
            this.pendingCandidateSince = DateTime.MinValue;
            if ((config.ManageTargetUptime || config.PickBetterAoeTarget || config.KeepTrashTargetSelected) && this.rsrHenchedActive)
            {
                if (config.PickBetterAoeTarget || config.KeepTrashTargetSelected)
                    this.UpdateTarget(bestPrimaryId, targets);
                // Keep last injected delegate alive rather than dropping to centroid hold immediately —
                // transient score drops (action cycling, mob movement) should not abort an active goal.
                if (this.lastInjectedGoalDelegate != null)
                {
                    contributions.Add(new(this.lastInjectedGoalDelegate, BossModGoalPriority.ImmediateAction, "AoE pack"));
                    this.lastInjected = true;
                    this.lastReason = "goal stable";
                }
                else if (config.ManageTargetUptime)
                    this.InjectCentroidHold(hints, targets, action, inAoeSituation, contributions);
            }
            else if (!config.KeepTrashTargetSelected && !config.PickBetterAoeTarget)
                this.RestoreRsrIfNeeded();
            return;
        }

        var goalZones = this.goalZonesField!.GetValue(hints) as IList;
        if (goalZones == null)
        {
            this.lastReason = "BMR goal zone list unavailable";
            if ((config.ManageTargetUptime || config.PickBetterAoeTarget || config.KeepTrashTargetSelected) && this.rsrHenchedActive)
            {
                if (config.PickBetterAoeTarget || config.KeepTrashTargetSelected)
                    this.UpdateTarget(bestPrimaryId, targets);
                if (config.ManageTargetUptime)
                    this.InjectCentroidHold(hints, targets, action, inAoeSituation, contributions);
            }
            else if (!config.KeepTrashTargetSelected && !config.PickBetterAoeTarget)
                this.RestoreRsrIfNeeded();
            return;
        }

        var now = DateTime.UtcNow;
        // Always track the latest best position, but only reset the debounce timer when hit count changes.
        // Position drift within the same hit count (e.g. cone rotating slightly as mobs shuffle) is noise.
        this.pendingCandidate = bestScore.Position;
        if (bestScore.Hits != this.pendingCandidateHits)
        {
            this.pendingCandidateHits = bestScore.Hits;
            this.pendingCandidateSince = now;
        }

        var candidateStable = (now - this.pendingCandidateSince) >= CandidateDebounce;
        var candidateChanged = Vector2.Distance(this.pendingCandidate, this.lastInjectedCandidate) > CandidateMovementThreshold
                               || this.pendingCandidateHits != this.lastInjectedHits;

        if (candidateStable && candidateChanged)
        {
            this.lastInjectedGoalDelegate = bestPlan.CreateGoalDelegate(this.resolvedWPosType!, this.wposXField!, this.wposZField!, this.lastCurrentHits);
            this.lastInjectedCandidate = this.pendingCandidate;
            this.lastInjectedHits = this.pendingCandidateHits;
            this.lastOverlay = bestPlan.CreateOverlay(action, bestScore.Position, this.lastCurrentHits, this.lastBestHits, player.Position.Y);
        }

        if (this.lastInjectedGoalDelegate == null)
        {
            this.lastReason = "candidate stabilising";
            return;
        }

        contributions.Add(new(this.lastInjectedGoalDelegate, BossModGoalPriority.ImmediateAction, "AoE pack"));
        this.lastInjected = true;
        this.lastOverlay ??= bestPlan.CreateOverlay(action, bestScore.Position, this.lastCurrentHits, this.lastBestHits, player.Position.Y);
        this.lastReason = candidateStable && candidateChanged ? "goal injected" : "goal stable";

        // Reactive RSR targeting: Henched only after a goal is injected (improvement found).
        if (config.PickBetterAoeTarget || config.KeepTrashTargetSelected)
        {
            this.ApplyRsrTargeting(bestPrimaryId, targets);
        }
        else if (this.rsrHenchedActive && (config.PickBetterAoeTarget || config.KeepTrashTargetSelected))
        {
            this.UpdateTarget(bestPrimaryId, targets);
        }
    }

    private Delegate CreateCentroidGoalDelegate(Vector2 centroid, float acceptRadius = 3f)
    {
        var wposType = this.resolvedWPosType!;
        var xField = this.wposXField!;
        var zField = this.wposZField!;
        var parameter = System.Linq.Expressions.Expression.Parameter(wposType, "p");
        var xVal = System.Linq.Expressions.Expression.Convert(System.Linq.Expressions.Expression.Field(parameter, xField), typeof(float));
        var zVal = System.Linq.Expressions.Expression.Convert(System.Linq.Expressions.Expression.Field(parameter, zField), typeof(float));
        var dx = System.Linq.Expressions.Expression.Subtract(xVal, System.Linq.Expressions.Expression.Constant(centroid.X));
        var dz = System.Linq.Expressions.Expression.Subtract(zVal, System.Linq.Expressions.Expression.Constant(centroid.Y));
        var distSq = System.Linq.Expressions.Expression.Add(
            System.Linq.Expressions.Expression.Multiply(dx, dx),
            System.Linq.Expressions.Expression.Multiply(dz, dz));
        // Flat-top circle: no gradient so BMR stops moving once inside.
        var radiusSq = System.Linq.Expressions.Expression.Constant(acceptRadius * acceptRadius);
        var score = System.Linq.Expressions.Expression.Condition(
            System.Linq.Expressions.Expression.LessThanOrEqual(distSq, radiusSq),
            System.Linq.Expressions.Expression.Constant(GoalZoneScorePolicy.PackApproachPreference),
            System.Linq.Expressions.Expression.Constant(0f));
        var delegateType = typeof(Func<,>).MakeGenericType(wposType, typeof(float));
        return System.Linq.Expressions.Expression.Lambda(delegateType, score, parameter).Compile();
    }

    private bool TryInjectBossHitboxAvoidance(object hints, IReadOnlyList<TargetSnapshot> priorityTargets, ICollection<BossModGoalContribution> contributions)
    {
        if (!config.AvoidStandingInsideEnemies || !currentTargetHasBossModule())
        {
            return false;
        }

        var player = services.ObjectTable.LocalPlayer;
        if (player == null)
        {
            this.lastReason = "local player unavailable";
            return false;
        }

        var playerPosition = new Vector2(player.Position.X, player.Position.Z);
        var targets = this.GetBossHitboxAvoidanceTargets(priorityTargets).ToArray();
        if (targets.Length == 0 || !this.IsInsideEnemyCenterAvoidance(playerPosition, targets))
        {
            return false;
        }

        var goalZones = this.goalZonesField!.GetValue(hints) as IList;
        if (goalZones == null)
        {
            this.lastReason = "BMR goal zone list unavailable";
            return false;
        }

        var plan = new EnemyHitboxAvoidanceGoalPlan(targets);
        contributions.Add(new(
            plan.CreateGoalDelegate(this.resolvedWPosType!, this.wposXField!, this.wposZField!),
            BossModGoalPriority.Convenience,
            "Boss center avoidance"));

        this.lastAction = null;
        this.lastCurrentHits = 0;
        this.lastBestHits = 0;
        this.lastInjected = true;
        this.lastReason = "avoiding boss center";
        this.lastOverlay = new AoePackOverlaySnapshot(
            0,
            "Boss center",
            RsrAoeShape.Circle.ToString(),
            player.Position,
            new Vector3(targets[0].Position.X, player.Position.Y, targets[0].Position.Y),
            Math.Max(0.5f, player.HitboxRadius),
            0f,
            0,
            0,
            targets.Select(target => new AoePackOverlayTarget(
                new Vector3(target.Position.X, player.Position.Y, target.Position.Y),
                target.Radius,
                false,
                this.IsInsideEnemyCenterAvoidance(playerPosition, target),
                "center")).ToArray());
        return true;
    }

    private IEnumerable<TargetSnapshot> GetBossHitboxAvoidanceTargets(IReadOnlyList<TargetSnapshot> priorityTargets)
    {
        if (services.TargetManager.Target is IBattleChara target && !target.IsDead)
        {
            yield return new TargetSnapshot(
                target.GameObjectId,
                new Vector2(target.Position.X, target.Position.Z),
                BossCenterAvoidanceRadius(target.HitboxRadius));
        }

        foreach (var priorityTarget in priorityTargets)
        {
            if (priorityTarget.Radius >= AvoidancePenaltyFreeRadius)
            {
                yield return priorityTarget with { Radius = BossCenterAvoidanceRadius(priorityTarget.Radius) };
            }
        }
    }

    private bool IsInsideEnemyCenterAvoidance(Vector2 position, IEnumerable<TargetSnapshot> targets)
    {
        return targets.Any(target => this.IsInsideEnemyCenterAvoidance(position, target));
    }

    private bool IsInsideEnemyCenterAvoidance(Vector2 position, TargetSnapshot target)
    {
        return Vector2.DistanceSquared(position, target.Position) < target.Radius * target.Radius;
    }

    private static float BossCenterAvoidanceRadius(float hitboxRadius)
    {
        return Math.Clamp(hitboxRadius * 0.2f, 1.25f, 3f);
    }

    private void ApplyRsrHenched()
    {
        if (this.rsrHenchedActive) return;
        var snapshot = rotationSolverIpc.TryGetCurrentState(services.Log);
        if (snapshot == null)
        {
            this.rsrRestoreStatus = "snapshot unavailable";
            this.rsrLastRestoreStatus = "snapshot unavailable";
            return;
        }

        // Never restore to Henched or Off — both indicate an unreliable snapshot; default to Auto.
        this.rsrSnapshotMode = (snapshot.Value == StateCommandType.Henched || snapshot.Value == StateCommandType.Off)
            ? StateCommandType.Auto
            : snapshot.Value;
        this.rsrRestoreStatus = $"snapshot {snapshot.Value} -> restore {this.rsrSnapshotMode}";
        rotationSolverIpc.SetHenched();
        this.rsrHenchedActive = true;
    }

    private void ApplyRsrTargeting(ulong primaryId, IReadOnlyCollection<TargetSnapshot>? priorityTargets = null, bool forcePreferred = false)
    {
        this.ApplyRsrHenched();
        this.UpdateTarget(primaryId, priorityTargets, forcePreferred);
    }

    private void UpdateTarget(ulong preferredId, IReadOnlyCollection<TargetSnapshot>? priorityTargets = null, bool forcePreferred = false)
    {
        if (!forcePreferred && this.TryKeepCurrentPriorityTarget(preferredId, priorityTargets))
        {
            return;
        }

        // Try preferred target first.
        if (preferredId != 0)
        {
            var preferred = services.ObjectTable.SearchById(preferredId) as IBattleNpc;
            if (preferred != null && !preferred.IsDead && preferred.CurrentHp > 0 && preferred.IsHostile())
            {
                if (services.TargetManager.Target?.GameObjectId != preferredId)
                {
                    services.TargetManager.Target = preferred;
                    this.lastTargetSwitchAt = DateTime.UtcNow;
                }
                return;
            }
        }

        // If the current target is already a valid hostile enemy, leave it alone.
        var currentTarget = services.TargetManager.Target as IBattleNpc;
        if (currentTarget != null &&
            currentTarget.StatusFlags.HasFlag(StatusFlags.InCombat) &&
            !currentTarget.IsDead && currentTarget.CurrentHp > 0 &&
            currentTarget.IsHostile())
        {
            return;
        }

        // Fall back to nearest hostile in-combat enemy only when current target is invalid.
        var player = services.ObjectTable.LocalPlayer;
        if (player == null) return;

        IBattleChara? best = null;
        var bestDist = float.MaxValue;
        foreach (var obj in services.ObjectTable.OfType<IBattleNpc>())
        {
            if (!obj.IsHostile()) continue;
            if (!obj.StatusFlags.HasFlag(StatusFlags.InCombat)) continue;
            if (obj.IsDead || obj.CurrentHp == 0) continue;
            var dist = Vector2.Distance(
                new Vector2(player.Position.X, player.Position.Z),
                new Vector2(obj.Position.X, obj.Position.Z));
            if (dist < bestDist) { best = obj; bestDist = dist; }
        }

        if (best != null)
        {
            services.TargetManager.Target = best;
            this.lastTargetSwitchAt = DateTime.UtcNow;
        }
    }

    private bool TryKeepCurrentPriorityTarget(ulong preferredId, IReadOnlyCollection<TargetSnapshot>? priorityTargets)
    {
        if (priorityTargets == null || priorityTargets.Count == 0)
        {
            return false;
        }

        var currentTarget = services.TargetManager.Target as IBattleNpc;
        if (currentTarget == null ||
            !currentTarget.StatusFlags.HasFlag(StatusFlags.InCombat) ||
            currentTarget.IsDead ||
            currentTarget.CurrentHp == 0 ||
            !currentTarget.IsHostile())
        {
            return false;
        }

        var currentId = currentTarget.GameObjectId;
        if (!priorityTargets.Any(target => target.InstanceId == currentId))
        {
            return false;
        }

        if (preferredId == 0 || preferredId == currentId)
        {
            return true;
        }

        return DateTime.UtcNow - this.lastTargetSwitchAt < TargetSwitchCooldown;
    }


    private void InjectCentroidHold(object hints, List<TargetSnapshot> targets, RsrAoeActionSnapshot? action, bool inAoeSituation, ICollection<BossModGoalContribution> contributions)
    {
        var aoeRange = action is { Range: <= 1f, IsTargetArea: false } ? action.EffectRange : (float?)null;
        if (!this.TryInjectPackCenterMovement(hints, targets, "holding near pack", aoeRange, inAoeSituation, contributions))
            this.lastReason = "holding near pack";
    }

    private bool IsPackCenterMovementEnabled() => config.ManageTargetUptime;

    private ulong SelectPackPrimaryTarget(IReadOnlyList<TargetSnapshot> targets)
    {
        if (targets.Count == 0)
        {
            return 0;
        }

        if (services.TargetManager.Target is IBattleNpc currentTarget &&
            currentTarget.StatusFlags.HasFlag(StatusFlags.InCombat) &&
            !currentTarget.IsDead &&
            currentTarget.CurrentHp > 0 &&
            currentTarget.IsHostile() &&
            targets.Any(target => target.InstanceId == currentTarget.GameObjectId))
        {
            return currentTarget.GameObjectId;
        }

        var center = targets.Aggregate(Vector2.Zero, (acc, target) => acc + target.Position) / targets.Count;
        TargetSnapshot best = targets[0];
        var bestNeighborCount = -1;
        var bestDistanceSq = float.MaxValue;
        foreach (var target in targets)
        {
            var neighborCount = 0;
            foreach (var other in targets)
            {
                if (Vector2.DistanceSquared(target.Position, other.Position) <= 100f)
                {
                    neighborCount++;
                }
            }

            var distanceSq = Vector2.DistanceSquared(target.Position, center);
            if (neighborCount > bestNeighborCount ||
                (neighborCount == bestNeighborCount && distanceSq < bestDistanceSq))
            {
                best = target;
                bestNeighborCount = neighborCount;
                bestDistanceSq = distanceSq;
            }
        }

        return best.InstanceId;
    }

    private bool TrySelectBestAoePrimaryForCurrentPosition(RsrAoeActionSnapshot action, IReadOnlyList<TargetSnapshot> targets, IBattleChara player, out ulong primaryId)
    {
        primaryId = 0;
        if (targets.Count < 2 || action.Shape == RsrAoeShape.Circle && action.Range > action.EffectRange + 3f)
        {
            return false;
        }

        if (action.Range > 1f || action.IsTargetArea)
        {
            return false;
        }

        var radius = Math.Max(1f, action.EffectRange);
        var castRange = Math.Min(Math.Max(action.Range, radius), 30f);
        var playerPos = new Vector2(player.Position.X, player.Position.Z);
        var primaryCandidates = action.Shape == RsrAoeShape.Circle
            ? [this.ResolvePrimaryTarget(action, targets) ?? targets[0]]
            : targets.ToArray();

        var bestHits = -1;
        foreach (var primary in primaryCandidates)
        {
            var plan = new GoalPlan(
                action.Shape,
                targets.ToArray(),
                primary,
                radius,
                castRange,
                action.HalfWidth,
                minHits: 2);
            var hits = plan.ScoreHits(playerPos);
            if (hits > bestHits)
            {
                primaryId = primary.InstanceId;
                bestHits = hits;
            }
        }

        return primaryId != 0 && bestHits >= 2;
    }

    private bool TryInjectPackCenterMovement(object hints, List<TargetSnapshot> targets, string reason, float? aoeRange, bool inAoeSituation, ICollection<BossModGoalContribution> contributions)
    {
        if (targets.Count < 2)
            return false;

        var goalZones = this.goalZonesField!.GetValue(hints) as IList;
        if (goalZones == null)
        {
            this.lastReason = "BMR goal zone list unavailable";
            return false;
        }

        if (services.TargetManager.Target == null)
            this.UpdateTarget(0, targets);

        var player = services.ObjectTable.LocalPlayer;
        if (player == null)
        {
            this.lastReason = "local player unavailable";
            return false;
        }

        var engagementRange = aoeRange ?? (inAoeSituation ? jobRangeProvider.PackAoeRange : jobRangeProvider.EngagementRange);
        var playerPos = new Vector2(player.Position.X, player.Position.Z);
        var primary = targets[0];

        var inRangeCount = 0;
        foreach (var t in targets)
        {
            var d = Vector2.Distance(playerPos, t.Position) - t.Radius;
            if (d <= engagementRange) inRangeCount++;
        }
        if (inRangeCount >= 2)
        {
            this.lastReason = "already in engagement range of pack";
            return false;
        }

        var plan = new GoalPlan(
            RsrAoeShape.Circle,
            targets.ToArray(),
            primary,
            radius: engagementRange,
            range: engagementRange,
            halfWidth: 0f,
            minHits: 1);

        var candidate = plan.FindBestCandidate(playerPos);

        if (this.lastCentroidGoalDelegate == null ||
            Vector2.Distance(candidate.Position, this.lastInjectedCentroid) > CentroidMovementThreshold)
        {
            this.lastCentroidGoalDelegate = plan.CreateGoalDelegate(this.resolvedWPosType!, this.wposXField!, this.wposZField!, 0);
            this.lastInjectedCentroid = candidate.Position;
        }

        contributions.Add(new(this.lastCentroidGoalDelegate, BossModGoalPriority.Uptime, "Pack engagement"));
        this.lastAction = null;
        this.lastCurrentHits = 0;
        this.lastBestHits = candidate.Hits;
        this.lastInjected = true;
        this.lastReason = reason;
        var y = player.Position.Y;
        var centroid2d = targets.Aggregate(Vector2.Zero, (acc, t) => acc + t.Position) / targets.Count;
        this.lastOverlay = new AoePackOverlaySnapshot(
            0,
            "Pack engagement",
            RsrAoeShape.Circle.ToString(),
            new Vector3(candidate.Position.X, y, candidate.Position.Y),
            new Vector3(centroid2d.X, y, centroid2d.Y),
            engagementRange,
            0f,
            0,
            candidate.Hits,
            this.CreateOverlayTargets(targets, y, candidate.Position, hit: false));
        return true;
    }

    private AoePackOverlayTarget[] CreateOverlayTargets(IEnumerable<TargetSnapshot> targets, float y, Vector2 candidate, bool hit)
    {
        return targets.Select(target => new AoePackOverlayTarget(
            new Vector3(target.Position.X, y, target.Position.Y),
            target.Radius,
            hit,
            false)).ToArray();
    }

    private sealed class EnemyHitboxAvoidanceGoalPlan(TargetSnapshot[] targets)
    {
        private static readonly MethodInfo ScoreFromWPosMethod = typeof(EnemyHitboxAvoidanceGoalPlan).GetMethod(nameof(ScoreFromWPos), BindingFlags.Instance | BindingFlags.NonPublic)!;
        private readonly TargetSnapshot[] targets = targets;

        public Delegate CreateGoalDelegate(Type wposType, FieldInfo xField, FieldInfo zField)
        {
            var parameter = Expression.Parameter(wposType, "p");
            var call = Expression.Call(
                Expression.Constant(this),
                ScoreFromWPosMethod,
                Expression.Convert(Expression.Field(parameter, xField), typeof(float)),
                Expression.Convert(Expression.Field(parameter, zField), typeof(float)));
            var delegateType = typeof(Func<,>).MakeGenericType(wposType, typeof(float));
            return Expression.Lambda(delegateType, call, parameter).Compile();
        }

        private float ScoreFromWPos(float x, float z)
        {
            var position = new Vector2(x, z);
            var score = GoalZoneScorePolicy.WeakPreference;
            foreach (var target in this.targets)
            {
                var radius = Math.Max(0.1f, target.Radius);
                var distance = Vector2.Distance(position, target.Position);
                if (distance < radius)
                {
                    score = Math.Min(score, GoalZoneScorePolicy.WeakPreference * distance / radius);
                }
            }

            return score;
        }
    }

    private void RestoreRsrIfNeeded()
    {
        if (!this.rsrHenchedActive) return;
        this.rsrRestoreStatus = $"restored {this.rsrSnapshotMode}";
        this.rsrLastRestoreStatus = $"snapshot {this.rsrSnapshotMode} restored";
        rotationSolverIpc.RestoreMode(this.rsrSnapshotMode);
        this.rsrHenchedActive = false;
    }

    private bool EnsureResolved(Type hintsType)
    {
        if (this.resolvedHintsType == hintsType &&
            this.goalZonesField != null &&
            this.priorityTargetsProperty != null &&
            this.enemyActorField != null &&
            this.actorPositionField != null &&
            this.actorHitboxRadiusField != null &&
            this.actorInstanceIdField != null &&
            this.wposXField != null &&
            this.wposZField != null)
        {
            return true;
        }

        const BindingFlags InstanceFlags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        var goalZones = hintsType.GetField("GoalZones", InstanceFlags);
        var priorityTargets = hintsType.GetProperty("PriorityTargets", InstanceFlags);
        var wposType = hintsType.Assembly.GetType("BossMod.WPos");
        var enemyType = hintsType.GetNestedType("Enemy", BindingFlags.Public);
        var actorField = enemyType?.GetField("Actor", InstanceFlags);
        var actorType = actorField?.FieldType;
        var positionField = (MemberInfo?)actorType?.GetProperty("Position", InstanceFlags) ?? actorType?.GetField("Position", InstanceFlags);
        var hitboxField = (MemberInfo?)actorType?.GetField("HitboxRadius", InstanceFlags) ?? actorType?.GetProperty("HitboxRadius", InstanceFlags);
        var instanceField = (MemberInfo?)actorType?.GetField("InstanceID", InstanceFlags) ?? actorType?.GetProperty("InstanceID", InstanceFlags);
        var xField = wposType?.GetField("X", InstanceFlags);
        var zField = wposType?.GetField("Z", InstanceFlags);
        if (goalZones == null || priorityTargets == null || wposType == null || actorField == null || positionField == null || hitboxField == null || instanceField == null || xField == null || zField == null)
        {
            this.lastReason = $"BMR AoE goal reflection members unavailable: {FormatMissing(
                (goalZones == null, "AIHints.GoalZones"),
                (priorityTargets == null, "AIHints.PriorityTargets"),
                (wposType == null, "BossMod.WPos"),
                (enemyType == null, "AIHints.Enemy"),
                (actorField == null, "AIHints.Enemy.Actor"),
                (positionField == null, "Enemy.Actor.Position"),
                (hitboxField == null, "Enemy.Actor.HitboxRadius"),
                (instanceField == null, "Enemy.Actor.InstanceID"),
                (xField == null, "BossMod.WPos.X"),
                (zField == null, "BossMod.WPos.Z"))}";
            return false;
        }

        this.resolvedHintsType = hintsType;
        this.resolvedWPosType = wposType;
        this.goalZonesField = goalZones;
        this.priorityTargetsProperty = priorityTargets;
        this.enemyActorField = actorField;
        this.actorPositionField = (MemberInfo)positionField;
        this.actorHitboxRadiusField = (MemberInfo)hitboxField;
        this.actorInstanceIdField = (MemberInfo)instanceField;
        this.wposXField = xField;
        this.wposZField = zField;
        return true;
    }

    private static object? GetMemberValue(MemberInfo? member, object? obj) => member switch
    {
        FieldInfo f    => f.GetValue(obj),
        PropertyInfo p => p.GetValue(obj),
        _              => null
    };

    private static string FormatMissing(params (bool Missing, string Name)[] members)
    {
        var missing = new List<string>();
        foreach (var member in members)
        {
            if (member.Missing)
            {
                missing.Add(member.Name);
            }
        }

        return string.Join(", ", missing);
    }

    private List<TargetSnapshot> ReadPriorityTargets(object hints)
    {
        var result = new List<TargetSnapshot>(8);
        if (this.priorityTargetsProperty!.GetValue(hints) is not IEnumerable enemies)
        {
            return result;
        }

        foreach (var enemy in enemies)
        {
            if (enemy == null)
            {
                continue;
            }

            var actor = this.enemyActorField!.GetValue(enemy);
            if (actor == null)
            {
                continue;
            }

            var position = GetMemberValue(this.actorPositionField, actor);
            if (position == null)
            {
                continue;
            }

            var instanceId = Convert.ToUInt64(GetMemberValue(this.actorInstanceIdField, actor), System.Globalization.CultureInfo.InvariantCulture);
            var gameObj = services.ObjectTable.SearchById(instanceId) as IBattleChara;
            if (gameObj == null || !gameObj.StatusFlags.HasFlag(StatusFlags.InCombat))
            {
                continue;
            }

            result.Add(new TargetSnapshot(
                instanceId,
                new Vector2(
                    Convert.ToSingle(this.wposXField!.GetValue(position), System.Globalization.CultureInfo.InvariantCulture),
                    Convert.ToSingle(this.wposZField!.GetValue(position), System.Globalization.CultureInfo.InvariantCulture)),
                Convert.ToSingle(GetMemberValue(this.actorHitboxRadiusField, actor), System.Globalization.CultureInfo.InvariantCulture)));
        }

        return result;
    }

    private TargetSnapshot? ResolvePrimaryTarget(RsrAoeActionSnapshot action, IReadOnlyList<TargetSnapshot> targets)
    {
        foreach (var target in targets)
        {
            if (target.InstanceId == action.PrimaryTargetId)
            {
                return target;
            }
        }

        if (action.PrimaryTargetPosition != default)
        {
            return new TargetSnapshot(
                action.PrimaryTargetId,
                new Vector2(action.PrimaryTargetPosition.X, action.PrimaryTargetPosition.Z),
                action.PrimaryTargetRadius);
        }

        return targets.Count > 0 ? targets[0] : null;
    }


}

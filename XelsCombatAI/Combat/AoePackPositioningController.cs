using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
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
    int PriorityTargetCount,
    Vector3? Candidate,
    Vector3? PrimaryTarget,
    bool CandidateInjected,
    TrashPullDiagnostics TrashPull);

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
    private FieldInfo? forcedMovementField;
    private FieldInfo? forbiddenZonesField;
    private FieldInfo? pathfindMapCenterField;
    private FieldInfo? pathfindMapBoundsField;
    private FieldInfo? potentialTargetsField;
    private PropertyInfo? priorityTargetsProperty;
    private FieldInfo? enemyActorField;
    private MemberInfo? actorPositionField;
    private MemberInfo? actorHitboxRadiusField;
    private MemberInfo? actorInstanceIdField;
    private FieldInfo? wposXField;
    private FieldInfo? wposZField;
    private ConstructorInfo? wdirConstructor;
    private MethodInfo? boundsContainsMethod;
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
    private bool bossModEncounterActive;
    private bool bmrMoveRequested;
    private bool bmrMoveImminent;
    private bool bossLikeCombatActive;
    private StateCommandType rsrSnapshotMode;
    private int lastPriorityTargetCount;
    private Vector2 lastInjectedCandidate;
    private int lastInjectedHits;
    private Delegate? lastInjectedGoalDelegate;
    private Vector2 pendingCandidate;
    private int pendingCandidateHits;
    private uint pendingCandidateActionId;
    private DateTime pendingCandidateSince = DateTime.MinValue;
    private uint lastInjectedActionId;
    private ulong lastInjectedPrimaryId;
    private uint lastSuggestionActionId;
    private int lastSuggestionCurrentHits;
    private int lastSuggestionBestHits;
    private Vector2 lastSuggestionCandidate;
    private DateTime lastSuggestionHeldUntil = DateTime.MinValue;
    private Vector2 lastInjectedCentroid;
    private Delegate? lastCentroidGoalDelegate;
    private Vector2 lastPackCentroid;
    private Vector2 lastPackDirection;
    private DateTime lastPackCentroidAt = DateTime.MinValue;
    private DateTime lastTargetSwitchAt = DateTime.MinValue;
    private readonly TrashPullStateTracker trashPullState = new();
    private static readonly TimeSpan CandidateDebounce = TimeSpan.FromMilliseconds(150);
    private static readonly TimeSpan SuggestionHold = TimeSpan.FromMilliseconds(900);
    private static readonly TimeSpan TargetSwitchCooldown = TimeSpan.FromMilliseconds(1500);
    private const float CandidateMovementThreshold = 2f;
    private const float SuggestionMovementThreshold = 2f;
    private const float CentroidMovementThreshold = 3f;
    private const float FluidPackFollowMovementThreshold = 2f;
    private const float FluidPackFollowMinimumSpeed = 1.25f;
    private const float FluidPackFollowSlack = 1.5f;
    private const float DominantPackClusterRadius = 10f;
    private const float RemotePackSwitchDistance = 45f;
    private const float LocalTrashTargetRetainSurfaceDistance = 5f;
    private const float LocalTrashTargetRetainTankDistance = 12f;
    private const float TankSidePackAnchorGain = 8f;
    private const float MaxPackCentroidStep = 15f;
    private const float MaxObservedPackSpeed = 8f;
    private const float BossLikeHitboxRadius = 4f;
    private const float MarginalAoeGainMaxMoveDistance = 4.5f;
    private const float ModerateAoeGainMaxMoveDistance = 7f;
    private const float StrongAoeGainMaxMoveDistance = 10f;
    private const float LongRangePackAoeThreshold = 8f;
    private const float ProactivePackAoeMaxBodyRange = 8f;
    private const float LongRangeCasterPackFollowDistanceFactor = 0.75f;
    private const float LongRangeCasterPackFollowMinDistance = 14f;
    private const float LongRangeCasterPackFollowMaxDistance = 20f;
    private const float LongRangeCasterPackFollowSlack = 4f;

    public AoePackPositioningStatus Status
    {
        get
        {
            var activePlan = this.lastInjected ? this.lastOverlay : this.lastSuggestion;
            return new(
                this.hookState,
                this.lastReason,
                rotationSolverActions.Status,
                $"{rotationSolverActions.Diagnostics}; Restore={this.rsrRestoreStatus}",
                activePlan?.ActionId ?? this.lastAction?.AdjustedActionId ?? 0,
                activePlan?.ActionName ?? this.lastAction?.ActionName ?? "<none>",
                activePlan?.Shape ?? this.lastAction?.Shape.ToString() ?? "<none>",
                activePlan?.CurrentHits ?? this.lastCurrentHits,
                activePlan?.BestHits ?? this.lastBestHits,
                this.lastInjected,
                this.rsrHenchedActive,
                this.rsrSnapshotMode,
                this.rsrLastRestoreStatus,
                this.lastPriorityTargetCount,
                activePlan?.Candidate,
                activePlan?.PrimaryTarget,
                this.lastInjected,
                this.trashPullState.Current);
        }
    }

    public AoePackOverlaySnapshot? Overlay => this.lastInjected ? this.lastOverlay : null;
    public AoePackOverlaySnapshot? SuggestedCandidate => this.lastInjected ? null : this.lastSuggestion;
    public bool RsrHenchedActive => this.rsrHenchedActive;

    public void SetHookState(string state)
    {
        this.hookState = state;
    }

    public void SetBossModEncounterState(bool activeModule)
    {
        this.bossModEncounterActive = activeModule;
    }

    public void SetBossModMovementState(bool moveRequested, bool moveImminent)
    {
        this.bmrMoveRequested = moveRequested;
        this.bmrMoveImminent = moveImminent;
    }

    public void Reset()
    {
        this.goalZonesField = null;
        this.forcedMovementField = null;
        this.forbiddenZonesField = null;
        this.pathfindMapCenterField = null;
        this.pathfindMapBoundsField = null;
        this.potentialTargetsField = null;
        this.priorityTargetsProperty = null;
        this.enemyActorField = null;
        this.actorPositionField = null;
        this.actorHitboxRadiusField = null;
        this.actorInstanceIdField = null;
        this.wposXField = null;
        this.wposZField = null;
        this.wdirConstructor = null;
        this.boundsContainsMethod = null;
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
        this.bossModEncounterActive = false;
        this.bmrMoveRequested = false;
        this.bmrMoveImminent = false;
        this.bossLikeCombatActive = false;
        this.lastPriorityTargetCount = 0;
        this.lastInjectedCandidate = default;
        this.lastInjectedHits = 0;
        this.lastInjectedGoalDelegate = null;
        this.pendingCandidate = default;
        this.pendingCandidateHits = 0;
        this.pendingCandidateActionId = 0;
        this.pendingCandidateSince = DateTime.MinValue;
        this.lastInjectedActionId = 0;
        this.lastInjectedPrimaryId = 0;
        this.lastSuggestionActionId = 0;
        this.lastSuggestionCurrentHits = 0;
        this.lastSuggestionBestHits = 0;
        this.lastSuggestionCandidate = default;
        this.lastSuggestionHeldUntil = DateTime.MinValue;
        this.lastInjectedCentroid = default;
        this.lastCentroidGoalDelegate = null;
        this.lastPackCentroid = default;
        this.lastPackDirection = default;
        this.lastPackCentroidAt = DateTime.MinValue;
        this.lastTargetSwitchAt = DateTime.MinValue;
        this.trashPullState.Reset();
        this.RestoreRsrIfNeeded();
        rotationSolverActions.Reset();
    }

    public void TryInjectGoal(object hints, ICollection<BossModGoalContribution> contributions)
    {
        this.lastInjected = false;
        this.lastOverlay = null;
        var packCenterMovementEnabled = this.IsPackCenterMovementEnabled();
        if (!config.Enabled || (!config.ManageAoePackPositioning && !config.KeepTrashTargetSelected && !packCenterMovementEnabled))
        {
            this.lastReason = "disabled";
            this.trashPullState.Reset("disabled");
            this.RestoreRsrIfNeeded();
            return;
        }

        if (!CombatEngagementDetector.IsEffectivelyInCombat(services) || services.Condition[ConditionFlag.Unconscious])
        {
            this.lastReason = "not active in combat";
            this.bossLikeCombatActive = false;
            this.trashPullState.Reset("not active in combat");
            this.RestoreRsrIfNeeded();
            return;
        }

        if (!this.EnsureResolved(hints.GetType()))
        {
            this.RestoreRsrIfNeeded();
            return;
        }

        var priorityTargets = this.ReadPriorityTargets(hints);
        var potentialTargets = priorityTargets.Count < 2 ? this.ReadPotentialTargets(hints) : [];
        if (config.ManageTargetUptime && services.TargetManager.Target == null)
        {
            this.TrySelectInitialCombatTarget(priorityTargets.Count > 0 ? priorityTargets : potentialTargets);
        }

        var targetHasBossModule = currentTargetHasBossModule();
        var effectivePackTargets = this.SelectEffectivePackTargets(priorityTargets, potentialTargets);
        var combinedTargets = new List<TargetSnapshot>(priorityTargets.Count + potentialTargets.Count);
        combinedTargets.AddRange(priorityTargets);
        combinedTargets.AddRange(potentialTargets);
        var allPackTargets = DistinctTargets(combinedTargets);
        if (allPackTargets.Count == 0)
        {
            allPackTargets = effectivePackTargets;
        }

        effectivePackTargets = this.ApplyRemotePackCurrentTargetFallback(effectivePackTargets, allPackTargets);

        var hitboxBossLikeContext = this.BossLikeTargetActive(priorityTargets) || this.BossLikeTargetActive(potentialTargets);
        var packLikeTrashContext = IsPackLikeTrashContext(this.bossModEncounterActive, targetHasBossModule, effectivePackTargets.Count);
        this.bossLikeCombatActive = ShouldUseBossModuleContext(
            this.bossModEncounterActive,
            targetHasBossModule,
            packLikeTrashContext,
            hitboxBossLikeContext,
            this.bossLikeCombatActive);
        var bossModuleContext = this.bossLikeCombatActive;
        var targets = bossModuleContext ? priorityTargets : effectivePackTargets;
        var pathfindBounds = this.CreatePathfindBoundsSnapshot(hints);
        this.lastPriorityTargetCount = targets.Count;
        var inAoeSituation = targets.Count >= 2 && !bossModuleContext;
        var forcedSafetyActive = this.BossModForcedMovementActive(hints);
        var forbiddenSafetyActive = this.BossModForbiddenSafetyActive(hints);
        var mechanicSafetyActive = forcedSafetyActive || forbiddenSafetyActive;
        var shouldYieldToMechanicSafety = ShouldYieldPackMovementForSafety(
            forcedSafetyActive,
            forbiddenSafetyActive,
            this.bmrMoveRequested,
            this.bmrMoveImminent,
            bossModuleContext);
        var trashDiagnostics = this.UpdateTrashPullState(targets, allPackTargets, inAoeSituation, bossModuleContext, shouldYieldToMechanicSafety);
        if (config.KeepTrashTargetSelected)
        {
            if (this.trashPullState.Current.Phase == TrashPullPhase.Gathering && targets.Count > 0)
            {
                this.UpdateTarget(this.SelectPackPrimaryTarget(targets), targets);
            }
            else
            {
                this.TrySelectInitialCombatTarget(targets.Count > 0 ? targets : priorityTargets.Count > 0 ? priorityTargets : potentialTargets);
            }
        }

        if (bossModuleContext && this.rsrHenchedActive)
        {
            this.RestoreRsrIfNeeded();
        }

        if (shouldYieldToMechanicSafety)
        {
            this.RestoreRsrIfNeeded();
            this.ClearAoeCandidateGoal();
            this.lastAction = null;
            this.lastCurrentHits = 0;
            this.lastBestHits = 0;
            this.lastReason = mechanicSafetyActive ? "forced mechanic movement active" : "BMR movement active";
            return;
        }

        if (this.TryInjectTankLeadMovement(pathfindBounds, trashDiagnostics, targets, contributions))
        {
            return;
        }

        var shouldUseRsrTargetControl = inAoeSituation && (config.PickBetterAoeTarget || config.KeepTrashTargetSelected);
        if (shouldUseRsrTargetControl)
        {
            this.ApplyRsrTargeting(this.SelectPackPrimaryTarget(targets), targets);
        }

        var hasAoeAction = rotationSolverActions.TryGetUpcomingGcd(requirePreview: false, out var action, out var reason);
        var isTargetCenteredCircle = action?.IsTargetCenteredCircle == true ||
                                     action != null && action.Shape == RsrAoeShape.Circle && action.Range > action.EffectRange + 3f && !action.IsTargetArea;

        if (!hasAoeAction && this.rsrHenchedActive && this.ShouldRestoreRsrAfterNoAction(reason, shouldUseRsrTargetControl))
        {
            this.RestoreRsrIfNeeded();
        }

        if (!config.ManageMovement)
        {
            if (config.PickBetterAoeTarget && inAoeSituation && services.ObjectTable.LocalPlayer is { } localPlayer &&
                action != null &&
                this.TrySelectBestAoePrimaryForCurrentPosition(action, targets, localPlayer, out var movementOffPrimaryId))
            {
                this.ApplyRsrTargeting(movementOffPrimaryId, targets);
            }

            if (!config.KeepTrashTargetSelected && !config.PickBetterAoeTarget)
            {
                this.RestoreRsrIfNeeded();
            }

            this.lastReason = "movement management disabled";
            return;
        }

        if (automatedMovementSuppressed())
        {
            if (!config.KeepTrashTargetSelected && !config.PickBetterAoeTarget)
            {
                this.RestoreRsrIfNeeded();
            }

            this.lastReason = "manual movement suppression active";
            return;
        }

        if (!config.ManageAoePackPositioning)
        {
            if (packCenterMovementEnabled && inAoeSituation && this.TryInjectPackCenterMovement(hints, pathfindBounds, targets, "moving closer to trash pack", aoeRange: null, inAoeSituation: inAoeSituation, contributions: contributions))
            {
                return;
            }

            this.lastReason = inAoeSituation && config.KeepTrashTargetSelected ? "trash targeting active" : inAoeSituation ? "pack movement unavailable" : "not enough priority targets";
            return;
        }

        if (packCenterMovementEnabled && inAoeSituation && action != null && !isTargetCenteredCircle)
        {
            if (config.PickBetterAoeTarget || config.KeepTrashTargetSelected)
            {
                var rsrReady = this.ApplyRsrHenched();
                // Only update target if the current one is invalid — don't flip targets mid-stabilisation
                // as that resets the debounce and prevents committing to a candidate position.
                var currentIsValid = services.TargetManager.Target is IBattleNpc t &&
                                     t.StatusFlags.HasFlag(StatusFlags.InCombat) &&
                                     !t.IsDead && t.CurrentHp > 0 && t.IsHostile();
                if (rsrReady && !currentIsValid)
                {
                    this.UpdateTarget(this.lastBestPrimaryId, targets);
                }
            }

        }
        if (!config.KeepTrashTargetSelected && !config.PickBetterAoeTarget)
        {
            this.RestoreRsrIfNeeded();
        }

        // --- Common early-exit for non-AoE actions ---
        if (!hasAoeAction || action == null)
        {
            if (!hasAoeAction && this.ShouldRestoreRsrAfterNoAction(reason, shouldUseRsrTargetControl))
            {
                this.RestoreRsrIfNeeded();
            }

            if (this.TryInjectProactivePackAoeMovement(hints, pathfindBounds, targets, inAoeSituation, contributions))
            {
                return;
            }

            if (packCenterMovementEnabled &&
                inAoeSituation &&
                this.TryInjectPackCenterMovement(hints, pathfindBounds, targets, "moving closer to trash pack", aoeRange: null, inAoeSituation: inAoeSituation, contributions: contributions))
            {
                return;
            }

            this.lastAction = hasAoeAction ? action : null;
            this.lastReason = reason;
            this.lastCurrentHits = 0;
            this.lastBestHits = 0;
            this.ClearAoeCandidateGoal();

            return;
        }

        this.lastAction = action;

        // Directional AoEs are still player-origin even when the action has a target range.
        // Only target-area and targeted circular AoEs can be aimed without moving the body.
        if (ShouldSkipBodyReposition(action))
        {
            if (action.IsTargetCenteredCircle &&
                services.ObjectTable.LocalPlayer is { } targetedPlayer &&
                (config.PickBetterAoeTarget || config.KeepTrashTargetSelected) &&
                this.TrySelectBestTargetCenteredAoePrimary(action, targets, targetedPlayer, out var targetedPrimaryId, out var targetedCurrentHits, out var targetedBestHits))
            {
                this.lastCurrentHits = targetedCurrentHits;
                this.lastBestHits = targetedBestHits;
                this.lastBestPrimaryId = targetedPrimaryId;
                this.ApplyRsrTargeting(targetedPrimaryId, targets, forcePreferred: targetedBestHits > targetedCurrentHits);
            }

            this.lastReason = "targeted AoE — no body repositioning";
            if (!action.IsTargetCenteredCircle)
            {
                this.lastCurrentHits = 0;
                this.lastBestHits = 0;
            }

            this.ClearAoeCandidateGoal();
            if (packCenterMovementEnabled && inAoeSituation)
            {
                var targetCenteredRange = action.IsTargetCenteredCircle
                    ? action.Range
                    : (float?)null;
                this.TryInjectPackCenterMovement(hints, pathfindBounds, targets, "targeted AoE — closing to attack range", targetCenteredRange, inAoeSituation, contributions);
            }

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
        var targetArray = targets.ToArray();
        var primaryCandidates = action.Shape == RsrAoeShape.Circle
            ? [this.ResolvePrimaryTarget(action, targets) ?? targets[0]]
            : targetArray;

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
                targetArray,
                primary,
                radius,
                castRange,
                action.HalfWidth,
                minHits: 2);

            var currentHits = plan.ScoreHits(playerPos);
            var retainedCandidate = this.lastInjectedGoalDelegate != null &&
                                    this.lastInjectedActionId == action.AdjustedActionId &&
                                    this.lastInjectedPrimaryId == primary.InstanceId
                ? this.lastInjectedCandidate
                : (Vector2?)null;
            var candidate = plan.FindBestCandidate(
                playerPos,
                candidate => CandidateInsidePathfindBounds(pathfindBounds, candidate),
                retainedCandidate);
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
            this.ClearAoeCandidateGoal();
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

        if (this.lastBestHits <= this.lastCurrentHits)
        {
            this.lastReason = "no meaningful AoE improvement";
            this.pendingCandidateSince = DateTime.MinValue;
            this.ClearAoeCandidateGoal();
            if ((config.ManageTargetUptime || config.PickBetterAoeTarget || config.KeepTrashTargetSelected) && this.rsrHenchedActive)
            {
                if (config.PickBetterAoeTarget || config.KeepTrashTargetSelected)
                    this.UpdateTarget(bestPrimaryId, targets);
                if (config.ManageTargetUptime)
                    this.InjectCentroidHold(hints, pathfindBounds, targets, action, inAoeSituation, contributions);
            }
            else if (!config.KeepTrashTargetSelected && !config.PickBetterAoeTarget)
                this.RestoreRsrIfNeeded();
            return;
        }

        var candidateMoveDistance = Vector2.Distance(playerPos, bestScore.Position);
        if (ShouldSkipMarginalAoeReposition(
                this.lastCurrentHits,
                this.lastBestHits,
                targets.Count,
                candidateMoveDistance,
                out var marginalSkipReason))
        {
            this.pendingCandidateSince = DateTime.MinValue;
            this.ClearAoeCandidateGoal();
            if (targetPickingEnabled && bestStationaryPrimaryId != 0 && bestStationaryHits >= 2)
            {
                this.ApplyRsrTargeting(
                    bestStationaryPrimaryId,
                    targets,
                    forcePreferred: bestStationaryHits > currentTargetHits);
            }
            else if (config.PickBetterAoeTarget || config.KeepTrashTargetSelected)
            {
                this.UpdateTarget(bestPrimaryId, targets);
            }

            this.lastReason = marginalSkipReason;
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
                    this.InjectCentroidHold(hints, pathfindBounds, targets, action, inAoeSituation, contributions);
            }
            else if (!config.KeepTrashTargetSelected && !config.PickBetterAoeTarget)
                this.RestoreRsrIfNeeded();
            return;
        }

        var now = DateTime.UtcNow;
        // Always track the latest best position, but only reset the debounce timer when hit count changes.
        // Position drift within the same hit count (e.g. cone rotating slightly as mobs shuffle) is noise.
        this.pendingCandidate = bestScore.Position;
        if (this.pendingCandidateSince == DateTime.MinValue ||
            action.AdjustedActionId != this.pendingCandidateActionId ||
            bestScore.Hits != this.pendingCandidateHits)
        {
            this.pendingCandidateActionId = action.AdjustedActionId;
            this.pendingCandidateHits = bestScore.Hits;
            this.pendingCandidateSince = now;
        }

        var candidateAge = now - this.pendingCandidateSince;
        var candidateStable = candidateAge >= CandidateDebounce;
        var actionOrPrimaryChanged = action.AdjustedActionId != this.lastInjectedActionId ||
                                     bestPrimaryId != this.lastInjectedPrimaryId;
        var candidateChanged = Vector2.Distance(this.pendingCandidate, this.lastInjectedCandidate) > CandidateMovementThreshold
                               || this.pendingCandidateHits != this.lastInjectedHits ||
                               actionOrPrimaryChanged;
        var candidateNeedsDelegate = this.lastInjectedGoalDelegate == null || candidateChanged;
        if (candidateNeedsDelegate && !candidateStable && actionOrPrimaryChanged)
        {
            this.ExpireAoeSuggestion(now);
            this.lastReason = string.Create(
                CultureInfo.InvariantCulture,
                $"candidate stabilising {Math.Max(0d, candidateAge.TotalSeconds):0.00}/{CandidateDebounce.TotalSeconds:0.00}s");
            return;
        }

        if (candidateStable && candidateNeedsDelegate)
        {
            this.lastInjectedGoalDelegate = bestPlan.CreateGoalDelegate(this.resolvedWPosType!, this.wposXField!, this.wposZField!, this.lastCurrentHits);
            this.lastInjectedCandidate = this.pendingCandidate;
            this.lastInjectedHits = this.pendingCandidateHits;
            this.lastInjectedActionId = action.AdjustedActionId;
            this.lastInjectedPrimaryId = bestPrimaryId;
            this.lastOverlay = bestPlan.CreateOverlay(action, this.pendingCandidate, this.lastCurrentHits, this.lastBestHits, player.Position.Y);
        }

        if (candidateStable)
        {
            this.UpdateAoeSuggestion(bestPlan, action, this.pendingCandidate, this.lastCurrentHits, this.lastBestHits, player.Position.Y, now);
        }
        else
        {
            this.ExpireAoeSuggestion(now);
        }

        if (this.lastInjectedGoalDelegate == null)
        {
            this.lastReason = string.Create(
                CultureInfo.InvariantCulture,
                $"candidate stabilising {Math.Max(0d, candidateAge.TotalSeconds):0.00}/{CandidateDebounce.TotalSeconds:0.00}s");
            return;
        }

        contributions.Add(new(this.lastInjectedGoalDelegate, BossModGoalPriority.ImmediateAction, "AoE pack", this.pendingCandidate, MechanicWhisperConfidence.Confident));
        this.lastInjected = true;
        this.lastOverlay ??= bestPlan.CreateOverlay(action, this.pendingCandidate, this.lastCurrentHits, this.lastBestHits, player.Position.Y);
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

    private bool BossModForbiddenSafetyActive(object hints)
    {
        return this.forbiddenZonesField?.GetValue(hints) is ICollection { Count: > 0 };
    }

    private bool BossModForcedMovementActive(object hints)
    {
        return VectorLengthSquared(this.forcedMovementField?.GetValue(hints)) > 0.01f;
    }

    private static float VectorLengthSquared(object? value)
    {
        if (value == null)
        {
            return 0f;
        }

        if (value is Vector3 vector)
        {
            return vector.LengthSquared();
        }

        var type = value.GetType();
        var x = ReadFloatField(value, type, "X");
        var y = ReadFloatField(value, type, "Y");
        var z = ReadFloatField(value, type, "Z");
        return x * x + y * y + z * z;
    }

    private static float ReadFloatField(object value, Type type, string name)
    {
        return type.GetField(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)?.GetValue(value) switch
        {
            float f => f,
            double d => (float)d,
            _ => 0f
        };
    }

    private bool ApplyRsrHenched()
    {
        if (this.rsrHenchedActive)
        {
            return true;
        }

        if (this.bossModEncounterActive || this.bossLikeCombatActive)
        {
            this.rsrRestoreStatus = "boss combat active; Henched skipped";
            this.rsrLastRestoreStatus = "boss combat active; Henched skipped";
            return false;
        }

        try
        {
            var snapshot = rotationSolverIpc.TryGetCurrentState(services.Log);
            if (snapshot == null)
            {
                this.rsrRestoreStatus = "snapshot unavailable";
                this.rsrLastRestoreStatus = "snapshot unavailable";
                return false;
            }

            // Never restore to Henched or Off — both indicate an unreliable snapshot; default to Auto.
            this.rsrSnapshotMode = (snapshot.Value == StateCommandType.Henched || snapshot.Value == StateCommandType.Off)
                ? StateCommandType.Auto
                : snapshot.Value;
            this.rsrRestoreStatus = $"snapshot {snapshot.Value} -> restore {this.rsrSnapshotMode}";
            if (!rotationSolverIpc.SetHenched())
            {
                this.rsrRestoreStatus = "set Henched unavailable";
                this.rsrLastRestoreStatus = "set Henched unavailable";
                return false;
            }

            this.rsrHenchedActive = true;
            return true;
        }
        catch (Exception ex)
        {
            this.rsrRestoreStatus = "set Henched failed";
            this.rsrLastRestoreStatus = "set Henched failed";
            services.Log.Verbose(ex, "Could not set Rotation Solver Reborn Henched mode.");
            return false;
        }
    }

    private bool BossLikeTargetActive(IReadOnlyList<TargetSnapshot> priorityTargets)
    {
        if (services.TargetManager.Target is IBattleChara target &&
            !target.IsDead &&
            target.CurrentHp > 0 &&
            target.HitboxRadius >= BossLikeHitboxRadius)
        {
            return true;
        }

        return priorityTargets.Any(target => target.Radius >= BossLikeHitboxRadius);
    }

    internal static bool IsPackLikeTrashContext(
        bool bossModEncounterActive,
        bool targetHasBossModule,
        int effectivePackTargetCount)
    {
        _ = targetHasBossModule;
        return !bossModEncounterActive &&
               effectivePackTargetCount >= 2;
    }

    internal static bool ShouldUseBossModuleContext(
        bool bossModEncounterActive,
        bool targetHasBossModule,
        bool packLikeTrashContext,
        bool hitboxBossLikeContext,
        bool previousBossLikeCombatActive)
    {
        if (bossModEncounterActive)
        {
            return true;
        }

        if (packLikeTrashContext)
        {
            return false;
        }

        if (targetHasBossModule)
        {
            return true;
        }

        return hitboxBossLikeContext || previousBossLikeCombatActive;
    }

    internal static bool ShouldYieldPackMovementForSafety(
        bool forcedMovementActive,
        bool forbiddenSafetyActive,
        bool bmrMoveRequested,
        bool bmrMoveImminent,
        bool bossModuleContext)
    {
        _ = bmrMoveRequested;
        _ = bmrMoveImminent;
        return forcedMovementActive ||
               forbiddenSafetyActive && !bossModuleContext;
    }

    private void ApplyRsrTargeting(ulong primaryId, IReadOnlyCollection<TargetSnapshot>? priorityTargets = null, bool forcePreferred = false)
    {
        if (!this.ApplyRsrHenched())
        {
            return;
        }

        this.UpdateTarget(primaryId, priorityTargets, forcePreferred);
    }

    private void TrySelectInitialCombatTarget(IReadOnlyList<TargetSnapshot> targets)
    {
        if (targets.Count == 0 || this.HasValidCurrentCombatTarget())
        {
            return;
        }

        this.UpdateTarget(this.SelectPackPrimaryTarget(targets), targets, forcePreferred: true);
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

        // If the current target is already part of the selected pack, leave it alone.
        // A valid hostile outside the selected pack is usually a straggler and should not keep focus.
        var currentTarget = services.TargetManager.Target as IBattleNpc;
        if (currentTarget != null &&
            currentTarget.StatusFlags.HasFlag(StatusFlags.InCombat) &&
            !currentTarget.IsDead && currentTarget.CurrentHp > 0 &&
            currentTarget.IsHostile() &&
            (priorityTargets == null || priorityTargets.Count == 0 || priorityTargets.Any(target => target.InstanceId == currentTarget.GameObjectId)))
        {
            return;
        }

        var player = services.ObjectTable.LocalPlayer;
        if (player == null)
        {
            return;
        }

        if (priorityTargets != null && priorityTargets.Count > 0)
        {
            IBattleNpc? bestPriority = null;
            var bestPriorityDist = float.MaxValue;
            foreach (var target in priorityTargets)
            {
                if (services.ObjectTable.SearchById(target.InstanceId) is not IBattleNpc npc ||
                    npc.IsDead || npc.CurrentHp == 0 || !npc.IsHostile())
                {
                    continue;
                }

                var dist = Vector2.DistanceSquared(
                    new Vector2(player.Position.X, player.Position.Z),
                    target.Position);
                if (dist < bestPriorityDist)
                {
                    bestPriority = npc;
                    bestPriorityDist = dist;
                }
            }

            if (bestPriority != null)
            {
                services.TargetManager.Target = bestPriority;
                this.lastTargetSwitchAt = DateTime.UtcNow;
                return;
            }
        }

        IBattleChara? best = null;
        var bestDist = float.MaxValue;
        foreach (var obj in services.ObjectTable.OfType<IBattleNpc>())
        {
            if (!obj.IsHostile())
            {
                continue;
            }

            if (!obj.StatusFlags.HasFlag(StatusFlags.InCombat))
            {
                continue;
            }

            if (obj.IsDead || obj.CurrentHp == 0)
            {
                continue;
            }

            var dist = Vector2.Distance(
                new Vector2(player.Position.X, player.Position.Z),
                new Vector2(obj.Position.X, obj.Position.Z));
            if (dist < bestDist)
            {
                best = obj;
                bestDist = dist;
            }
        }

        if (best != null)
        {
            services.TargetManager.Target = best;
            this.lastTargetSwitchAt = DateTime.UtcNow;
        }
    }

    private bool HasValidCurrentCombatTarget()
    {
        return services.TargetManager.Target is IBattleNpc currentTarget &&
               currentTarget.StatusFlags.HasFlag(StatusFlags.InCombat) &&
               !currentTarget.IsDead &&
               currentTarget.CurrentHp > 0 &&
               currentTarget.IsHostile();
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


    private void InjectCentroidHold(
        object hints,
        BossModPathfindBoundsSnapshot? pathfindBounds,
        List<TargetSnapshot> targets,
        RsrAoeActionSnapshot? action,
        bool inAoeSituation,
        ICollection<BossModGoalContribution> contributions)
    {
        var aoeRange = action != null && !ShouldSkipBodyReposition(action) ? action.EffectRange : (float?)null;
        if (!this.TryInjectPackCenterMovement(hints, pathfindBounds, targets, "holding near pack", aoeRange, inAoeSituation, contributions))
        {
            this.ClearAoeCandidateGoal();
        }
    }

    private bool IsPackCenterMovementEnabled() => config.ManageTargetUptime;

    private static bool ShouldSkipBodyReposition(RsrAoeActionSnapshot action)
    {
        return action.IsTargetArea ||
               action.IsTargetCenteredCircle ||
               action.Shape == RsrAoeShape.Circle && action.Range > action.EffectRange + 3f;
    }

    private bool TrySelectBestTargetCenteredAoePrimary(
        RsrAoeActionSnapshot action,
        IReadOnlyList<TargetSnapshot> targets,
        IBattleChara player,
        out ulong primaryId,
        out int currentHits,
        out int bestHits)
    {
        primaryId = 0;
        currentHits = 0;
        bestHits = 0;
        if (!action.IsTargetCenteredCircle || targets.Count < 2)
        {
            return false;
        }

        var currentTargetId = services.TargetManager.Target?.GameObjectId ?? 0;
        var playerPos = new Vector2(player.Position.X, player.Position.Z);
        foreach (var target in targets)
        {
            if (!TargetInActionRange(playerPos, player.HitboxRadius, target, action.Range))
            {
                continue;
            }

            var hits = CountTargetCenteredCircleHits(target, targets, action.EffectRange);
            var isCurrent = target.InstanceId == currentTargetId;
            if (isCurrent)
            {
                currentHits = hits;
            }

            if (hits > bestHits ||
                hits == bestHits && isCurrent)
            {
                primaryId = target.InstanceId;
                bestHits = hits;
            }
        }

        return primaryId != 0 &&
               bestHits >= 2 &&
               bestHits > currentHits;
    }

    private void ClearAoeCandidateGoal()
    {
        this.lastSuggestion = null;
        this.lastInjectedGoalDelegate = null;
        this.pendingCandidate = default;
        this.pendingCandidateHits = 0;
        this.pendingCandidateActionId = 0;
        this.pendingCandidateSince = DateTime.MinValue;
        this.lastInjectedActionId = 0;
        this.lastInjectedPrimaryId = 0;
        this.lastSuggestionActionId = 0;
        this.lastSuggestionCurrentHits = 0;
        this.lastSuggestionBestHits = 0;
        this.lastSuggestionCandidate = default;
        this.lastSuggestionHeldUntil = DateTime.MinValue;
    }

    private void UpdateAoeSuggestion(
        GoalPlan plan,
        RsrAoeActionSnapshot action,
        Vector2 candidate,
        int currentHits,
        int bestHits,
        float y,
        DateTime now)
    {
        var suggestionChanged =
            this.lastSuggestion == null ||
            action.AdjustedActionId != this.lastSuggestionActionId ||
            currentHits != this.lastSuggestionCurrentHits ||
            bestHits != this.lastSuggestionBestHits ||
            Vector2.Distance(candidate, this.lastSuggestionCandidate) > SuggestionMovementThreshold;
        if (suggestionChanged)
        {
            this.lastSuggestion = plan.CreateOverlay(action, candidate, currentHits, bestHits, y);
            this.lastSuggestionActionId = action.AdjustedActionId;
            this.lastSuggestionCurrentHits = currentHits;
            this.lastSuggestionBestHits = bestHits;
            this.lastSuggestionCandidate = candidate;
        }

        this.lastSuggestionHeldUntil = now.Add(SuggestionHold);
    }

    private void ExpireAoeSuggestion(DateTime now)
    {
        if (this.lastSuggestion != null && now > this.lastSuggestionHeldUntil)
        {
            this.lastSuggestion = null;
            this.lastSuggestionActionId = 0;
            this.lastSuggestionCurrentHits = 0;
            this.lastSuggestionBestHits = 0;
            this.lastSuggestionCandidate = default;
        }
    }

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

        if (ShouldSkipBodyReposition(action))
        {
            return false;
        }

        var radius = Math.Max(1f, action.EffectRange);
        var castRange = Math.Min(Math.Max(action.Range, radius), 30f);
        var playerPos = new Vector2(player.Position.X, player.Position.Z);
        var targetArray = targets.ToArray();
        var primaryCandidates = action.Shape == RsrAoeShape.Circle
            ? [this.ResolvePrimaryTarget(action, targets) ?? targets[0]]
            : targetArray;

        var bestHits = -1;
        foreach (var primary in primaryCandidates)
        {
            var plan = new GoalPlan(
                action.Shape,
                targetArray,
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

    private bool TryInjectProactivePackAoeMovement(
        object hints,
        BossModPathfindBoundsSnapshot? pathfindBounds,
        List<TargetSnapshot> targets,
        bool inAoeSituation,
        ICollection<BossModGoalContribution> contributions)
    {
        if (!inAoeSituation ||
            targets.Count < 2 ||
            jobRangeProvider.PackAoeRange > ProactivePackAoeMaxBodyRange)
        {
            return false;
        }

        if (this.goalZonesField!.GetValue(hints) is not IList)
        {
            this.lastReason = "BMR goal zone list unavailable";
            return false;
        }

        var player = services.ObjectTable.LocalPlayer;
        if (player == null)
        {
            this.lastReason = "local player unavailable";
            return false;
        }

        var playerPos = new Vector2(player.Position.X, player.Position.Z);
        var primary = targets[0];
        var preferredPrimaryId = this.SelectPackPrimaryTarget(targets);
        foreach (var target in targets)
        {
            if (target.InstanceId == preferredPrimaryId)
            {
                primary = target;
                break;
            }
        }

        var radius = Math.Max(1f, jobRangeProvider.PackAoeRange);
        var targetArray = targets.ToArray();
        var plan = new GoalPlan(
            RsrAoeShape.Circle,
            targetArray,
            primary,
            radius,
            radius,
            halfWidth: 0f,
            minHits: 2);
        var currentHits = plan.ScoreHits(playerPos);
        var retainedCandidate = this.lastInjectedGoalDelegate != null &&
                                this.lastInjectedActionId == 0 &&
                                this.lastInjectedPrimaryId == primary.InstanceId
            ? this.lastInjectedCandidate
            : (Vector2?)null;
        var candidate = plan.FindBestCandidate(
            playerPos,
            candidate => CandidateInsidePathfindBounds(pathfindBounds, candidate),
            retainedCandidate);
        this.lastCurrentHits = currentHits;
        this.lastBestHits = candidate.Hits;
        this.lastBestPrimaryId = primary.InstanceId;

        if (candidate.Hits <= currentHits || candidate.Hits < 2)
        {
            return false;
        }

        var moveDistance = Vector2.Distance(playerPos, candidate.Position);
        if (ShouldSkipMarginalAoeReposition(currentHits, candidate.Hits, targets.Count, moveDistance, out var marginalSkipReason))
        {
            this.lastReason = marginalSkipReason;
            return false;
        }

        var candidateChanged = this.lastInjectedGoalDelegate == null ||
                               this.lastInjectedActionId != 0 ||
                               this.lastInjectedPrimaryId != primary.InstanceId ||
                               this.lastInjectedHits != candidate.Hits ||
                               Vector2.Distance(candidate.Position, this.lastInjectedCandidate) > CandidateMovementThreshold;
        if (candidateChanged)
        {
            this.lastInjectedGoalDelegate = plan.CreateGoalDelegate(this.resolvedWPosType!, this.wposXField!, this.wposZField!, currentHits);
            this.lastInjectedCandidate = candidate.Position;
            this.lastInjectedHits = candidate.Hits;
            this.lastInjectedActionId = 0;
            this.lastInjectedPrimaryId = primary.InstanceId;
        }

        contributions.Add(new(this.lastInjectedGoalDelegate!, BossModGoalPriority.Uptime, "AoE pack prep", candidate.Position, MechanicWhisperConfidence.Confident));
        this.lastAction = null;
        this.lastInjected = true;
        var y = player.Position.Y;
        this.lastOverlay = new AoePackOverlaySnapshot(
            0,
            "Pack AoE prep",
            RsrAoeShape.Circle.ToString(),
            new Vector3(candidate.Position.X, y, candidate.Position.Y),
            new Vector3(primary.Position.X, y, primary.Position.Y),
            radius,
            0f,
            currentHits,
            candidate.Hits,
            this.CreateOverlayTargets(targets, y, candidate.Position, hit: false));
        this.lastReason = string.Create(
            CultureInfo.InvariantCulture,
            $"pre-positioning for pack AoE ({currentHits}/{targets.Count} -> {candidate.Hits}/{targets.Count})");

        return true;
    }

    private bool TryInjectPackCenterMovement(
        object hints,
        BossModPathfindBoundsSnapshot? pathfindBounds,
        List<TargetSnapshot> targets,
        string reason,
        float? aoeRange,
        bool inAoeSituation,
        ICollection<BossModGoalContribution> contributions)
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

        var playerPos = new Vector2(player.Position.X, player.Position.Z);
        var primary = targets[0];
        var centroid = AverageTargets(targets);
        var engagementRange = aoeRange ?? (inAoeSituation ? jobRangeProvider.PackAoeRange : jobRangeProvider.EngagementRange);
        var useLongRangeCasterPullFollow = this.ShouldUseLongRangeCasterPullFollow(inAoeSituation, playerPos, centroid, engagementRange);
        if (useLongRangeCasterPullFollow)
        {
            reason += " (long-range caster follow)";
        }

        var packMotion = this.UpdatePackMotion(centroid, DateTime.UtcNow);

        var inRangeCount = 0;
        foreach (var t in targets)
        {
            var d = Vector2.Distance(playerPos, t.Position) - t.Radius;
            if (d <= engagementRange)
            {
                inRangeCount++;
            }
        }

        var requiredInRangeCount = RequiredPackEngagementTargets(inAoeSituation, engagementRange, targets.Count);
        if (!useLongRangeCasterPullFollow &&
            this.TryInjectFluidPackFollow(pathfindBounds, targets, player, playerPos, centroid, packMotion, engagementRange, inAoeSituation, contributions))
        {
            return true;
        }

        if (inRangeCount >= requiredInRangeCount)
        {
            this.ClearAoeCandidateGoal();
            this.lastReason = requiredInRangeCount > 1
                ? $"already in AoE range of pack: {inRangeCount}/{requiredInRangeCount}"
                : "already in engagement range of pack";
            return false;
        }

        if (useLongRangeCasterPullFollow &&
            this.TryInjectFluidPackFollow(pathfindBounds, targets, player, playerPos, centroid, packMotion, engagementRange, inAoeSituation, contributions))
        {
            return true;
        }

        var targetArray = targets.ToArray();
        var plan = new GoalPlan(
            RsrAoeShape.Circle,
            targetArray,
            primary,
            radius: engagementRange,
            range: engagementRange,
            halfWidth: 0f,
            minHits: requiredInRangeCount);

        var retainedCandidate = this.lastCentroidGoalDelegate != null
            ? this.lastInjectedCentroid
            : (Vector2?)null;
        var candidate = plan.FindBestCandidate(
            playerPos,
            candidate => CandidateInsidePathfindBounds(pathfindBounds, candidate),
            retainedCandidate);
        if (candidate.Hits <= 0)
        {
            this.lastCentroidGoalDelegate = null;
            this.lastInjectedCentroid = default;
            this.lastReason = $"{reason}: no reachable pack engagement position";
            return false;
        }

        if (this.lastCentroidGoalDelegate == null ||
            Vector2.Distance(candidate.Position, this.lastInjectedCentroid) > CentroidMovementThreshold)
        {
            this.lastCentroidGoalDelegate = plan.CreateGoalDelegate(this.resolvedWPosType!, this.wposXField!, this.wposZField!, 0);
            this.lastInjectedCentroid = candidate.Position;
        }

        contributions.Add(new(this.lastCentroidGoalDelegate, BossModGoalPriority.Uptime, "Pack engagement", candidate.Position, MechanicWhisperConfidence.Confident));
        this.lastAction = null;
        this.lastCurrentHits = 0;
        this.lastBestHits = candidate.Hits;
        this.ClearAoeCandidateGoal();
        this.lastInjected = true;
        this.lastReason = reason;
        var y = player.Position.Y;
        this.lastOverlay = new AoePackOverlaySnapshot(
            0,
            "Pack engagement",
            RsrAoeShape.Circle.ToString(),
            new Vector3(candidate.Position.X, y, candidate.Position.Y),
            new Vector3(centroid.X, y, centroid.Y),
            engagementRange,
            0f,
            0,
            candidate.Hits,
            this.CreateOverlayTargets(targets, y, candidate.Position, hit: false));
        return true;
    }

    private bool TryInjectFluidPackFollow(
        BossModPathfindBoundsSnapshot? pathfindBounds,
        List<TargetSnapshot> targets,
        IBattleChara player,
        Vector2 playerPos,
        Vector2 centroid,
        PackMotion packMotion,
        float engagementRange,
        bool inAoeSituation,
        ICollection<BossModGoalContribution> contributions)
    {
        if (!inAoeSituation || packMotion.Speed < FluidPackFollowMinimumSpeed)
        {
            return false;
        }

        var desiredTrailingDistance = this.ResolvePackFollowDistance(engagementRange);
        if (Vector2.Distance(playerPos, centroid) <= desiredTrailingDistance + FluidPackFollowSlack)
        {
            return false;
        }

        var direction = packMotion.Direction;
        if (direction.LengthSquared() <= 0.01f)
        {
            direction = centroid - playerPos;
            if (direction.LengthSquared() <= 0.01f)
            {
                return false;
            }

            direction = Vector2.Normalize(direction);
        }

        var distanceToCentroid = Vector2.Distance(playerPos, centroid);
        var candidate = centroid - direction * desiredTrailingDistance;
        var catchupSeconds = packMotion.Speed > 0.5f
            ? Math.Clamp((distanceToCentroid - desiredTrailingDistance) / packMotion.Speed, 0f, 1f)
            : 0f;
        if (catchupSeconds > 0f)
        {
            var projectedCentroid = centroid + direction * packMotion.Speed * catchupSeconds * 0.75f;
            var projectedCandidate = projectedCentroid - direction * desiredTrailingDistance;
            var projectedHits = CountTargetsInRange(projectedCandidate, targets, engagementRange);
            var currentHits = CountTargetsInRange(candidate, targets, engagementRange);
            if (projectedHits >= currentHits && CandidateInsidePathfindBounds(pathfindBounds, projectedCandidate))
            {
                candidate = projectedCandidate;
            }
        }

        if (!CandidateInsidePathfindBounds(pathfindBounds, candidate))
        {
            candidate = centroid;
            if (!CandidateInsidePathfindBounds(pathfindBounds, candidate))
            {
                this.lastReason = "moving pack follow destination outside BMR bounds";
                return false;
            }
        }

        var candidateHits = CountTargetsInRange(candidate, targets, engagementRange);
        if (candidateHits <= 0)
        {
            return false;
        }

        if (this.lastCentroidGoalDelegate == null ||
            Vector2.Distance(candidate, this.lastInjectedCentroid) > FluidPackFollowMovementThreshold)
        {
            this.lastCentroidGoalDelegate = this.CreateCentroidGoalDelegate(candidate, acceptRadius: 2.5f);
            this.lastInjectedCentroid = candidate;
        }

        contributions.Add(new(this.lastCentroidGoalDelegate, BossModGoalPriority.Uptime, "Pack engagement", candidate, MechanicWhisperConfidence.Confident));
        this.lastAction = null;
        this.lastCurrentHits = CountTargetsInRange(playerPos, targets, engagementRange);
        this.lastBestHits = candidateHits;
        this.ClearAoeCandidateGoal();
        this.lastInjected = true;
        this.lastReason = string.Create(CultureInfo.InvariantCulture, $"following moving trash pack ({packMotion.Speed:0.0}y/s)");
        var y = player.Position.Y;
        this.lastOverlay = new AoePackOverlaySnapshot(
            0,
            "Pack engagement",
            RsrAoeShape.Circle.ToString(),
            new Vector3(candidate.X, y, candidate.Y),
            new Vector3(centroid.X, y, centroid.Y),
            engagementRange,
            0f,
            this.lastCurrentHits,
            this.lastBestHits,
            this.CreateOverlayTargets(targets, y, candidate, hit: false));
        return true;
    }

    private PackMotion UpdatePackMotion(Vector2 centroid, DateTime now)
    {
        var direction = this.lastPackDirection;
        var speed = 0f;
        if (this.lastPackCentroidAt != DateTime.MinValue)
        {
            var elapsed = (float)(now - this.lastPackCentroidAt).TotalSeconds;
            var delta = centroid - this.lastPackCentroid;
            var distance = delta.Length();
            if (elapsed is >= 0.15f and <= 2f && distance is > 0.05f and <= MaxPackCentroidStep)
            {
                speed = Math.Min(distance / elapsed, MaxObservedPackSpeed);
                direction = delta / distance;
            }
        }

        this.lastPackCentroid = centroid;
        this.lastPackCentroidAt = now;
        if (direction.LengthSquared() > 0.01f)
        {
            this.lastPackDirection = direction;
        }

        return new PackMotion(direction, speed);
    }

    private static int CountTargetsInRange(Vector2 position, IEnumerable<TargetSnapshot> targets, float engagementRange)
    {
        var count = 0;
        foreach (var target in targets)
        {
            if (Vector2.Distance(position, target.Position) - target.Radius <= engagementRange)
            {
                count++;
            }
        }

        return count;
    }

    private static bool TargetInActionRange(Vector2 playerPosition, float playerRadius, TargetSnapshot target, float range)
    {
        return Vector2.Distance(playerPosition, target.Position) - playerRadius - target.Radius <= range;
    }

    private static int CountTargetCenteredCircleHits(TargetSnapshot primary, IEnumerable<TargetSnapshot> targets, float radius)
    {
        var count = 0;
        foreach (var target in targets)
        {
            var effective = radius + target.Radius;
            if (Vector2.DistanceSquared(primary.Position, target.Position) <= effective * effective)
            {
                count++;
            }
        }

        return count;
    }

    private static Vector2 AverageTargets(IReadOnlyCollection<TargetSnapshot> targets)
    {
        return targets.Aggregate(Vector2.Zero, (acc, target) => acc + target.Position) / targets.Count;
    }

    private static int RequiredPackEngagementTargets(bool inAoeSituation, float engagementRange, int targetCount)
    {
        if (!inAoeSituation || targetCount <= 1)
        {
            return 1;
        }

        // Close-range AoE jobs need to actually be in the pack before ABC spends GCDs.
        // Requiring only two hits leaves them at the edge of trash pulls and can stall AoE flow.
        if (engagementRange <= 8f)
        {
            if (targetCount <= 4)
            {
                return targetCount;
            }

            return Math.Min(targetCount, Math.Max(4, (int)MathF.Ceiling(targetCount * 0.75f)));
        }

        return 1;
    }

    internal static bool ShouldSkipMarginalAoeReposition(int currentHits, int bestHits, int targetCount, float moveDistance, out string reason)
    {
        reason = string.Empty;
        var gain = bestHits - currentHits;
        if (gain <= 0 || targetCount <= 1)
        {
            return false;
        }

        var goodEnoughHits = GoodEnoughTrashAoeHits(targetCount);
        if (currentHits < goodEnoughHits)
        {
            return false;
        }

        var maxMoveDistance = gain switch
        {
            <= 1 => MarginalAoeGainMaxMoveDistance,
            2 => ModerateAoeGainMaxMoveDistance,
            _ => StrongAoeGainMaxMoveDistance
        };
        if (moveDistance <= maxMoveDistance)
        {
            return false;
        }

        reason = string.Create(
            CultureInfo.InvariantCulture,
            $"already good AoE coverage ({currentHits}/{targetCount}); skipped {moveDistance:0.0}y move for +{gain} hit");
        return true;
    }

    private static int GoodEnoughTrashAoeHits(int targetCount)
    {
        if (targetCount <= 1)
        {
            return targetCount;
        }

        if (targetCount <= 3)
        {
            return targetCount;
        }

        if (targetCount <= 5)
        {
            return targetCount - 1;
        }

        return Math.Min(targetCount, Math.Max(4, (int)MathF.Ceiling(targetCount * 0.75f)));
    }

    private BossModPathfindBoundsSnapshot? CreatePathfindBoundsSnapshot(object hints)
    {
        if (this.pathfindMapCenterField == null ||
            this.pathfindMapBoundsField == null ||
            this.wposXField == null ||
            this.wposZField == null ||
            this.wdirConstructor == null ||
            this.boundsContainsMethod == null)
        {
            return null;
        }

        return BossModPathfindBoundsSnapshot.TryCreate(
            hints,
            this.pathfindMapCenterField,
            this.pathfindMapBoundsField,
            this.wposXField,
            this.wposZField,
            this.wdirConstructor,
            this.boundsContainsMethod,
            out var snapshot)
            ? snapshot
            : null;
    }

    private static bool CandidateInsidePathfindBounds(BossModPathfindBoundsSnapshot? bounds, Vector2 candidate)
    {
        return bounds?.Contains(candidate) ?? true;
    }

    private AoePackOverlayTarget[] CreateOverlayTargets(IEnumerable<TargetSnapshot> targets, float y, Vector2 candidate, bool hit)
    {
        return targets.Select(target => new AoePackOverlayTarget(
            new Vector3(target.Position.X, y, target.Position.Y),
            target.Radius,
            hit,
            false)).ToArray();
    }

    private readonly record struct PackMotion(Vector2 Direction, float Speed);

    private void RestoreRsrIfNeeded()
    {
        if (!this.rsrHenchedActive)
        {
            return;
        }

        try
        {
            if (rotationSolverIpc.RestoreMode(this.rsrSnapshotMode))
            {
                this.rsrRestoreStatus = $"restored {this.rsrSnapshotMode}";
                this.rsrLastRestoreStatus = $"snapshot {this.rsrSnapshotMode} restored";
            }
            else
            {
                this.rsrRestoreStatus = "restore unavailable";
                this.rsrLastRestoreStatus = $"restore {this.rsrSnapshotMode} unavailable";
            }
        }
        catch (Exception ex)
        {
            this.rsrRestoreStatus = "restore failed";
            this.rsrLastRestoreStatus = $"restore {this.rsrSnapshotMode} failed";
            services.Log.Verbose(ex, "Could not restore Rotation Solver Reborn mode.");
        }
        finally
        {
            this.rsrHenchedActive = false;
        }
    }

    private bool ShouldRestoreRsrAfterNoAction(string reason, bool shouldUseRsrTargetControl)
    {
        return !shouldUseRsrTargetControl;
    }

    private bool EnsureResolved(Type hintsType)
    {
        if (this.resolvedHintsType == hintsType &&
            this.goalZonesField != null &&
            this.forcedMovementField != null &&
            this.forbiddenZonesField != null &&
            this.pathfindMapCenterField != null &&
            this.pathfindMapBoundsField != null &&
            this.potentialTargetsField != null &&
            this.priorityTargetsProperty != null &&
            this.enemyActorField != null &&
            this.actorPositionField != null &&
            this.actorHitboxRadiusField != null &&
            this.actorInstanceIdField != null &&
            this.wposXField != null &&
            this.wposZField != null &&
            this.wdirConstructor != null &&
            this.boundsContainsMethod != null)
        {
            return true;
        }

        const BindingFlags InstanceFlags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        var goalZones = hintsType.GetField("GoalZones", InstanceFlags);
        var forcedMovement = hintsType.GetField("ForcedMovement", InstanceFlags);
        var forbiddenZones = hintsType.GetField("ForbiddenZones", InstanceFlags);
        var pathfindMapCenter = hintsType.GetField("PathfindMapCenter", InstanceFlags);
        var pathfindMapBounds = hintsType.GetField("PathfindMapBounds", InstanceFlags);
        var potentialTargets = hintsType.GetField("PotentialTargets", InstanceFlags);
        var priorityTargets = hintsType.GetProperty("PriorityTargets", InstanceFlags);
        var wposType = hintsType.Assembly.GetType("BossMod.WPos");
        var wdirType = hintsType.Assembly.GetType("BossMod.WDir");
        var enemyType = hintsType.GetNestedType("Enemy", BindingFlags.Public);
        var actorField = enemyType?.GetField("Actor", InstanceFlags);
        var actorType = actorField?.FieldType;
        var positionField = (MemberInfo?)actorType?.GetProperty("Position", InstanceFlags) ?? actorType?.GetField("Position", InstanceFlags);
        var hitboxField = (MemberInfo?)actorType?.GetField("HitboxRadius", InstanceFlags) ?? actorType?.GetProperty("HitboxRadius", InstanceFlags);
        var instanceField = (MemberInfo?)actorType?.GetField("InstanceID", InstanceFlags) ?? actorType?.GetProperty("InstanceID", InstanceFlags);
        var xField = wposType?.GetField("X", InstanceFlags);
        var zField = wposType?.GetField("Z", InstanceFlags);
        var wdirConstructor = wdirType?.GetConstructor([typeof(float), typeof(float)]);
        var boundsContainsMethod = wdirType == null
            ? null
            : pathfindMapBounds?.FieldType.GetMethods(InstanceFlags)
                .FirstOrDefault(method =>
                {
                    if (method.Name != "Contains")
                    {
                        return false;
                    }

                    var parameters = method.GetParameters();
                    if (parameters.Length != 1)
                    {
                        return false;
                    }

                    var parameterType = parameters[0].ParameterType;
                    return parameterType == wdirType || (parameterType.IsByRef && parameterType.GetElementType() == wdirType);
                });
        if (goalZones == null || forcedMovement == null || forbiddenZones == null || pathfindMapCenter == null || pathfindMapBounds == null || potentialTargets == null || priorityTargets == null || wposType == null || wdirType == null || actorField == null || positionField == null || hitboxField == null || instanceField == null || xField == null || zField == null || wdirConstructor == null || boundsContainsMethod == null)
        {
            this.lastReason = $"BMR AoE goal reflection members unavailable: {FormatMissing(
                (goalZones == null, "AIHints.GoalZones"),
                (forcedMovement == null, "AIHints.ForcedMovement"),
                (forbiddenZones == null, "AIHints.ForbiddenZones"),
                (pathfindMapCenter == null, "AIHints.PathfindMapCenter"),
                (pathfindMapBounds == null, "AIHints.PathfindMapBounds"),
                (potentialTargets == null, "AIHints.PotentialTargets"),
                (priorityTargets == null, "AIHints.PriorityTargets"),
                (wposType == null, "BossMod.WPos"),
                (wdirType == null, "BossMod.WDir"),
                (enemyType == null, "AIHints.Enemy"),
                (actorField == null, "AIHints.Enemy.Actor"),
                (positionField == null, "Enemy.Actor.Position"),
                (hitboxField == null, "Enemy.Actor.HitboxRadius"),
                (instanceField == null, "Enemy.Actor.InstanceID"),
                (xField == null, "BossMod.WPos.X"),
                (zField == null, "BossMod.WPos.Z"),
                (wdirConstructor == null, "BossMod.WDir(float,float)"),
                (boundsContainsMethod == null, "ArenaBounds.Contains"))}";
            return false;
        }

        this.resolvedHintsType = hintsType;
        this.resolvedWPosType = wposType;
        this.goalZonesField = goalZones;
        this.forcedMovementField = forcedMovement;
        this.forbiddenZonesField = forbiddenZones;
        this.pathfindMapCenterField = pathfindMapCenter;
        this.pathfindMapBoundsField = pathfindMapBounds;
        this.potentialTargetsField = potentialTargets;
        this.priorityTargetsProperty = priorityTargets;
        this.enemyActorField = actorField;
        this.actorPositionField = (MemberInfo)positionField;
        this.actorHitboxRadiusField = (MemberInfo)hitboxField;
        this.actorInstanceIdField = (MemberInfo)instanceField;
        this.wposXField = xField;
        this.wposZField = zField;
        this.wdirConstructor = wdirConstructor;
        this.boundsContainsMethod = boundsContainsMethod;
        return true;
    }

    private static object? GetMemberValue(MemberInfo? member, object? obj) => member switch
    {
        FieldInfo f => f.GetValue(obj),
        PropertyInfo p => p.GetValue(obj),
        _ => null
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

    private List<TargetSnapshot> SelectEffectivePackTargets(IReadOnlyList<TargetSnapshot> priorityTargets, IReadOnlyList<TargetSnapshot> potentialTargets)
    {
        if (priorityTargets.Count >= 2)
        {
            return this.SelectDominantPackCluster(priorityTargets);
        }

        if (potentialTargets.Count >= 2)
        {
            return this.SelectDominantPackCluster(potentialTargets);
        }

        return priorityTargets.ToList();
    }

    private List<TargetSnapshot> SelectDominantPackCluster(IReadOnlyList<TargetSnapshot> targets)
    {
        if (targets.Count <= 2)
        {
            return targets.ToList();
        }

        var anchor = this.ResolvePackAnchor();
        var currentTargetId = services.TargetManager.Target?.GameObjectId ?? 0;
        List<TargetSnapshot>? best = null;
        var bestCount = 0;
        var bestAnchorDistance = float.MaxValue;
        var bestContainsCurrent = false;

        foreach (var seed in targets)
        {
            var cluster = targets
                .Where(target =>
                    target.InstanceId == seed.InstanceId ||
                    Vector2.Distance(target.Position, seed.Position) - target.Radius - seed.Radius <= DominantPackClusterRadius)
                .OrderBy(target => Vector2.DistanceSquared(target.Position, seed.Position))
                .ThenBy(target => target.InstanceId)
                .ToList();
            if (cluster.Count < 2)
            {
                continue;
            }

            var anchorDistance = AverageDistanceSquared(cluster, anchor);
            var containsCurrent = currentTargetId != 0 && cluster.Any(target => target.InstanceId == currentTargetId);
            if (cluster.Count > bestCount ||
                cluster.Count == bestCount && anchorDistance < bestAnchorDistance ||
                cluster.Count == bestCount && MathF.Abs(anchorDistance - bestAnchorDistance) <= 1f && containsCurrent && !bestContainsCurrent)
            {
                best = cluster;
                bestCount = cluster.Count;
                bestAnchorDistance = anchorDistance;
                bestContainsCurrent = containsCurrent;
            }
        }

        return best ?? targets.ToList();
    }

    private List<TargetSnapshot> ApplyRemotePackCurrentTargetFallback(List<TargetSnapshot> effectiveTargets, IReadOnlyList<TargetSnapshot> allTargets)
    {
        if (effectiveTargets.Count < 2 || allTargets.Count == 0)
        {
            return effectiveTargets;
        }

        if (services.ObjectTable.LocalPlayer is not { } player ||
            services.TargetManager.Target is not IBattleNpc currentTarget ||
            !currentTarget.StatusFlags.HasFlag(StatusFlags.InCombat) ||
            currentTarget.IsDead ||
            currentTarget.CurrentHp == 0 ||
            !currentTarget.IsHostile())
        {
            return effectiveTargets;
        }

        var currentId = currentTarget.GameObjectId;
        if (effectiveTargets.Any(target => target.InstanceId == currentId))
        {
            return effectiveTargets;
        }

        TargetSnapshot? currentSnapshot = null;
        foreach (var target in allTargets)
        {
            if (target.InstanceId == currentId)
            {
                currentSnapshot = target;
                break;
            }
        }

        if (!currentSnapshot.HasValue)
        {
            return effectiveTargets;
        }

        var playerPos = new Vector2(player.Position.X, player.Position.Z);
        var current = currentSnapshot.Value;
        var currentSurfaceDistance = Vector2.Distance(playerPos, current.Position) - current.Radius;
        if (currentSurfaceDistance > LocalTrashTargetRetainSurfaceDistance)
        {
            return effectiveTargets;
        }

        var remoteCentroid = AverageTargets(effectiveTargets);
        if (Vector2.Distance(playerPos, remoteCentroid) < RemotePackSwitchDistance)
        {
            return effectiveTargets;
        }

        var anchor = this.ResolvePackAnchor();
        var playerAnchorDistance = Vector2.Distance(playerPos, anchor);
        var currentAnchorDistance = Vector2.Distance(current.Position, anchor) - current.Radius;
        var remoteAnchorDistance = Vector2.Distance(remoteCentroid, anchor);
        if (!ShouldRetainLocalTrashTarget(
                playerAnchorDistance,
                currentSurfaceDistance,
                currentAnchorDistance,
                remoteAnchorDistance))
        {
            return effectiveTargets;
        }

        var localTargets = allTargets
            .Where(target =>
                target.InstanceId == currentId ||
                Vector2.Distance(target.Position, current.Position) - target.Radius - current.Radius <= DominantPackClusterRadius)
            .OrderBy(target => Vector2.DistanceSquared(target.Position, current.Position))
            .ThenBy(target => target.InstanceId)
            .ToList();

        return localTargets.Count > 0 ? localTargets : effectiveTargets;
    }

    internal static bool ShouldRetainLocalTrashTarget(float playerAnchorDistance, float currentSurfaceDistance, float currentAnchorDistance, float remoteAnchorDistance)
    {
        if (currentSurfaceDistance > LocalTrashTargetRetainSurfaceDistance)
        {
            return false;
        }

        if (playerAnchorDistance > LocalTrashTargetRetainTankDistance &&
            remoteAnchorDistance + TankSidePackAnchorGain < currentAnchorDistance)
        {
            return false;
        }

        return true;
    }

    private Vector2 ResolvePackAnchor()
    {
        var player = services.ObjectTable.LocalPlayer;
        if (player == null)
        {
            return Vector2.Zero;
        }

        var tank = PartyAllyProvider.SelectBestTank(services, player);
        if (tank != null)
        {
            return new Vector2(tank.Position.X, tank.Position.Z);
        }

        return new Vector2(player.Position.X, player.Position.Z);
    }

    private TrashPullDiagnostics UpdateTrashPullState(
        IReadOnlyList<TargetSnapshot> dominantTargets,
        IReadOnlyList<TargetSnapshot> allTargets,
        bool trashContext,
        bool bossContext,
        bool bmrSafetyPressure)
    {
        var player = services.ObjectTable.LocalPlayer;
        if (player == null)
        {
            this.trashPullState.Reset("local player unavailable");
            return this.trashPullState.Current;
        }

        var tank = PartyAllyProvider.SelectBestTank(services, player);
        var partyMembers = PartyAllyProvider.GetVisiblePartyAllies(services, player).Members
            .Select(member => new TrashPullActorPosition(member.GameObjectId, member.Position))
            .ToArray();
        var observation = new TrashPullObservation(
            DateTime.UtcNow,
            CombatEngagementDetector.IsEffectivelyInCombat(services),
            trashContext,
            bossContext,
            automatedMovementSuppressed(),
            bmrSafetyPressure,
            player.Position,
            jobRangeProvider.EngagementRange,
            jobRangeProvider.PackAoeRange,
            tank == null ? null : new TrashPullActorPosition(tank.GameObjectId, tank.Position),
            partyMembers,
            dominantTargets,
            allTargets);
        return this.trashPullState.Update(observation);
    }

    private bool TryInjectTankLeadMovement(
        BossModPathfindBoundsSnapshot? pathfindBounds,
        TrashPullDiagnostics diagnostics,
        IReadOnlyList<TargetSnapshot> targets,
        ICollection<BossModGoalContribution> contributions)
    {
        if (!config.LeadTrashPullsWithTank ||
            !config.ManageMovement ||
            !config.ManageTargetUptime ||
            !diagnostics.LeadCandidateActive ||
            diagnostics.LeadDestination == null)
        {
            return false;
        }

        var player = services.ObjectTable.LocalPlayer;
        if (player == null)
        {
            this.lastReason = "local player unavailable";
            return false;
        }

        var destination = diagnostics.LeadDestination.Value;
        var destination2 = new Vector2(destination.X, destination.Z);
        if (!CandidateInsidePathfindBounds(pathfindBounds, destination2))
        {
            this.lastReason = "tank lead outside BMR bounds";
            return false;
        }

        var player2 = new Vector2(player.Position.X, player.Position.Z);
        if (Vector2.Distance(player2, destination2) <= 1.25f)
        {
            this.lastReason = "tank lead already reached";
            return false;
        }

        if (this.lastCentroidGoalDelegate == null ||
            Vector2.Distance(destination2, this.lastInjectedCentroid) > FluidPackFollowMovementThreshold)
        {
            this.lastCentroidGoalDelegate = this.CreateCentroidGoalDelegate(destination2, acceptRadius: 2.5f);
            this.lastInjectedCentroid = destination2;
        }

        contributions.Add(new(this.lastCentroidGoalDelegate, BossModGoalPriority.Uptime, "Tank pull lead", destination2, MechanicWhisperConfidence.Confident));
        this.lastAction = null;
        this.lastCurrentHits = 0;
        this.lastBestHits = Math.Max(0, diagnostics.DominantTargetCount);
        this.ClearAoeCandidateGoal();
        this.lastInjected = true;
        this.lastReason = string.Create(
            CultureInfo.InvariantCulture,
            $"following tank lead; behind={diagnostics.BehindDistance?.ToString("0.0", CultureInfo.InvariantCulture) ?? "n/a"}y; {diagnostics.LeadRejectionReason}");
        var centroid = diagnostics.PackCentroid ?? destination;
        this.lastOverlay = new AoePackOverlaySnapshot(
            0,
            "Tank pull lead",
            RsrAoeShape.Circle.ToString(),
            new Vector3(destination.X, player.Position.Y, destination.Z),
            new Vector3(centroid.X, player.Position.Y, centroid.Z),
            jobRangeProvider.PackAoeRange,
            0f,
            0,
            this.lastBestHits,
            this.CreateOverlayTargets(targets, player.Position.Y, destination2, hit: false));
        return true;
    }

    private static float AverageDistanceSquared(IReadOnlyCollection<TargetSnapshot> targets, Vector2 anchor)
    {
        if (targets.Count == 0)
        {
            return 0f;
        }

        var total = 0f;
        foreach (var target in targets)
        {
            total += Vector2.DistanceSquared(target.Position, anchor);
        }

        return total / targets.Count;
    }

    private bool ShouldUseLongRangeCasterPullFollow(bool inAoeSituation, Vector2 playerPos, Vector2 centroid, float engagementRange)
    {
        if (!inAoeSituation || jobRangeProvider.PackAoeRange <= LongRangePackAoeThreshold)
        {
            return false;
        }

        var phase = this.trashPullState.Current.Phase;
        if (phase != TrashPullPhase.Gathering && phase != TrashPullPhase.Stabilizing)
        {
            return false;
        }

        var distanceToPack = Vector2.Distance(playerPos, centroid);
        return distanceToPack > this.ResolvePackFollowDistance(engagementRange) + LongRangeCasterPackFollowSlack;
    }

    private float ResolvePackFollowDistance(float engagementRange)
    {
        if (jobRangeProvider.PackAoeRange > LongRangePackAoeThreshold)
        {
            return Math.Clamp(
                engagementRange * LongRangeCasterPackFollowDistanceFactor,
                LongRangeCasterPackFollowMinDistance,
                LongRangeCasterPackFollowMaxDistance);
        }

        return Math.Clamp(engagementRange * 0.45f, 2.5f, 8f);
    }

    private static List<TargetSnapshot> DistinctTargets(IReadOnlyList<TargetSnapshot> targets)
    {
        return targets
            .GroupBy(target => target.InstanceId)
            .Select(group => group.First())
            .ToList();
    }

    private List<TargetSnapshot> ReadPriorityTargets(object hints)
    {
        if (this.priorityTargetsProperty!.GetValue(hints) is not IEnumerable enemies)
        {
            return [];
        }

        return this.ReadTargets(enemies);
    }

    private List<TargetSnapshot> ReadPotentialTargets(object hints)
    {
        if (this.potentialTargetsField!.GetValue(hints) is not IEnumerable enemies)
        {
            return [];
        }

        return this.ReadTargets(enemies);
    }

    private List<TargetSnapshot> ReadTargets(IEnumerable enemies)
    {
        var result = new List<TargetSnapshot>(8);
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

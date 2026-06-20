using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq.Expressions;
using System.Numerics;
using System.Reflection;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Game.ClientState.Objects.Types;
using FFXIVClientStructs.FFXIV.Client.Game;
using XelsCombatAI.Game;
using XelsCombatAI.Integrations;

namespace XelsCombatAI.Combat;

internal sealed record HealerAoePositioningStatus(
    string HookState,
    string LastReason,
    bool Injected,
    int PartyMembers,
    float DistanceToCenter,
    int CoveredMembers,
    Vector3? Center);

internal sealed record HealerCoverageOverlaySnapshot(
    Vector3 Center,
    float Radius,
    bool Injected,
    float DistanceToCenter,
    int CoveredMembers,
    int TotalMembers,
    IReadOnlyList<Vector3> Members);

internal sealed class HealerAoePositioningController(
    Configuration config,
    DalamudServices services,
    BossModIpc bossMod,
    RotationSolverActionReflection rotationSolverActions,
    Func<bool> automatedMovementSuppressed,
    Func<bool> currentTargetHasBossModule,
    MobilityDecisionEvaluator mobilityEvaluator,
    FacingController facingController,
    Func<BossModMechanicPressure> mechanicPressure)
    : IBossModGoalZoneContributor
{
    private static readonly BindingFlags InstanceFlags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

    // Healer AoE heals and support actions commonly use a 20y radius around the healer.
    private const float CoverageRadius = 20f;
    private const float CoverageRadiusSquared = CoverageRadius * CoverageRadius;
    private const float MaxConvenienceMoveDistance = 6f;
    private const float MaxTankCoverageRestoreDistance = 4.5f;
    private const float MaxRoutineCoverageComfortMoveDistance = 8f;
    private const float MaxMechanicCoverageComfortMoveDistance = 10f;
    private const float MaxDowntimeCoverageComfortMoveDistance = 14f;
    private const float MaxCriticalCoverageCatchUpMoveDistance = 60f;
    private const float MaxPartyAoeHealCatchUpMoveDistance = 30f;
    private const float MaxBossCoverageRoutineMoveDistance = 18f;
    private const float BossRangeComfortSurfaceDistance = 14f;
    private const float MinimumBossCoverageDashDistance = 10f;
    private const float MinimumBossCoverageDashGain = 6f;
    private const int MinimumUrgentCoverageDashGain = 2;
    private const float MaximumBossCoverageDashTargetDistance = 25f;
    private const float BossCoverageDashTargetRadius = 7.5f;
    private const float MinimumPartyAoeHealEffectRange = 8f;
    private const float MinimumCoverageComfortSlackGain = 3f;
    private const float PreferredCoverageSlack = 5f;
    private const float PreferredCoveragePointRadius = 5f;
    private const float MaxCoverageComfortScoreBonus = 0.15f;
    private const float EstimatedCombatMoveSpeed = 6f;
    private const float CoverageArrivalBufferSeconds = 0.15f;
    private const float GcdElapsedResetTolerance = 0.2f;
    private const int MinimumCoverageGain = 2;
    private const float PreferredScore = GoalZoneScorePolicy.NormalPreference;
    private const float TankCoverageBonus = 0.25f;
    private static readonly TimeSpan FallbackGcdWindow = TimeSpan.FromMilliseconds(2500);

    private FieldInfo? goalZonesField;
    private FieldInfo? forcedMovementField;
    private FieldInfo? forbiddenZonesField;
    private FieldInfo? wposXField;
    private FieldInfo? wposZField;
    private Type? resolvedHintsType;
    private Type? resolvedWPosType;
    private string hookState = "unresolved";
    private string lastReason = "not evaluated";
    private bool lastInjected;
    private int lastPartyMembers;
    private float lastDistanceToCenter;
    private int lastCoveredMembers;
    private HealerCoverageOverlaySnapshot? lastOverlay;
    private HealerCoverageGoalPlan? lastPlan;
    private Delegate? lastGoalDelegate;
    private HealerCoverageMoveDecision? coverageMoveDecision;
    private long healerCoverageGcdWindowId;
    private long evaluatedHealerCoverageGcdWindowId = -1;
    private float lastObservedGcdElapsed = -1f;
    private DateTime fallbackHealerCoverageGcdWindowStartedAt = DateTime.MinValue;
    private DateTime nextCoverageDashAttempt = DateTime.MinValue;
    private bool bmrMoveRequested;
    private bool bmrMoveImminent;

    public HealerAoePositioningStatus Status => new(
        this.hookState,
        this.lastReason,
        this.lastInjected,
        this.lastPartyMembers,
        this.lastDistanceToCenter,
        this.lastCoveredMembers,
        this.lastOverlay?.Center);

    public HealerCoverageOverlaySnapshot? Overlay => this.lastOverlay;

    public void SetHookState(string state)
    {
        this.hookState = state;
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
        this.wposXField = null;
        this.wposZField = null;
        this.resolvedHintsType = null;
        this.resolvedWPosType = null;
        this.lastReason = "reset";
        this.lastInjected = false;
        this.lastPartyMembers = 0;
        this.lastDistanceToCenter = 0f;
        this.lastCoveredMembers = 0;
        this.lastOverlay = null;
        this.lastPlan = null;
        this.lastGoalDelegate = null;
        this.coverageMoveDecision = null;
        this.healerCoverageGcdWindowId = 0;
        this.evaluatedHealerCoverageGcdWindowId = -1;
        this.lastObservedGcdElapsed = -1f;
        this.fallbackHealerCoverageGcdWindowStartedAt = DateTime.MinValue;
        this.nextCoverageDashAttempt = DateTime.MinValue;
        this.bmrMoveRequested = false;
        this.bmrMoveImminent = false;
    }

    public void TryInjectGoal(object hints, ICollection<BossModGoalContribution> contributions)
    {
        this.lastInjected = false;
        this.lastOverlay = null;

        if (!config.Enabled || !config.ManageHealerCoverageZone)
        {
            this.lastReason = "disabled";
            return;
        }

        if (!config.ManageMovement)
        {
            this.lastReason = "movement management disabled";
            return;
        }

        if (!CombatEngagementDetector.IsEffectivelyInCombat(services) || services.Condition[ConditionFlag.Unconscious])
        {
            this.lastReason = "not active in combat";
            return;
        }

        if (automatedMovementSuppressed())
        {
            this.lastReason = "manual movement suppression active";
            return;
        }

        if (JobRoles.GetRangeRole(services.ObjectTable.LocalPlayer) != RangeRole.Healer)
        {
            this.lastReason = "not a healer";
            return;
        }

        var player = services.ObjectTable.LocalPlayer;
        if (player == null)
        {
            this.lastReason = "local player unavailable";
            return;
        }

        if (player.IsCasting)
        {
            this.lastReason = "player casting";
            return;
        }

        if (!this.EnsureResolved(hints.GetType()))
            return;

        if (this.goalZonesField!.GetValue(hints) is not System.Collections.IList goalZones)
        {
            this.lastReason = "BMR goal zone list unavailable";
            return;
        }

        var partySnapshot = PartyAllyProvider.GetVisiblePartyAllies(services, player);
        var members = partySnapshot.Members;
        this.lastPartyMembers = members.Count;

        if (members.Count < 1)
        {
            this.lastReason = "no visible party members";
            return;
        }

        var tank = PartyAllyProvider.SelectBestTank(services, player);
        var bossModuleContext = currentTargetHasBossModule();
        var plan = this.BuildPlan(player, members, tank, bossModuleContext);
        var now = DateTime.UtcNow;
        rotationSolverActions.TryGetUpcomingGcd(requirePreview: false, out var upcomingGcd, out _);
        var gcdWindowId = this.UpdateHealerCoverageGcdWindow(upcomingGcd, now);

        this.lastDistanceToCenter = plan.DistanceToCenter;
        this.lastCoveredMembers = plan.BestCoveredCount;
        var coverageGain = plan.BestCoveredCount - plan.CurrentCoveredCount;
        var restoresTankCoverage = plan.HasTank &&
                                    !plan.CurrentCoversTank &&
                                    plan.BestCoversTank;
        var strongCoverageGain = coverageGain >= MinimumCoverageGain ||
                                 plan.CurrentCoveredCount == 0 && coverageGain > 0;
        var restoresSingleMissingCoverage = IsSingleMissingCoverageRestore(
            plan.CurrentCoveredCount,
            plan.BestCoveredCount,
            plan.TotalMembers);
        var pressure = mechanicPressure();
        var partyAoeHealGcdPending = upcomingGcd != null && IsPartyAoeHealAction(upcomingGcd);
        var partyAoeHealPending = partyAoeHealGcdPending || pressure.RaidwideOrDamageSoon;
        var partyAoeHealActionName = partyAoeHealGcdPending
            ? upcomingGcd!.ActionName
            : pressure.RaidwideOrDamageSoon ? "BossMod damage pressure" : "<none>";
        var tankbusterHealCoveragePending = pressure.TankbusterSoon ||
                                            (pressure.NextDamageType == BossModPredictedDamageType.Tankbuster && pressure.DamageSoon);
        var forcedMovementActive = VectorLengthSquared(this.forcedMovementField?.GetValue(hints)) > 0.01f;
        var forbiddenSafetyActive = this.forbiddenZonesField?.GetValue(hints) is ICollection { Count: > 0 };
        var bossModGoalZoneActive = goalZones.Count > 0;
        var downtimeLikely = this.IsDowntimeLikely();
        var mechanicPositioningActive = forcedMovementActive || forbiddenSafetyActive || bossModGoalZoneActive || this.bmrMoveRequested || this.bmrMoveImminent;
        var slidecastWindow = CasterMovementPolicy.IsCasterSlidecastWindow(player);
        var proactiveCoverageComfort = ShouldImproveCoverageComfort(
            plan.CurrentCoveredCount,
            plan.BestCoveredCount,
            plan.TotalMembers,
            plan.CurrentCoverageComfortSlack,
            plan.BestCoverageComfortSlack,
            plan.DistanceToCenter,
            downtimeLikely,
            mechanicPositioningActive);
        var criticalCoverageCatchUp = ShouldCatchUpCriticalCoverage(
            plan.CurrentCoveredCount,
            plan.BestCoveredCount,
            plan.TotalMembers,
            plan.DistanceToCenter);
        var partyAoeHealCatchUp = ShouldCatchUpForPartyAoeHeal(
            plan.CurrentCoveredCount,
            plan.BestCoveredCount,
            plan.TotalMembers,
            plan.DistanceToCenter,
            partyAoeHealPending);
        var bossCoverageMove = mechanicPositioningActive && ShouldImproveBossCoveragePosition(
            plan.CurrentCoveredCount,
            plan.BestCoveredCount,
            plan.TotalMembers,
            plan.CurrentBossRangeScore,
            plan.BestBossRangeScore,
            bossModuleContext);
        var urgentHealingCoverage = ShouldImproveUrgentHealingCoverage(
            plan.CurrentCoveredCount,
            plan.BestCoveredCount,
            plan.TotalMembers,
            plan.CurrentCoversTank,
            plan.BestCoversTank,
            partyAoeHealPending,
            pressure.SharedDamageSoon,
            tankbusterHealCoveragePending);
        var boundedTankRestore = restoresTankCoverage &&
                                 tankbusterHealCoveragePending &&
                                 (plan.DistanceToCenter <= MaxTankCoverageRestoreDistance || urgentHealingCoverage);
        var shouldMove = strongCoverageGain || proactiveCoverageComfort || criticalCoverageCatchUp || partyAoeHealCatchUp || bossCoverageMove || urgentHealingCoverage || boundedTankRestore;
        var maxMoveDistance = criticalCoverageCatchUp
            ? MaxCriticalCoverageCatchUpMoveDistance
            : partyAoeHealCatchUp || urgentHealingCoverage
            ? MaxPartyAoeHealCatchUpMoveDistance
            : bossCoverageMove
            ? MaxBossCoverageRoutineMoveDistance
            : proactiveCoverageComfort
            ? ResolveCoverageComfortMoveDistance(downtimeLikely, mechanicPositioningActive)
            : MaxConvenienceMoveDistance;
        var priority = criticalCoverageCatchUp || partyAoeHealCatchUp
            ? BossModGoalPriority.DefensiveMechanic
            : BossModGoalPriority.PartyUtility;

        if (this.lastPlan == null || !this.lastPlan.SameSource(plan))
        {
            this.lastGoalDelegate = plan.CreateGoalDelegate(this.resolvedWPosType!, this.wposXField!, this.wposZField!);
            this.lastPlan = plan;
        }

        if (this.TryUseHealerCoverageMoveDecision(gcdWindowId, player, plan, contributions))
        {
            return;
        }

        if (this.evaluatedHealerCoverageGcdWindowId == gcdWindowId)
        {
            this.lastReason = "healer coverage move already chosen this GCD";
            this.lastOverlay = plan.CreateOverlay(player.Position.Y, injected: false);
            return;
        }

        if (shouldMove && ShouldYieldCoverageForSafety(
                forcedMovementActive,
                forbiddenSafetyActive,
                bossModGoalZoneActive,
                this.bmrMoveRequested,
                this.bmrMoveImminent))
        {
            var coverageDashReason = ResolveCoverageDashReason(
                urgentHealingCoverage,
                partyAoeHealCatchUp,
                criticalCoverageCatchUp,
                tankbusterHealCoveragePending,
                pressure.SharedDamageSoon,
                partyAoeHealActionName);
            if (ShouldAllowCoverageDashDuringBossModMovement(
                    urgentHealingCoverage,
                    partyAoeHealCatchUp,
                    criticalCoverageCatchUp,
                    tankbusterHealCoveragePending,
                    mechanicPositioningActive,
                    bossCoverageMove,
                    proactiveCoverageComfort,
                    strongCoverageGain) &&
                this.TryUseCoverageDash(
                    gcdWindowId,
                    player,
                    plan,
                    members,
                    coverageDashReason,
                    urgentHealingCoverage || partyAoeHealCatchUp || criticalCoverageCatchUp,
                    tankbusterHealCoveragePending))
            {
                return;
            }

            shouldMove = false;
            this.lastReason = "forced mechanic movement active";
        }
        else if (shouldMove &&
                 ShouldSuppressSlideWindowCoverage(
                     slidecastWindow))
        {
            shouldMove = false;
            this.evaluatedHealerCoverageGcdWindowId = gcdWindowId;
            this.lastReason = "healer coverage needs intent";
        }
        else if (shouldMove && plan.DistanceToCenter > maxMoveDistance)
        {
            var coverageDashReason = ResolveCoverageDashReason(
                urgentHealingCoverage,
                partyAoeHealCatchUp,
                criticalCoverageCatchUp,
                tankbusterHealCoveragePending,
                pressure.SharedDamageSoon,
                partyAoeHealActionName);
            if ((bossCoverageMove || urgentHealingCoverage || partyAoeHealCatchUp || criticalCoverageCatchUp) &&
                this.TryUseCoverageDash(gcdWindowId, player, plan, members, coverageDashReason, urgentHealingCoverage || partyAoeHealCatchUp || criticalCoverageCatchUp, tankbusterHealCoveragePending))
            {
                return;
            }

            shouldMove = false;
            this.lastReason = partyAoeHealPending
                ? $"party AoE heal coverage point too far: {plan.DistanceToCenter:0.0}y"
                : $"coverage point too far: {plan.DistanceToCenter:0.0}y";
        }
        else if (shouldMove &&
                 ShouldSkipCoverageMoveForGcdTiming(
                     plan.DistanceToCenter,
                     upcomingGcd?.GcdRemaining ?? -1f,
                     upcomingGcd?.GcdElapsed ?? -1f,
                     upcomingGcd?.GcdTotal ?? -1f,
                     slidecastWindow,
                     IsBossModSafetyMovementActive(forcedMovementActive, forbiddenSafetyActive, bossModGoalZoneActive, this.bmrMoveImminent),
                     out var timingSkipReason))
        {
            var coverageDashReason = ResolveCoverageDashReason(
                urgentHealingCoverage,
                partyAoeHealCatchUp,
                criticalCoverageCatchUp,
                tankbusterHealCoveragePending,
                pressure.SharedDamageSoon,
                partyAoeHealActionName);
            if ((urgentHealingCoverage || partyAoeHealCatchUp || criticalCoverageCatchUp) &&
                this.TryUseCoverageDash(gcdWindowId, player, plan, members, coverageDashReason, urgent: true, tankbusterHealCoveragePending))
            {
                return;
            }

            shouldMove = false;
            this.evaluatedHealerCoverageGcdWindowId = gcdWindowId;
            this.lastReason = timingSkipReason;
        }
        else if (!shouldMove && restoresTankCoverage && plan.DistanceToCenter > MaxTankCoverageRestoreDistance)
        {
            this.lastReason = $"tank coverage point too far: {plan.DistanceToCenter:0.0}y";
        }
        else if (!shouldMove && restoresSingleMissingCoverage)
        {
            this.lastReason = $"single missing member ignored: {plan.CurrentCoveredCount}/{plan.TotalMembers} -> {plan.BestCoveredCount}/{plan.TotalMembers}";
        }
        else if (!shouldMove && coverageGain > 0)
        {
            this.lastReason = $"minor coverage gain ignored: {plan.CurrentCoveredCount}/{plan.TotalMembers} -> {plan.BestCoveredCount}/{plan.TotalMembers}";
        }
        else if (!shouldMove && plan.HasTank && !plan.CurrentCoversTank)
        {
            this.lastReason = "tank out of coverage; no better safe coverage point";
        }
        else
        {
            this.lastReason = $"covering {plan.CurrentCoveredCount}/{plan.TotalMembers}";
        }

        if (shouldMove)
        {
            var prefix = partyAoeHealCatchUp
                ? $"party AoE heal ({partyAoeHealActionName}); "
                : criticalCoverageCatchUp
                ? "critical party coverage; "
                : restoresTankCoverage
                ? "tank out of coverage; "
                : urgentHealingCoverage && tankbusterHealCoveragePending
                ? "tankbuster healing coverage; "
                : urgentHealingCoverage
                ? "urgent healing coverage; "
                : proactiveCoverageComfort && coverageGain > 0
                ? downtimeLikely
                    ? "downtime coverage setup; "
                    : mechanicPositioningActive
                    ? "mechanic coverage setup; "
                    : "proactive coverage setup; "
                : proactiveCoverageComfort
                ? downtimeLikely
                    ? "downtime healer comfort; "
                    : mechanicPositioningActive
                    ? "mechanic healer comfort; "
                    : "healer comfort; "
                : bossCoverageMove
                ? "boss healer coverage; "
                : string.Empty;
            this.coverageMoveDecision = new(
                gcdWindowId,
                plan.OptimalCenter,
                this.lastGoalDelegate!,
                plan.CreateOverlay(player.Position.Y, injected: true),
                plan.BestCoveredCount,
                plan.TotalMembers,
                priority,
                prefix);
            this.evaluatedHealerCoverageGcdWindowId = gcdWindowId;
            this.InjectHealerCoverageMoveDecision(this.coverageMoveDecision, plan.CurrentCoveredCount, contributions);
            return;
        }

        this.lastInjected = shouldMove;
        this.lastOverlay = plan.CreateOverlay(player.Position.Y, injected: shouldMove);
    }

    private long UpdateHealerCoverageGcdWindow(RsrAoeActionSnapshot? action, DateTime now)
    {
        var newWindow = this.healerCoverageGcdWindowId == 0;
        if (action != null && AoeRepositionPolicy.HasReliableGcdTiming(action.GcdRemaining, action.GcdElapsed, action.GcdTotal))
        {
            if (this.lastObservedGcdElapsed >= 0f &&
                action.GcdElapsed + GcdElapsedResetTolerance < this.lastObservedGcdElapsed)
            {
                newWindow = true;
            }

            this.lastObservedGcdElapsed = action.GcdElapsed;
            this.fallbackHealerCoverageGcdWindowStartedAt = now.Subtract(TimeSpan.FromSeconds(Math.Max(0f, action.GcdElapsed)));
        }
        else
        {
            if (this.fallbackHealerCoverageGcdWindowStartedAt == DateTime.MinValue ||
                now - this.fallbackHealerCoverageGcdWindowStartedAt >= FallbackGcdWindow)
            {
                newWindow = true;
                this.fallbackHealerCoverageGcdWindowStartedAt = now;
            }

            this.lastObservedGcdElapsed = -1f;
        }

        if (newWindow)
        {
            this.healerCoverageGcdWindowId++;
            this.coverageMoveDecision = null;
            this.evaluatedHealerCoverageGcdWindowId = -1;
        }

        return this.healerCoverageGcdWindowId;
    }

    private bool TryUseHealerCoverageMoveDecision(
        long gcdWindowId,
        IBattleChara player,
        HealerCoverageGoalPlan plan,
        ICollection<BossModGoalContribution> contributions)
    {
        var decision = this.coverageMoveDecision;
        if (decision == null || decision.GcdWindowId != gcdWindowId)
        {
            return false;
        }

        var playerPosition = new Vector2(player.Position.X, player.Position.Z);
        if (plan.CurrentCoveredCount >= decision.BestCoveredCount ||
            Vector2.Distance(playerPosition, decision.Center) <= 0.75f)
        {
            this.coverageMoveDecision = null;
            this.evaluatedHealerCoverageGcdWindowId = gcdWindowId;
            this.lastInjected = false;
            this.lastOverlay = plan.CreateOverlay(player.Position.Y, injected: false);
            this.lastReason = "healer coverage position reached for this GCD";
            return true;
        }

        this.InjectHealerCoverageMoveDecision(decision, plan.CurrentCoveredCount, contributions);
        this.lastReason = $"{decision.ReasonPrefix}holding healer coverage move for this GCD ({plan.CurrentCoveredCount}/{decision.TotalMembers} -> {decision.BestCoveredCount}/{decision.TotalMembers})";
        return true;
    }

    private void InjectHealerCoverageMoveDecision(
        HealerCoverageMoveDecision decision,
        int currentCoveredCount,
        ICollection<BossModGoalContribution> contributions)
    {
        contributions.Add(new(decision.Goal, decision.Priority, "Healer coverage zone", decision.Center, MechanicWhisperConfidence.Confident));
        this.lastInjected = true;
        this.lastOverlay = decision.Overlay;
        this.lastReason = $"{decision.ReasonPrefix}covering {currentCoveredCount}/{decision.TotalMembers}, can cover {decision.BestCoveredCount}/{decision.TotalMembers}";
    }

    private bool TryUseCoverageDash(
        long gcdWindowId,
        IBattleChara player,
        HealerCoverageGoalPlan plan,
        IReadOnlyList<IBattleChara> members,
        string reason,
        bool urgent,
        bool tankbusterHealCoveragePending)
    {
        if (DateTime.UtcNow < this.nextCoverageDashAttempt ||
            !config.UseGapCloser)
        {
            return false;
        }

        return player.ClassJob.RowId switch
        {
            24 when config.GapCloserWHM => this.TryUseWhiteMageCoverageDash(gcdWindowId, player, plan, reason, urgent, tankbusterHealCoveragePending),
            40 when config.GapCloserSGE => this.TryUseSageCoverageDash(gcdWindowId, player, plan, members, reason, urgent, tankbusterHealCoveragePending),
            _ => false
        };
    }

    private unsafe bool TryUseWhiteMageCoverageDash(
        long gcdWindowId,
        IBattleChara player,
        HealerCoverageGoalPlan plan,
        string reason,
        bool urgent,
        bool tankbusterHealCoveragePending)
    {
        if (!ActionUse.CanUseAction(ActionUse.WhiteMageAetherialShiftActionId))
        {
            return false;
        }

        var playerPosition = new Vector2(player.Position.X, player.Position.Z);
        var currentDistance = Vector2.Distance(playerPosition, plan.OptimalCenter);
        if (currentDistance < MinimumBossCoverageDashDistance)
        {
            return false;
        }

        var movement = new Vector3(plan.OptimalCenter.X - player.Position.X, 0f, plan.OptimalCenter.Y - player.Position.Z);
        if (movement.LengthSquared() <= 0.0001f)
        {
            return false;
        }

        var direction = Vector3.Normalize(movement);
        var destination = player.Position + direction * CombatConstants.FixedForwardGapCloserRange;
        var landingPosition = new Vector2(destination.X, destination.Z);
        var landingDistance = Vector2.Distance(landingPosition, plan.OptimalCenter);
        var gain = currentDistance - landingDistance;
        var landingCoveredCount = CountCovered(landingPosition, plan.AllMembers);
        var landingCoversTank = plan.CoversTankAt(landingPosition);
        if (!ShouldUseCoverageDashLanding(
                plan.CurrentCoveredCount,
                plan.BestCoveredCount,
                plan.TotalMembers,
                gain,
                landingCoveredCount,
                urgent,
                tankbusterHealCoveragePending,
                plan.CurrentCoversTank,
                landingCoversTank))
        {
            return false;
        }

        if (!mobilityEvaluator.TryValidateFixedDashDestination(
            player,
            destination,
            services.TargetManager.Target as IBattleChara,
            null,
            MobilityIntent.PathRecovery,
            "Aetherial Shift",
            ActionUse.WhiteMageAetherialShiftActionId,
            0f,
            requireSafetyProgress: false,
            requireUptimeProgress: false,
            requireVnavReachable: true,
            fixedDashRange: CombatConstants.FixedForwardGapCloserRange,
            fixedDashBackwards: false,
            out var decision))
        {
            this.lastReason = $"Aetherial Shift coverage rejected: {decision.RiskReason}";
            return false;
        }

        this.nextCoverageDashAttempt = DateTime.UtcNow.AddMilliseconds(250);
        var desiredRotation = Geometry.DirectionToRotation(direction);
        if (Geometry.AbsAngleDelta(player.Rotation, desiredRotation) > FacingController.DirectionalDashToleranceRadians)
        {
            facingController.RequestFacing(FacingController.CreateDirectionalDashRequest(desiredRotation, destination, "turn for Aetherial Shift", FacingBossModPolicy.AssistValidatedDash));
            this.lastInjected = false;
            this.lastOverlay = plan.CreateOverlay(player.Position.Y, injected: true);
            this.lastReason = $"turning for Aetherial Shift {reason} ({plan.CurrentCoveredCount}/{plan.TotalMembers} -> {plan.BestCoveredCount}/{plan.TotalMembers})";
            return true;
        }

        var used = ActionManager.Instance()->UseAction(ActionType.Action, ActionUse.WhiteMageAetherialShiftActionId, player.GameObjectId);
        mobilityEvaluator.RecordActionResult(decision, used, used ? "action used" : "action failed");
        if (used)
        {
            this.coverageMoveDecision = null;
            this.evaluatedHealerCoverageGcdWindowId = gcdWindowId;
            this.lastInjected = false;
            this.lastOverlay = plan.CreateOverlay(player.Position.Y, injected: true);
            this.lastReason = $"used Aetherial Shift for {reason} ({plan.CurrentCoveredCount}/{plan.TotalMembers} -> {plan.BestCoveredCount}/{plan.TotalMembers})";
            return true;
        }

        this.lastReason = "failed to use Aetherial Shift for boss healer coverage";
        return false;
    }

    private unsafe bool TryUseSageCoverageDash(
        long gcdWindowId,
        IBattleChara player,
        HealerCoverageGoalPlan plan,
        IReadOnlyList<IBattleChara> members,
        string reason,
        bool urgent,
        bool tankbusterHealCoveragePending)
    {
        if (!ActionUse.CanUseAction(ActionUse.SageIcarusActionId))
        {
            return false;
        }

        var playerPosition = new Vector2(player.Position.X, player.Position.Z);
        var currentDistance = Vector2.Distance(playerPosition, plan.OptimalCenter);
        if (currentDistance < MinimumBossCoverageDashDistance)
        {
            return false;
        }

        IBattleChara? bestAlly = null;
        MobilityDecisionDiagnostics bestDecision = MobilityDecisionDiagnostics.Empty;
        var bestScore = float.NegativeInfinity;
        foreach (var ally in members)
        {
            if (ally.GameObjectId == player.GameObjectId ||
                Vector3.Distance(player.Position, ally.Position) > MaximumBossCoverageDashTargetDistance)
            {
                continue;
            }

            var allyPosition = new Vector2(ally.Position.X, ally.Position.Z);
            var landingDistance = Vector2.Distance(allyPosition, plan.OptimalCenter);
            var gain = currentDistance - landingDistance;
            var landingCoveredCount = CountCovered(allyPosition, plan.AllMembers);
            var landingCoversTank = plan.CoversTankAt(allyPosition);
            if (landingDistance > BossCoverageDashTargetRadius ||
                !ShouldUseCoverageDashLanding(
                    plan.CurrentCoveredCount,
                    plan.BestCoveredCount,
                    plan.TotalMembers,
                    gain,
                    landingCoveredCount,
                    urgent,
                    tankbusterHealCoveragePending,
                    plan.CurrentCoversTank,
                    landingCoversTank))
            {
                continue;
            }

            if (!mobilityEvaluator.TryValidateDashDestination(
                player,
                ally.Position,
                services.TargetManager.Target as IBattleChara,
                null,
                MobilityIntent.PathRecovery,
                "Icarus",
                ActionUse.SageIcarusActionId,
                0f,
                requireSafetyProgress: false,
                requireUptimeProgress: false,
                requireVnavReachable: true,
                out var decision))
            {
                this.lastReason = $"Icarus coverage rejected: {decision.RiskReason}";
                continue;
            }

            var score = gain + CountCovered(allyPosition, plan.AllMembers);
            if (score > bestScore)
            {
                bestAlly = ally;
                bestDecision = decision;
                bestScore = score;
            }
        }

        if (bestAlly == null)
        {
            return false;
        }

        this.nextCoverageDashAttempt = DateTime.UtcNow.AddMilliseconds(250);
        var used = ActionManager.Instance()->UseAction(ActionType.Action, ActionUse.SageIcarusActionId, bestAlly.GameObjectId);
        mobilityEvaluator.RecordActionResult(bestDecision, used, used ? "action used" : "action failed");
        if (used)
        {
            this.coverageMoveDecision = null;
            this.evaluatedHealerCoverageGcdWindowId = gcdWindowId;
            this.lastInjected = false;
            this.lastOverlay = plan.CreateOverlay(player.Position.Y, injected: true);
            this.lastReason = $"used Icarus for {reason} ({plan.CurrentCoveredCount}/{plan.TotalMembers} -> {plan.BestCoveredCount}/{plan.TotalMembers})";
            return true;
        }

        this.lastReason = "failed to use Icarus for boss healer coverage";
        return false;
    }

    private HealerCoverageGoalPlan BuildPlan(IBattleChara player, IReadOnlyList<IBattleChara> members, IBattleChara? tank, bool bossModuleContext)
    {
        var playerPos = new Vector2(player.Position.X, player.Position.Z);
        var allPositions = new List<Vector2>(members.Count);
        Vector2? tankPosition = null;
        foreach (var m in members)
        {
            var position = new Vector2(m.Position.X, m.Position.Z);
            if (tank != null && m.GameObjectId == tank.GameObjectId)
            {
                tankPosition = position;
            }

            allPositions.Add(position);
        }

        if (tank != null && !tankPosition.HasValue)
        {
            tankPosition = new Vector2(tank.Position.X, tank.Position.Z);
        }

        var currentCoveredMembers = GetCoveredMembers(playerPos, allPositions);
        var currentCovered = currentCoveredMembers.Count;
        var currentCoversTank = tankPosition.HasValue &&
                                Vector2.DistanceSquared(playerPos, tankPosition.Value) <= CoverageRadiusSquared;
        var bossCenterAvoidance = this.ResolveBossCenterAvoidance();
        var bossRangePreference = bossModuleContext ? this.ResolveBossRangePreference() : null;
        Func<Vector2, bool>? candidateAllowed = null;
        if (bossCenterAvoidance.HasValue)
        {
            var avoidance = bossCenterAvoidance.Value;
            candidateAllowed = avoidance.Allows;
        }

        var bestCenter = SelectBestCenter(playerPos, allPositions, tankPosition, this.lastPlan?.OptimalCenter, candidateAllowed, bossRangePreference);
        var bestCovered = GetCoveredMembers(bestCenter, allPositions);
        var bestCoversTank = tankPosition.HasValue &&
                             Vector2.DistanceSquared(bestCenter, tankPosition.Value) <= CoverageRadiusSquared;

        if (bestCovered.Count < currentCovered ||
            (bestCovered.Count == currentCovered && currentCoversTank && !bestCoversTank))
        {
            bestCenter = playerPos;
            bestCovered = currentCoveredMembers;
            bestCoversTank = currentCoversTank;
        }

        if (bestCovered.Count == currentCovered &&
            TrySelectComfortCoverageCenter(playerPos, allPositions, tankPosition, this.lastPlan?.OptimalCenter, out var comfortCenter, candidateAllowed, bossRangePreference))
        {
            var comfortCovered = GetCoveredMembers(comfortCenter, allPositions);
            var comfortCoversTank = tankPosition.HasValue &&
                                    Vector2.DistanceSquared(comfortCenter, tankPosition.Value) <= CoverageRadiusSquared;
            var mustCoverTank = bestCoversTank || currentCoversTank;
            if (comfortCovered.Count >= bestCovered.Count && (!mustCoverTank || comfortCoversTank))
            {
                bestCenter = comfortCenter;
                bestCovered = comfortCovered;
                bestCoversTank = comfortCoversTank;
            }
        }

        var currentComfortSlack = MinimumCoverageSlack(playerPos, currentCoveredMembers);
        var bestComfortSlack = MinimumCoverageSlack(bestCenter, bestCovered);

        return new HealerCoverageGoalPlan(
            playerPos,
            currentCoveredMembers,
            bestCenter,
            bestCovered,
            allPositions,
            tankPosition,
            Vector2.Distance(playerPos, bestCenter),
            currentCovered,
            currentCoversTank,
            bestCoversTank,
            currentComfortSlack,
            bestComfortSlack,
            bossCenterAvoidance,
            bossRangePreference,
            bossRangePreference?.Score(playerPos) ?? 0f);
    }

    private BossCenterAvoidance? ResolveBossCenterAvoidance()
    {
        if (!config.AvoidStandingInsideEnemies ||
            services.TargetManager.Target is not IBattleChara target ||
            target.IsDead ||
            target.CurrentHp == 0 ||
            !BossCenterAvoidanceController.IsBossLikeHitbox(target.HitboxRadius))
        {
            return null;
        }

        return new BossCenterAvoidance(
            new Vector2(target.Position.X, target.Position.Z),
            BossCenterAvoidanceController.AvoidanceRadius(target.HitboxRadius));
    }

    private BossRangePreference? ResolveBossRangePreference()
    {
        if (services.TargetManager.Target is not IBattleChara target ||
            target.IsDead ||
            target.CurrentHp == 0 ||
            !target.IsTargetable)
        {
            return null;
        }

        return new BossRangePreference(
            new Vector2(target.Position.X, target.Position.Z),
            target.HitboxRadius + BossRangeComfortSurfaceDistance);
    }

    private static Vector2 AveragePosition(IReadOnlyList<Vector2> members)
    {
        var average = Vector2.Zero;
        foreach (var member in members)
        {
            average += member;
        }

        return average / members.Count;
    }

    internal static Vector2 SelectBestCenter(
        Vector2 playerPosition,
        IReadOnlyList<Vector2> members,
        Vector2? tankPosition,
        Vector2? previousCenter = null,
        Func<Vector2, bool>? candidateAllowed = null)
        => SelectBestCenter(playerPosition, members, tankPosition, previousCenter, candidateAllowed, bossRangePreference: null);

    private static Vector2 SelectBestCenter(
        Vector2 playerPosition,
        IReadOnlyList<Vector2> members,
        Vector2? tankPosition,
        Vector2? previousCenter,
        Func<Vector2, bool>? candidateAllowed,
        BossRangePreference? bossRangePreference)
    {
        var best = playerPosition;
        var bestCovered = CountCovered(best, members);
        var bestCoversTank = tankPosition.HasValue &&
                             Vector2.DistanceSquared(best, tankPosition.Value) <= CoverageRadiusSquared;
        var bestDistance = 0f;

        foreach (var candidate in EnumerateCoverageCenters(playerPosition, members))
        {
            if (!IsCandidateAllowed(candidate, candidateAllowed))
            {
                continue;
            }

            var covered = CountCovered(candidate, members);
            var coversTank = tankPosition.HasValue &&
                             Vector2.DistanceSquared(candidate, tankPosition.Value) <= CoverageRadiusSquared;
            var distance = Vector2.Distance(playerPosition, candidate);
            if (covered > bestCovered ||
                (covered == bestCovered && coversTank && !bestCoversTank) ||
                (covered == bestCovered && coversTank == bestCoversTank && PreferCoverageCandidate(candidate, best, distance, bestDistance, bossRangePreference)))
            {
                best = candidate;
                bestCovered = covered;
                bestCoversTank = coversTank;
                bestDistance = distance;
            }
        }

        var playerIsAlreadyBest = Vector2.DistanceSquared(best, playerPosition) <= 0.25f;
        if (!playerIsAlreadyBest &&
            previousCenter.HasValue &&
            IsCandidateAllowed(previousCenter.Value, candidateAllowed) &&
            ShouldRetainPreviousCenter(previousCenter.Value, best, members, tankPosition))
        {
            best = previousCenter.Value;
        }

        return best;
    }

    internal static bool TrySelectComfortCoverageCenter(
        Vector2 playerPosition,
        IReadOnlyList<Vector2> members,
        Vector2? tankPosition,
        Vector2? previousCenter,
        out Vector2 comfortCenter,
        Func<Vector2, bool>? candidateAllowed = null)
        => TrySelectComfortCoverageCenter(playerPosition, members, tankPosition, previousCenter, out comfortCenter, candidateAllowed, bossRangePreference: null);

    private static bool TrySelectComfortCoverageCenter(
        Vector2 playerPosition,
        IReadOnlyList<Vector2> members,
        Vector2? tankPosition,
        Vector2? previousCenter,
        out Vector2 comfortCenter,
        Func<Vector2, bool>? candidateAllowed,
        BossRangePreference? bossRangePreference)
    {
        comfortCenter = playerPosition;
        var currentCovered = GetCoveredMembers(playerPosition, members);
        if (currentCovered.Count <= 1)
        {
            return false;
        }

        var currentCoversTank = CoversTank(playerPosition, tankPosition);
        var best = playerPosition;
        var bestMinSlack = MinimumCoverageSlack(playerPosition, currentCovered);
        var bestUsefulSlack = MathF.Min(bestMinSlack, PreferredCoverageSlack);
        var bestDistanceSq = 0f;
        var targetUsefulSlack = MathF.Min(PreferredCoverageSlack, bestMinSlack + MinimumCoverageComfortSlackGain);

        foreach (var candidate in EnumerateComfortCoverageCenters(playerPosition, currentCovered, previousCenter))
        {
            if (!IsCandidateAllowed(candidate, candidateAllowed))
            {
                continue;
            }

            if (Vector2.DistanceSquared(candidate, playerPosition) <= 0.25f)
            {
                continue;
            }

            if (!PreservesCoverage(candidate, currentCovered))
            {
                continue;
            }

            if (currentCoversTank && !CoversTank(candidate, tankPosition))
            {
                continue;
            }

            var minSlack = MinimumCoverageSlack(candidate, currentCovered);
            var usefulSlack = MathF.Min(minSlack, PreferredCoverageSlack);
            var distanceSq = Vector2.DistanceSquared(candidate, playerPosition);
            if (usefulSlack > bestUsefulSlack + 0.05f ||
                MathF.Abs(usefulSlack - bestUsefulSlack) <= 0.05f &&
                PreferCoverageCandidate(candidate, best, MathF.Sqrt(distanceSq), MathF.Sqrt(bestDistanceSq), bossRangePreference))
            {
                best = candidate;
                bestMinSlack = minSlack;
                bestUsefulSlack = usefulSlack;
                bestDistanceSq = distanceSq;
            }
        }

        if (Vector2.DistanceSquared(best, playerPosition) <= 0.25f ||
            bestUsefulSlack < targetUsefulSlack - 0.05f)
        {
            return false;
        }

        comfortCenter = best;
        return true;
    }

    private static bool PreferCoverageCandidate(Vector2 candidate, Vector2 currentBest, float candidateDistance, float bestDistance, BossRangePreference? bossRangePreference)
    {
        if (bossRangePreference.HasValue)
        {
            var candidateRange = bossRangePreference.Value.Score(candidate);
            var bestRange = bossRangePreference.Value.Score(currentBest);
            if (candidateRange > bestRange + 0.05f)
            {
                return true;
            }

            if (candidateRange + 0.05f < bestRange)
            {
                return false;
            }
        }

        return candidateDistance < bestDistance;
    }

    private static bool IsCandidateAllowed(Vector2 candidate, Func<Vector2, bool>? candidateAllowed)
    {
        return candidateAllowed == null || candidateAllowed(candidate);
    }

    private static bool ShouldRetainPreviousCenter(
        Vector2 previousCenter,
        Vector2 selectedCenter,
        IReadOnlyList<Vector2> members,
        Vector2? tankPosition)
    {
        if (CountCovered(previousCenter, members) < CountCovered(selectedCenter, members))
        {
            return false;
        }

        return !CoversTank(selectedCenter, tankPosition) || CoversTank(previousCenter, tankPosition);
    }

    private static IEnumerable<Vector2> EnumerateCoverageCenters(Vector2 playerPosition, IReadOnlyList<Vector2> members)
    {
        yield return playerPosition;

        foreach (var candidate in EnumeratePlayerSideCoverageCenters(playerPosition, members))
        {
            yield return candidate;
        }

        for (var i = 0; i < members.Count; ++i)
        {
            yield return members[i];
            for (var j = i + 1; j < members.Count; ++j)
            {
                yield return (members[i] + members[j]) * 0.5f;
            }
        }

        yield return AveragePosition(members);
    }

    private static IEnumerable<Vector2> EnumerateComfortCoverageCenters(Vector2 playerPosition, IReadOnlyList<Vector2> currentCoveredMembers, Vector2? previousCenter)
    {
        yield return playerPosition;

        if (previousCenter.HasValue)
        {
            yield return previousCenter.Value;
        }

        foreach (var candidate in EnumeratePlayerSideCoverageCenters(playerPosition, currentCoveredMembers))
        {
            yield return candidate;
        }

        for (var i = 0; i < currentCoveredMembers.Count; ++i)
        {
            yield return currentCoveredMembers[i];
            for (var j = i + 1; j < currentCoveredMembers.Count; ++j)
            {
                yield return (currentCoveredMembers[i] + currentCoveredMembers[j]) * 0.5f;
            }
        }

        yield return AveragePosition(currentCoveredMembers);
    }

    private static IEnumerable<Vector2> EnumeratePlayerSideCoverageCenters(Vector2 playerPosition, IReadOnlyList<Vector2> members)
    {
        var offset = CoverageRadius - PreferredCoverageSlack;
        foreach (var member in members)
        {
            var towardPlayer = playerPosition - member;
            if (towardPlayer.LengthSquared() <= 0.01f)
            {
                continue;
            }

            yield return member + Vector2.Normalize(towardPlayer) * offset;
        }
    }

    private static int CountCovered(Vector2 candidate, IReadOnlyList<Vector2> members)
    {
        var covered = 0;
        foreach (var member in members)
        {
            if (Vector2.DistanceSquared(candidate, member) <= CoverageRadiusSquared)
                covered++;
        }

        return covered;
    }

    private static bool CoversTank(Vector2 candidate, Vector2? tankPosition)
    {
        return tankPosition.HasValue &&
               Vector2.DistanceSquared(candidate, tankPosition.Value) <= CoverageRadiusSquared;
    }

    private static bool PreservesCoverage(Vector2 candidate, IReadOnlyList<Vector2> coveredMembers)
    {
        foreach (var member in coveredMembers)
        {
            if (Vector2.DistanceSquared(candidate, member) > CoverageRadiusSquared)
            {
                return false;
            }
        }

        return true;
    }

    private static float MinimumCoverageSlack(Vector2 candidate, IReadOnlyList<Vector2> coveredMembers)
    {
        if (coveredMembers.Count == 0)
        {
            return 0f;
        }

        var slack = float.MaxValue;
        foreach (var member in coveredMembers)
        {
            slack = MathF.Min(slack, CoverageRadius - Vector2.Distance(candidate, member));
        }

        return slack;
    }

    private static IReadOnlyList<Vector2> GetCoveredMembers(Vector2 candidate, IReadOnlyList<Vector2> members)
    {
        var covered = new List<Vector2>(members.Count);
        foreach (var member in members)
        {
            if (Vector2.DistanceSquared(candidate, member) <= CoverageRadiusSquared)
                covered.Add(member);
        }

        return covered;
    }

    private bool EnsureResolved(Type hintsType)
    {
        if (this.resolvedHintsType == hintsType &&
            this.goalZonesField != null &&
            this.forcedMovementField != null &&
            this.forbiddenZonesField != null &&
            this.wposXField != null &&
            this.wposZField != null)
        {
            return true;
        }

        var goalZones = hintsType.GetField("GoalZones", InstanceFlags);
        var forcedMovement = hintsType.GetField("ForcedMovement", InstanceFlags);
        var forbiddenZones = hintsType.GetField("ForbiddenZones", InstanceFlags);
        var wposType = hintsType.Assembly.GetType("BossMod.WPos");
        var xField = wposType?.GetField("X", InstanceFlags);
        var zField = wposType?.GetField("Z", InstanceFlags);
        if (goalZones == null || forcedMovement == null || forbiddenZones == null || wposType == null || xField == null || zField == null)
        {
            this.lastReason = $"BMR healer coverage reflection members unavailable: {FormatMissing(
                (goalZones == null, "AIHints.GoalZones"),
                (forcedMovement == null, "AIHints.ForcedMovement"),
                (forbiddenZones == null, "AIHints.ForbiddenZones"),
                (wposType == null, "BossMod.WPos"),
                (xField == null, "BossMod.WPos.X"),
                (zField == null, "BossMod.WPos.Z"))}";
            return false;
        }

        this.resolvedHintsType = hintsType;
        this.resolvedWPosType = wposType;
        this.goalZonesField = goalZones;
        this.forcedMovementField = forcedMovement;
        this.forbiddenZonesField = forbiddenZones;
        this.wposXField = xField;
        this.wposZField = zField;
        return true;
    }

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

    internal static bool IsSingleMissingCoverageRestore(
        int currentCoveredCount,
        int bestCoveredCount,
        int totalMembers)
    {
        return totalMembers > 0 &&
               bestCoveredCount == totalMembers &&
               bestCoveredCount - currentCoveredCount == 1;
    }

    internal static bool ShouldCatchUpCriticalCoverage(
        int currentCoveredCount,
        int bestCoveredCount,
        int totalMembers,
        float distanceToCenter)
    {
        var criticalCoveredThreshold = Math.Max(1, totalMembers / 4);
        var partyClusterThreshold = Math.Max(1, (totalMembers + 1) / 2);
        return totalMembers > 0 &&
               currentCoveredCount <= criticalCoveredThreshold &&
               bestCoveredCount >= partyClusterThreshold &&
               bestCoveredCount > currentCoveredCount &&
               distanceToCenter <= MaxCriticalCoverageCatchUpMoveDistance;
    }

    internal static bool ShouldCatchUpForPartyAoeHeal(
        int currentCoveredCount,
        int bestCoveredCount,
        int totalMembers,
        float distanceToCenter,
        bool partyAoeHealPending)
    {
        return partyAoeHealPending &&
               totalMembers > 0 &&
               totalMembers - currentCoveredCount >= 2 &&
               bestCoveredCount >= Math.Max(1, totalMembers - 1) &&
               bestCoveredCount > currentCoveredCount &&
               distanceToCenter <= MaxPartyAoeHealCatchUpMoveDistance;
    }

    internal static bool ShouldImproveCoverageComfort(
        int currentCoveredCount,
        int bestCoveredCount,
        int totalMembers,
        float currentCoverageComfortSlack,
        float bestCoverageComfortSlack,
        float distanceToCenter,
        bool downtimeLikely,
        bool mechanicPositioningActive)
    {
        if (totalMembers <= 1 ||
            bestCoveredCount < currentCoveredCount ||
            bestCoveredCount <= 0 ||
            IsSingleMissingCoverageRestore(currentCoveredCount, bestCoveredCount, totalMembers) ||
            distanceToCenter > ResolveCoverageComfortMoveDistance(downtimeLikely, mechanicPositioningActive))
        {
            return false;
        }

        var usefulCluster = Math.Max(2, (totalMembers + 1) / 2);
        if (bestCoveredCount > currentCoveredCount)
        {
            return bestCoveredCount - currentCoveredCount >= MinimumCoverageGain &&
                   bestCoveredCount >= usefulCluster;
        }

        if (!downtimeLikely && !mechanicPositioningActive)
        {
            return false;
        }

        return currentCoveredCount >= usefulCluster &&
               bestCoverageComfortSlack >= currentCoverageComfortSlack + MinimumCoverageComfortSlackGain;
    }

    internal static bool ShouldImproveBossCoveragePosition(
        int currentCoveredCount,
        int bestCoveredCount,
        int totalMembers,
        float currentBossRangeScore,
        float bestBossRangeScore,
        bool bossModuleContext)
    {
        if (!bossModuleContext || totalMembers <= 1 || bestCoveredCount < currentCoveredCount)
        {
            return false;
        }

        if (bestCoveredCount > currentCoveredCount)
        {
            return bestCoveredCount - currentCoveredCount >= MinimumCoverageGain;
        }

        var usefulCluster = Math.Max(2, (totalMembers + 1) / 2);
        return currentCoveredCount >= usefulCluster &&
               bestBossRangeScore >= currentBossRangeScore + 0.15f;
    }

    internal static bool ShouldImproveUrgentHealingCoverage(
        int currentCoveredCount,
        int bestCoveredCount,
        int totalMembers,
        bool currentCoversTank,
        bool bestCoversTank,
        bool partyAoeHealPending,
        bool sharedDamageSoon,
        bool tankbusterHealCoveragePending)
    {
        if (totalMembers <= 0 || bestCoveredCount < currentCoveredCount)
        {
            return false;
        }

        if (tankbusterHealCoveragePending && !currentCoversTank && bestCoversTank)
        {
            return true;
        }

        if (!partyAoeHealPending && !sharedDamageSoon)
        {
            return false;
        }

        var missingMembers = totalMembers - currentCoveredCount;
        var coverageGain = bestCoveredCount - currentCoveredCount;
        return missingMembers >= 2 &&
               coverageGain >= MinimumUrgentCoverageDashGain &&
               bestCoveredCount >= Math.Max(1, totalMembers - 1);
    }

    internal static bool ShouldSuppressSlideWindowCoverage(bool slidecastWindow)
    {
        return slidecastWindow;
    }

    internal static bool ShouldAllowCoverageDashDuringBossModMovement(
        bool urgentHealingCoverage,
        bool partyAoeHealCatchUp,
        bool criticalCoverageCatchUp,
        bool tankbusterHealCoveragePending,
        bool mechanicPositioningActive,
        bool bossCoverageMove,
        bool proactiveCoverageComfort,
        bool strongCoverageGain)
    {
        return urgentHealingCoverage ||
               partyAoeHealCatchUp ||
               criticalCoverageCatchUp ||
               tankbusterHealCoveragePending ||
               mechanicPositioningActive && (bossCoverageMove || proactiveCoverageComfort || strongCoverageGain);
    }

    internal static bool ShouldUseCoverageDashLanding(
        int currentCoveredCount,
        int bestCoveredCount,
        int totalMembers,
        float distanceGain,
        int landingCoveredCount,
        bool urgent,
        bool tankbusterHealCoveragePending,
        bool currentCoversTank,
        bool landingCoversTank)
    {
        if (landingCoveredCount < currentCoveredCount)
        {
            return false;
        }

        if (tankbusterHealCoveragePending &&
            !currentCoversTank &&
            landingCoversTank &&
            distanceGain >= MinimumBossCoverageDashGain)
        {
            return true;
        }

        if (!urgent)
        {
            return distanceGain >= MinimumBossCoverageDashGain;
        }

        var coverageGain = landingCoveredCount - currentCoveredCount;
        return distanceGain >= MinimumBossCoverageDashGain &&
               (coverageGain >= MinimumUrgentCoverageDashGain ||
                landingCoveredCount >= Math.Min(bestCoveredCount, Math.Max(1, totalMembers - 1)));
    }

    private static string ResolveCoverageDashReason(
        bool urgentHealingCoverage,
        bool partyAoeHealCatchUp,
        bool criticalCoverageCatchUp,
        bool tankbusterHealCoveragePending,
        bool sharedDamageSoon,
        string partyAoeHealActionName)
    {
        if (tankbusterHealCoveragePending)
        {
            return "tankbuster healing coverage";
        }

        if (partyAoeHealCatchUp)
        {
            return $"party AoE heal coverage ({partyAoeHealActionName})";
        }

        if (criticalCoverageCatchUp)
        {
            return "critical party coverage";
        }

        if (urgentHealingCoverage && sharedDamageSoon)
        {
            return "shared damage healing coverage";
        }

        return urgentHealingCoverage
            ? "urgent healing coverage"
            : "boss healer coverage";
    }

    private static float ResolveCoverageComfortMoveDistance(bool downtimeLikely, bool mechanicPositioningActive)
    {
        if (downtimeLikely)
        {
            return MaxDowntimeCoverageComfortMoveDistance;
        }

        return mechanicPositioningActive
            ? MaxMechanicCoverageComfortMoveDistance
            : MaxRoutineCoverageComfortMoveDistance;
    }

    internal static bool IsPartyAoeHealAction(RsrAoeActionSnapshot action)
    {
        if (IsKnownOffensivePartyHeal(action.ActionName))
            return true;

        return action.IsFriendly &&
               action.Shape == RsrAoeShape.Circle &&
               action.Range <= 1.5f &&
               action.EffectRange >= MinimumPartyAoeHealEffectRange;
    }

    internal static bool ShouldYieldCoverageForSafety(
        bool forcedMovementActive,
        bool forbiddenSafetyActive,
        bool bossModGoalZoneActive,
        bool bmrMoveRequested,
        bool bmrMoveImminent)
    {
        _ = forbiddenSafetyActive;
        _ = bossModGoalZoneActive;
        _ = bmrMoveRequested;
        _ = bmrMoveImminent;
        return forcedMovementActive;
    }

    internal static bool ShouldSkipCoverageMoveForGcdTiming(
        float moveDistance,
        float gcdRemaining,
        float gcdElapsed,
        float gcdTotal,
        bool slidecastWindow,
        bool bossModSafetyMovementActive,
        out string reason)
    {
        reason = string.Empty;
        if (slidecastWindow)
        {
            reason = "healer coverage waits for instant movement instead of slidecast stepping";
            return true;
        }

        if (bossModSafetyMovementActive ||
            !AoeRepositionPolicy.HasReliableGcdTiming(gcdRemaining, gcdElapsed, gcdTotal))
        {
            return false;
        }

        var requiredSeconds = moveDistance / EstimatedCombatMoveSpeed + CoverageArrivalBufferSeconds;
        if (gcdRemaining >= requiredSeconds)
        {
            return false;
        }

        reason = $"healer coverage move too late for next cast ({moveDistance:0.0}y needs {requiredSeconds:0.0}s, {gcdRemaining:0.0}s left)";
        return true;
    }

    internal static bool IsBossModSafetyMovementActive(
        bool forcedMovementActive,
        bool forbiddenSafetyActive,
        bool bossModGoalZoneActive,
        bool bmrMoveImminent)
    {
        return forcedMovementActive ||
               bmrMoveImminent && (forbiddenSafetyActive || bossModGoalZoneActive);
    }

    private bool IsDowntimeLikely()
    {
        var target = services.TargetManager.Target;
        if (target == null || !target.IsTargetable)
        {
            return true;
        }

        var nextDowntimeStart = bossMod.NextDowntimeIn();
        var nextDowntimeEnd = bossMod.NextDowntimeEndIn();
        return IsFiniteTimelineValue(nextDowntimeEnd) &&
               (!IsFiniteTimelineValue(nextDowntimeStart) || nextDowntimeEnd < nextDowntimeStart);
    }

    private static bool IsFiniteTimelineValue(float value) => !float.IsNaN(value) && !float.IsInfinity(value) && value < float.MaxValue * 0.5f;

    private static bool IsKnownOffensivePartyHeal(string actionName)
    {
        return string.Equals(actionName, "Pneuma", StringComparison.OrdinalIgnoreCase);
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
        return type.GetField(name, InstanceFlags)?.GetValue(value) switch
        {
            float f => f,
            double d => (float)d,
            _ => 0f
        };
    }

    private readonly record struct BossCenterAvoidance(Vector2 Center, float Radius)
    {
        public bool Allows(Vector2 candidate)
        {
            return Vector2.DistanceSquared(candidate, this.Center) >= this.Radius * this.Radius;
        }

        public bool SameSource(BossCenterAvoidance other)
        {
            return MathF.Abs(this.Radius - other.Radius) <= 0.25f &&
                   Vector2.DistanceSquared(this.Center, other.Center) <= 9f;
        }
    }

    private readonly record struct BossRangePreference(Vector2 Center, float DesiredDistance)
    {
        public float Score(Vector2 candidate)
        {
            var distance = Vector2.Distance(candidate, this.Center);
            if (distance >= this.DesiredDistance)
            {
                return 1f;
            }

            return Math.Clamp(distance / MathF.Max(1f, this.DesiredDistance), 0f, 1f);
        }

        public bool SameSource(BossRangePreference other)
        {
            return MathF.Abs(this.DesiredDistance - other.DesiredDistance) <= 1f &&
                   Vector2.DistanceSquared(this.Center, other.Center) <= 9f;
        }
    }

    private sealed record HealerCoverageMoveDecision(
        long GcdWindowId,
        Vector2 Center,
        Delegate Goal,
        HealerCoverageOverlaySnapshot Overlay,
        int BestCoveredCount,
        int TotalMembers,
        BossModGoalPriority Priority,
        string ReasonPrefix);

    private sealed class HealerCoverageGoalPlan
    {
        private static readonly MethodInfo ScoreFromWPosMethod = typeof(HealerCoverageGoalPlan).GetMethod(nameof(ScoreFromWPos), BindingFlags.Instance | BindingFlags.NonPublic)!;

        // Healer's current position for non-movement overlay display.
        private readonly Vector2 currentCenter;
        // Members covered from the healer's current position.
        private readonly IReadOnlyList<Vector2> currentCoveredMembers;
        // Optimal healer position that covers the most party members.
        private readonly Vector2 optimalCenter;
        // Members covered from optimalCenter (for overlay display).
        private readonly IReadOnlyList<Vector2> coveredMembers;
        // All visible party member positions (for scoring any candidate healer position).
        private readonly IReadOnlyList<Vector2> allMembers;
        private readonly Vector2? tankPosition;
        private readonly float distanceToCenter;
        private readonly int currentCoveredCount;
        private readonly bool currentCoversTank;
        private readonly bool bestCoversTank;
        private readonly float currentCoverageComfortSlack;
        private readonly float bestCoverageComfortSlack;
        private readonly BossCenterAvoidance? bossCenterAvoidance;
        private readonly BossRangePreference? bossRangePreference;
        private readonly float currentBossRangeScore;

        public HealerCoverageGoalPlan(
            Vector2 currentCenter,
            IReadOnlyList<Vector2> currentCoveredMembers,
            Vector2 optimalCenter,
            IReadOnlyList<Vector2> coveredMembers,
            IReadOnlyList<Vector2> allMembers,
            Vector2? tankPosition,
            float distanceToCenter,
            int currentCoveredCount,
            bool currentCoversTank,
            bool bestCoversTank,
            float currentCoverageComfortSlack,
            float bestCoverageComfortSlack,
            BossCenterAvoidance? bossCenterAvoidance,
            BossRangePreference? bossRangePreference,
            float currentBossRangeScore)
        {
            this.currentCenter = currentCenter;
            this.currentCoveredMembers = currentCoveredMembers;
            this.optimalCenter = optimalCenter;
            this.coveredMembers = coveredMembers;
            this.allMembers = allMembers;
            this.tankPosition = tankPosition;
            this.distanceToCenter = distanceToCenter;
            this.currentCoveredCount = currentCoveredCount;
            this.currentCoversTank = currentCoversTank;
            this.bestCoversTank = bestCoversTank;
            this.currentCoverageComfortSlack = currentCoverageComfortSlack;
            this.bestCoverageComfortSlack = bestCoverageComfortSlack;
            this.bossCenterAvoidance = bossCenterAvoidance;
            this.bossRangePreference = bossRangePreference;
            this.currentBossRangeScore = currentBossRangeScore;
        }

        public float DistanceToCenter => this.distanceToCenter;
        public Vector2 OptimalCenter => this.optimalCenter;
        public int BestCoveredCount => this.coveredMembers.Count;
        public int CurrentCoveredCount => this.currentCoveredCount;
        public int TotalMembers => this.allMembers.Count;
        public bool HasTank => this.tankPosition.HasValue;
        public bool CurrentCoversTank => this.currentCoversTank;
        public bool BestCoversTank => this.bestCoversTank;
        public float CurrentCoverageComfortSlack => this.currentCoverageComfortSlack;
        public float BestCoverageComfortSlack => this.bestCoverageComfortSlack;
        public IReadOnlyList<Vector2> AllMembers => this.allMembers;
        public float CurrentBossRangeScore => this.currentBossRangeScore;
        public float BestBossRangeScore => this.bossRangePreference?.Score(this.optimalCenter) ?? 0f;

        public bool CoversTankAt(Vector2 candidate)
            => CoversTank(candidate, this.tankPosition);

        public bool SameSource(HealerCoverageGoalPlan other)
        {
            if (Vector2.DistanceSquared(this.optimalCenter, other.optimalCenter) > 9f ||
                this.coveredMembers.Count != other.coveredMembers.Count ||
                this.allMembers.Count != other.allMembers.Count ||
                this.tankPosition.HasValue != other.tankPosition.HasValue ||
                this.bossCenterAvoidance.HasValue != other.bossCenterAvoidance.HasValue ||
                this.bossRangePreference.HasValue != other.bossRangePreference.HasValue ||
                this.bossCenterAvoidance.HasValue &&
                !this.bossCenterAvoidance.Value.SameSource(other.bossCenterAvoidance!.Value) ||
                this.bossRangePreference.HasValue &&
                !this.bossRangePreference.Value.SameSource(other.bossRangePreference!.Value) ||
                this.tankPosition.HasValue &&
                Vector2.DistanceSquared(this.tankPosition.Value, other.tankPosition!.Value) > 9f)
            {
                return false;
            }

            for (var i = 0; i < this.allMembers.Count; i++)
            {
                if (Vector2.DistanceSquared(this.allMembers[i], other.allMembers[i]) > 9f)
                    return false;
            }

            return true;
        }

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

        public HealerCoverageOverlaySnapshot CreateOverlay(float y, bool injected)
        {
            var center = injected ? this.optimalCenter : this.currentCenter;
            var members = injected ? this.coveredMembers : this.currentCoveredMembers;
            var distance = injected ? this.distanceToCenter : 0f;
            var memberPositions = new Vector3[members.Count];
            for (var i = 0; i < members.Count; i++)
                memberPositions[i] = new Vector3(members[i].X, y, members[i].Y);
            return new(new Vector3(center.X, y, center.Y), CoverageRadius, injected, distance, members.Count, this.allMembers.Count, memberPositions);
        }

        private float ScoreFromWPos(float x, float z)
        {
            // Score by how many members the healer's 20y circle would cover at this position.
            var candidatePos = new Vector2(x, z);
            if (this.bossCenterAvoidance.HasValue && !this.bossCenterAvoidance.Value.Allows(candidatePos))
            {
                return 0f;
            }

            var covered = 0;
            foreach (var member in allMembers)
            {
                if (Vector2.DistanceSquared(candidatePos, member) <= CoverageRadiusSquared)
                    covered++;
            }

            if (covered == 0)
                return 0f;

            var fraction = (float)covered / allMembers.Count;
            var score = PreferredScore * fraction;
            if (this.bestCoverageComfortSlack >= this.currentCoverageComfortSlack + MinimumCoverageComfortSlackGain &&
                covered >= this.currentCoveredCount &&
                PreservesCoverage(candidatePos, this.coveredMembers))
            {
                var distanceToPreferred = Vector2.Distance(candidatePos, this.optimalCenter);
                var preferredGain = Math.Clamp(1f - (distanceToPreferred / PreferredCoveragePointRadius), 0f, 1f);
                score = MathF.Min(GoalZoneScorePolicy.StrongPreference, score + preferredGain * MaxCoverageComfortScoreBonus);
            }

            if (this.tankPosition.HasValue)
            {
                var coversTank = Vector2.DistanceSquared(candidatePos, this.tankPosition.Value) <= CoverageRadiusSquared;
                score = coversTank
                    ? MathF.Min(GoalZoneScorePolicy.StrongPreference, score + TankCoverageBonus)
                    : score * 0.8f;
            }

            return score;
        }
    }
}

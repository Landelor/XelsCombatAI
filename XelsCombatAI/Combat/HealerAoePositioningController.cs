using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq.Expressions;
using System.Numerics;
using System.Reflection;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Game.ClientState.Objects.Types;
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
    RotationSolverActionReflection rotationSolverActions,
    Func<bool> automatedMovementSuppressed)
    : IBossModGoalZoneContributor
{
    private static readonly BindingFlags InstanceFlags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

    // Healer AoE heals and support actions commonly use a 20y radius around the healer.
    private const float CoverageRadius = 20f;
    private const float CoverageRadiusSquared = CoverageRadius * CoverageRadius;
    private const float MaxConvenienceMoveDistance = 6f;
    private const float MaxTankCoverageRestoreDistance = 4.5f;
    private const float MaxSingleMemberFullCoverageMoveDistance = 6f;
    private const float MaxCriticalCoverageCatchUpMoveDistance = 60f;
    private const float MaxPartyAoeHealCatchUpMoveDistance = 30f;
    private const float MinimumPartyAoeHealEffectRange = 8f;
    private const int MinimumCoverageGain = 2;
    private const float PreferredScore = GoalZoneScorePolicy.NormalPreference;
    private const float TankCoverageBonus = 0.25f;

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

        if (!this.EnsureResolved(hints.GetType()))
            return;

        if (this.goalZonesField!.GetValue(hints) is not System.Collections.IList)
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
        var plan = this.BuildPlan(player, members, tank);

        this.lastDistanceToCenter = plan.DistanceToCenter;
        this.lastCoveredMembers = plan.BestCoveredCount;
        var coverageGain = plan.BestCoveredCount - plan.CurrentCoveredCount;
        var restoresTankCoverage = plan.HasTank &&
                                    !plan.CurrentCoversTank &&
                                    plan.BestCoversTank;
        var strongCoverageGain = coverageGain >= MinimumCoverageGain ||
                                 plan.CurrentCoveredCount == 0 && coverageGain > 0;
        var restoresFullCoverage = ShouldRestoreSingleMissingCoverage(
            plan.CurrentCoveredCount,
            plan.BestCoveredCount,
            plan.TotalMembers,
            plan.DistanceToCenter);
        var partyAoeHealPending = this.TryGetUpcomingPartyAoeHeal(out var partyAoeHealActionName);
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
        var boundedTankRestore = restoresTankCoverage &&
                                 plan.DistanceToCenter <= MaxTankCoverageRestoreDistance;
        var shouldMove = strongCoverageGain || restoresFullCoverage || criticalCoverageCatchUp || partyAoeHealCatchUp || boundedTankRestore;
        var maxMoveDistance = criticalCoverageCatchUp
            ? MaxCriticalCoverageCatchUpMoveDistance
            : partyAoeHealCatchUp
            ? MaxPartyAoeHealCatchUpMoveDistance
            : MaxConvenienceMoveDistance;
        var priority = criticalCoverageCatchUp || partyAoeHealCatchUp
            ? BossModGoalPriority.DefensiveMechanic
            : BossModGoalPriority.Uptime;

        if (this.lastPlan == null || !this.lastPlan.SameSource(plan))
        {
            this.lastGoalDelegate = plan.CreateGoalDelegate(this.resolvedWPosType!, this.wposXField!, this.wposZField!);
            this.lastPlan = plan;
        }

        if (shouldMove && this.BossModHardSafetyActive(hints))
        {
            shouldMove = false;
            this.lastReason = "forced mechanic movement active";
        }
        else if (shouldMove && plan.DistanceToCenter > maxMoveDistance)
        {
            shouldMove = false;
            this.lastReason = partyAoeHealPending
                ? $"party AoE heal coverage point too far: {plan.DistanceToCenter:0.0}y"
                : $"coverage point too far: {plan.DistanceToCenter:0.0}y";
        }
        else if (!shouldMove && restoresTankCoverage && plan.DistanceToCenter > MaxTankCoverageRestoreDistance)
        {
            this.lastReason = $"tank coverage point too far: {plan.DistanceToCenter:0.0}y";
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
            contributions.Add(new(this.lastGoalDelegate!, priority, "Healer coverage zone"));
            var prefix = partyAoeHealCatchUp
                ? $"party AoE heal ({partyAoeHealActionName}); "
                : criticalCoverageCatchUp
                ? "critical party coverage; "
                : restoresTankCoverage
                ? "tank out of coverage; "
                : string.Empty;
            this.lastReason = $"{prefix}covering {plan.CurrentCoveredCount}/{plan.TotalMembers}, can cover {plan.BestCoveredCount}/{plan.TotalMembers}";
        }

        this.lastInjected = shouldMove;
        this.lastOverlay = plan.CreateOverlay(player.Position.Y, injected: shouldMove);
    }

    private HealerCoverageGoalPlan BuildPlan(IBattleChara player, IReadOnlyList<IBattleChara> members, IBattleChara? tank)
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

        var currentCovered = CountCovered(playerPos, allPositions);
        var currentCoversTank = tankPosition.HasValue &&
                                Vector2.DistanceSquared(playerPos, tankPosition.Value) <= CoverageRadiusSquared;
        var bestCenter = SelectBestCenter(playerPos, allPositions, tankPosition, this.lastPlan?.OptimalCenter);
        var bestCovered = GetCoveredMembers(bestCenter, allPositions);
        var bestCoversTank = tankPosition.HasValue &&
                             Vector2.DistanceSquared(bestCenter, tankPosition.Value) <= CoverageRadiusSquared;

        if (bestCovered.Count < currentCovered ||
            (bestCovered.Count == currentCovered && currentCoversTank && !bestCoversTank))
        {
            bestCenter = playerPos;
            bestCovered = GetCoveredMembers(playerPos, allPositions);
            bestCoversTank = currentCoversTank;
        }

        return new HealerCoverageGoalPlan(
            bestCenter,
            bestCovered,
            allPositions,
            tankPosition,
            Vector2.Distance(playerPos, bestCenter),
            currentCovered,
            currentCoversTank,
            bestCoversTank);
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

    internal static Vector2 SelectBestCenter(Vector2 playerPosition, IReadOnlyList<Vector2> members, Vector2? tankPosition, Vector2? previousCenter = null)
    {
        var best = playerPosition;
        var bestCovered = CountCovered(best, members);
        var bestCoversTank = tankPosition.HasValue &&
                             Vector2.DistanceSquared(best, tankPosition.Value) <= CoverageRadiusSquared;
        var bestDistance = 0f;

        foreach (var candidate in EnumerateCoverageCenters(playerPosition, members))
        {
            var covered = CountCovered(candidate, members);
            var coversTank = tankPosition.HasValue &&
                             Vector2.DistanceSquared(candidate, tankPosition.Value) <= CoverageRadiusSquared;
            var distance = Vector2.Distance(playerPosition, candidate);
            if (covered > bestCovered ||
                (covered == bestCovered && coversTank && !bestCoversTank) ||
                (covered == bestCovered && coversTank == bestCoversTank && distance < bestDistance))
            {
                best = candidate;
                bestCovered = covered;
                bestCoversTank = coversTank;
                bestDistance = distance;
            }
        }

        var playerIsAlreadyBest = Vector2.DistanceSquared(best, playerPosition) <= 0.25f;
        if (!playerIsAlreadyBest && TrySelectNaturalCoverageCenter(best, members, tankPosition, out var naturalCenter))
        {
            best = naturalCenter;
        }

        if (!playerIsAlreadyBest &&
            previousCenter.HasValue &&
            ShouldRetainPreviousCenter(previousCenter.Value, best, members, tankPosition))
        {
            best = previousCenter.Value;
        }

        return best;
    }

    private static bool TrySelectNaturalCoverageCenter(
        Vector2 selectedCenter,
        IReadOnlyList<Vector2> members,
        Vector2? tankPosition,
        out Vector2 naturalCenter)
    {
        naturalCenter = selectedCenter;
        var selectedCovered = GetCoveredMembers(selectedCenter, members);
        if (selectedCovered.Count <= 1)
        {
            return false;
        }

        var candidate = AveragePosition(selectedCovered);
        if (CountCovered(candidate, members) < selectedCovered.Count)
        {
            return false;
        }

        if (CoversTank(selectedCenter, tankPosition) && !CoversTank(candidate, tankPosition))
        {
            return false;
        }

        naturalCenter = candidate;
        return true;
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
        yield return AveragePosition(members);

        for (var i = 0; i < members.Count; ++i)
        {
            yield return members[i];
            for (var j = i + 1; j < members.Count; ++j)
            {
                yield return (members[i] + members[j]) * 0.5f;
            }
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

    internal static bool ShouldRestoreSingleMissingCoverage(
        int currentCoveredCount,
        int bestCoveredCount,
        int totalMembers,
        float distanceToCenter)
    {
        return totalMembers > 0 &&
               bestCoveredCount == totalMembers &&
               bestCoveredCount > currentCoveredCount &&
               distanceToCenter <= MaxSingleMemberFullCoverageMoveDistance;
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
        bool bmrMoveRequested,
        bool bmrMoveImminent)
    {
        _ = forbiddenSafetyActive;
        _ = bmrMoveRequested;
        _ = bmrMoveImminent;
        return forcedMovementActive;
    }

    private bool BossModHardSafetyActive(object hints)
    {
        return ShouldYieldCoverageForSafety(
            VectorLengthSquared(this.forcedMovementField?.GetValue(hints)) > 0.01f,
            this.forbiddenZonesField?.GetValue(hints) is ICollection { Count: > 0 },
            this.bmrMoveRequested,
            this.bmrMoveImminent);
    }

    private bool TryGetUpcomingPartyAoeHeal(out string actionName)
    {
        actionName = "<none>";
        if (!rotationSolverActions.TryGetUpcomingGcd(requirePreview: false, out var action, out _) ||
            !IsPartyAoeHealAction(action))
        {
            return false;
        }

        actionName = action.ActionName;
        return true;
    }

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

    private sealed class HealerCoverageGoalPlan
    {
        private static readonly MethodInfo ScoreFromWPosMethod = typeof(HealerCoverageGoalPlan).GetMethod(nameof(ScoreFromWPos), BindingFlags.Instance | BindingFlags.NonPublic)!;

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

        public HealerCoverageGoalPlan(
            Vector2 optimalCenter,
            IReadOnlyList<Vector2> coveredMembers,
            IReadOnlyList<Vector2> allMembers,
            Vector2? tankPosition,
            float distanceToCenter,
            int currentCoveredCount,
            bool currentCoversTank,
            bool bestCoversTank)
        {
            this.optimalCenter = optimalCenter;
            this.coveredMembers = coveredMembers;
            this.allMembers = allMembers;
            this.tankPosition = tankPosition;
            this.distanceToCenter = distanceToCenter;
            this.currentCoveredCount = currentCoveredCount;
            this.currentCoversTank = currentCoversTank;
            this.bestCoversTank = bestCoversTank;
        }

        public float DistanceToCenter => this.distanceToCenter;
        public Vector2 OptimalCenter => this.optimalCenter;
        public int BestCoveredCount => this.coveredMembers.Count;
        public int CurrentCoveredCount => this.currentCoveredCount;
        public int TotalMembers => this.allMembers.Count;
        public bool HasTank => this.tankPosition.HasValue;
        public bool CurrentCoversTank => this.currentCoversTank;
        public bool BestCoversTank => this.bestCoversTank;

        public bool SameSource(HealerCoverageGoalPlan other)
        {
            if (Vector2.DistanceSquared(this.optimalCenter, other.optimalCenter) > 9f ||
                this.coveredMembers.Count != other.coveredMembers.Count ||
                this.allMembers.Count != other.allMembers.Count ||
                this.tankPosition.HasValue != other.tankPosition.HasValue ||
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
            var memberPositions = new Vector3[coveredMembers.Count];
            for (var i = 0; i < coveredMembers.Count; i++)
                memberPositions[i] = new Vector3(coveredMembers[i].X, y, coveredMembers[i].Y);
            return new(new Vector3(optimalCenter.X, y, optimalCenter.Y), CoverageRadius, injected, distanceToCenter, this.coveredMembers.Count, this.allMembers.Count, memberPositions);
        }

        private float ScoreFromWPos(float x, float z)
        {
            // Score by how many members the healer's 20y circle would cover at this position.
            var candidatePos = new Vector2(x, z);
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

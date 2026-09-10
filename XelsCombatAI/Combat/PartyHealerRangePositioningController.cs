using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;
using System.Numerics;
using System.Reflection;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Game.ClientState.Objects.Types;
using XelsCombatAI.Game;
using XelsCombatAI.Integrations;

namespace XelsCombatAI.Combat;

internal sealed record PartyHealerRangePositioningStatus(
    string HookState,
    string LastReason,
    bool Injected,
    string HealerName,
    float DistanceToHealer,
    float DistanceToEntry);

internal sealed record PartyHealerRangeOverlaySnapshot(
    Vector3 HealerPosition,
    Vector3 PreferredEntryPosition,
    float Radius,
    bool PlayerInRange,
    bool Injected,
    string HealerName,
    float DistanceToHealer,
    float DistanceToEntry);

internal sealed class PartyHealerRangePositioningController(
    Configuration config,
    DalamudServices services,
    Func<bool> automatedMovementSuppressed,
    Func<BossModMechanicPressure> mechanicPressure)
    : IBossModGoalZoneContributor
{
    private const float CoverageRadius = 20f;
    private const float CoverageRadiusSquared = CoverageRadius * CoverageRadius;
    private const float CoverageEntryMargin = 1.5f;
    private const float PreferredEntryRadius = 1.5f;
    private const float MaxRaidDamageMoveDistance = 12f;
    private const float PreferredEntryScore = GoalZoneScorePolicy.NormalPreference;
    private const float InsideScore = GoalZoneScorePolicy.WeakPreference;
    private static readonly BindingFlags InstanceFlags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
    private static readonly MethodInfo ScoreFromWPosMethod =
        typeof(PartyHealerRangePositioningController).GetMethod(nameof(ScoreFromWPos), BindingFlags.Instance | BindingFlags.NonPublic)!;

    private FieldInfo? goalZonesField;
    private FieldInfo? forcedMovementField;
    private FieldInfo? wposXField;
    private FieldInfo? wposZField;
    private Type? resolvedHintsType;
    private Type? resolvedWPosType;
    private string hookState = "unresolved";
    private string lastReason = "not evaluated";
    private bool lastInjected;
    private string lastHealerName = "<none>";
    private float lastDistanceToHealer;
    private float lastDistanceToEntry;
    private Delegate? lastGoalDelegate;
    private HealerRangePlan? lastPlan;
    private PartyHealerRangeOverlaySnapshot? lastOverlay;
    private bool bmrMoveImminent;

    public PartyHealerRangePositioningStatus Status => new(
        this.hookState,
        this.lastReason,
        this.lastInjected,
        this.lastHealerName,
        this.lastDistanceToHealer,
        this.lastDistanceToEntry);

    public PartyHealerRangeOverlaySnapshot? Overlay => this.lastOverlay;

    public void SetHookState(string state)
    {
        this.hookState = state;
    }

    public void SetBossModMovementState(bool moveRequested, bool moveImminent)
    {
        _ = moveRequested;
        this.bmrMoveImminent = moveImminent;
    }

    public void Reset()
    {
        this.goalZonesField = null;
        this.forcedMovementField = null;
        this.wposXField = null;
        this.wposZField = null;
        this.resolvedHintsType = null;
        this.resolvedWPosType = null;
        this.lastReason = "reset";
        this.lastInjected = false;
        this.lastHealerName = "<none>";
        this.lastDistanceToHealer = 0f;
        this.lastDistanceToEntry = 0f;
        this.lastGoalDelegate = null;
        this.lastPlan = null;
        this.lastOverlay = null;
        this.bmrMoveImminent = false;
    }

    public void TryInjectGoal(object hints, ICollection<BossModGoalContribution> contributions)
    {
        this.lastInjected = false;

        if (!config.Enabled || !config.ManageHealerCoverageZone)
        {
            this.SetInactive("disabled");
            return;
        }

        if (!config.ManageMovement)
        {
            this.SetInactive("movement management disabled");
            return;
        }

        if (!CombatEngagementDetector.IsEffectivelyInCombat(services) || services.Condition[ConditionFlag.Unconscious])
        {
            this.SetInactive("not active in combat");
            return;
        }

        if (automatedMovementSuppressed())
        {
            this.SetInactive("automated movement suppressed");
            return;
        }

        var player = services.ObjectTable.LocalPlayer;
        if (player == null)
        {
            this.SetInactive("local player unavailable");
            return;
        }

        if (ShouldSkipPartyHealerRangeForRole(player.ClassJob.RowId))
        {
            this.SetInactive(JobRoles.IsTankJob(player.ClassJob.RowId)
                ? "tank role skips party healer range"
                : "healer role uses healer coverage");
            return;
        }

        if (CasterMovementPolicy.ShouldSuppressAdvisoryMovement(player))
        {
            this.SetInactive("player casting");
            return;
        }

        var pressure = mechanicPressure();
        if (!ShouldMoveForPartyHealerRange(pressure))
        {
            this.SetInactive("no raid damage pressure");
            return;
        }

        if (pressure.MovementLockSoon || pressure.KnockbackSoon || pressure.FreezingSoon || pressure.MisdirectionActive)
        {
            this.SetInactive(pressure.FormatOptionalMovementHoldReason());
            return;
        }

        if (!this.EnsureResolved(hints.GetType()))
        {
            return;
        }

        var forcedMovementActive = VectorLengthSquared(this.forcedMovementField?.GetValue(hints)) > 0.01f;
        if (forcedMovementActive || this.bmrMoveImminent)
        {
            this.SetInactive("BossMod movement active");
            return;
        }

        if (this.goalZonesField!.GetValue(hints) is not IList)
        {
            this.SetInactive("BMR goal zone list unavailable");
            return;
        }

        var healers = PartyAllyProvider
            .EnumerateVisiblePartyAllies(services, player)
            .Where(ally => JobRoles.GetRangeRole(ally.ClassJob.RowId) == RangeRole.Healer)
            .ToList();
        if (healers.Count == 0)
        {
            this.SetInactive("no visible party healer");
            return;
        }

        var playerPosition = new Vector2(player.Position.X, player.Position.Z);
        var previousPlan = this.lastPlan;
        var plan = SelectBestHealerRangePlan(playerPosition, healers);
        this.lastHealerName = plan.HealerName;
        this.lastDistanceToHealer = plan.DistanceToHealer;
        this.lastDistanceToEntry = plan.DistanceToEntry;
        this.lastPlan = plan;
        this.lastOverlay = plan.CreateOverlay(player.Position.Y, injected: false);

        if (plan.PlayerInRange)
        {
            this.lastReason = $"inside {plan.HealerName} healer range";
            return;
        }

        if (plan.DistanceToEntry > MaxRaidDamageMoveDistance)
        {
            this.lastReason = $"party healer range too far: {plan.DistanceToEntry:0.0}y";
            return;
        }

        if (this.lastGoalDelegate == null || previousPlan == null || !plan.SameSource(previousPlan))
        {
            this.lastGoalDelegate = this.CreateGoalDelegate();
        }

        contributions.Add(new(
            this.lastGoalDelegate,
            BossModGoalPriority.DefensiveMechanic,
            "Party healer range",
            plan.PreferredEntryPosition));
        this.lastInjected = true;
        this.lastOverlay = plan.CreateOverlay(player.Position.Y, injected: true);
        this.lastReason = $"moving into {plan.HealerName} healer range before raid damage ({plan.DistanceToEntry:0.0}y)";
    }

    internal static bool ShouldSkipPartyHealerRangeForRole(uint classJobId)
    {
        return JobRoles.IsTankJob(classJobId) ||
               JobRoles.GetRangeRole(classJobId) == RangeRole.Healer;
    }

    internal static bool ShouldMoveForPartyHealerRange(BossModMechanicPressure pressure)
    {
        return pressure.RaidwideSoon || pressure.SharedDamageSoon;
    }

    internal static Vector2 FindPreferredEntryPosition(Vector2 playerPosition, Vector2 healerPosition, float distanceToHealer)
    {
        if (distanceToHealer <= CoverageRadius)
        {
            return playerPosition;
        }

        if (distanceToHealer <= 0.01f)
        {
            return healerPosition;
        }

        var direction = Vector2.Normalize(playerPosition - healerPosition);
        return healerPosition + direction * (CoverageRadius - CoverageEntryMargin);
    }

    private void SetInactive(string reason)
    {
        this.lastReason = reason;
        this.lastInjected = false;
        this.lastHealerName = "<none>";
        this.lastDistanceToHealer = 0f;
        this.lastDistanceToEntry = 0f;
        this.lastGoalDelegate = null;
        this.lastPlan = null;
        this.lastOverlay = null;
    }

    private bool EnsureResolved(Type hintsType)
    {
        if (this.resolvedHintsType == hintsType &&
            this.goalZonesField != null &&
            this.forcedMovementField != null &&
            this.wposXField != null &&
            this.wposZField != null)
        {
            return true;
        }

        var goalZones = hintsType.GetField("GoalZones", InstanceFlags);
        var forcedMovement = hintsType.GetField("ForcedMovement", InstanceFlags);
        var wposType = hintsType.Assembly.GetType("BossMod.WPos");
        var xField = wposType?.GetField("X", InstanceFlags);
        var zField = wposType?.GetField("Z", InstanceFlags);
        if (goalZones == null || forcedMovement == null || wposType == null || xField == null || zField == null)
        {
            this.SetInactive($"BMR party healer range reflection members unavailable: {FormatMissing(
                (goalZones == null, "AIHints.GoalZones"),
                (forcedMovement == null, "AIHints.ForcedMovement"),
                (wposType == null, "BossMod.WPos"),
                (xField == null, "BossMod.WPos.X"),
                (zField == null, "BossMod.WPos.Z"))}");
            return false;
        }

        this.resolvedHintsType = hintsType;
        this.resolvedWPosType = wposType;
        this.goalZonesField = goalZones;
        this.forcedMovementField = forcedMovement;
        this.wposXField = xField;
        this.wposZField = zField;
        return true;
    }

    private Delegate CreateGoalDelegate()
    {
        var parameter = Expression.Parameter(this.resolvedWPosType!, "p");
        var call = Expression.Call(
            Expression.Constant(this),
            ScoreFromWPosMethod,
            Expression.Convert(Expression.Field(parameter, this.wposXField!), typeof(float)),
            Expression.Convert(Expression.Field(parameter, this.wposZField!), typeof(float)));
        var delegateType = typeof(Func<,>).MakeGenericType(this.resolvedWPosType!, typeof(float));
        return Expression.Lambda(delegateType, call, parameter).Compile();
    }

    private float ScoreFromWPos(float x, float z)
    {
        var plan = this.lastPlan;
        if (plan == null)
        {
            return 0f;
        }

        var point = new Vector2(x, z);
        if (Vector2.DistanceSquared(point, plan.HealerPosition) > CoverageRadiusSquared)
        {
            return 0f;
        }

        var preferredDistance = Vector2.Distance(point, plan.PreferredEntryPosition);
        return preferredDistance <= PreferredEntryRadius
            ? PreferredEntryScore
            : InsideScore;
    }

    private static HealerRangePlan SelectBestHealerRangePlan(Vector2 playerPosition, IReadOnlyList<IBattleChara> healers)
    {
        HealerRangePlan? best = null;
        foreach (var healer in healers)
        {
            var healerPosition = new Vector2(healer.Position.X, healer.Position.Z);
            var distanceToHealer = Vector2.Distance(playerPosition, healerPosition);
            var preferredEntryPosition = FindPreferredEntryPosition(playerPosition, healerPosition, distanceToHealer);
            var candidate = new HealerRangePlan(
                healer.Name.TextValue,
                healer.GameObjectId,
                healerPosition,
                preferredEntryPosition,
                distanceToHealer,
                Vector2.Distance(playerPosition, preferredEntryPosition),
                distanceToHealer <= CoverageRadius);
            if (best == null ||
                candidate.PlayerInRange && !best.PlayerInRange ||
                candidate.PlayerInRange == best.PlayerInRange && candidate.DistanceToEntry < best.DistanceToEntry)
            {
                best = candidate;
            }
        }

        return best!;
    }

    private static float VectorLengthSquared(object? value)
    {
        if (value == null)
        {
            return 0f;
        }

        var type = value.GetType();
        var x = type.GetField("X", InstanceFlags)?.GetValue(value);
        var z = type.GetField("Z", InstanceFlags)?.GetValue(value);
        return ConvertToSingle(x) is { } xf && ConvertToSingle(z) is { } zf
            ? (xf * xf) + (zf * zf)
            : 0f;
    }

    private static float? ConvertToSingle(object? value)
    {
        return value switch
        {
            float f => f,
            double d => (float)d,
            _ => null
        };
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

    private sealed record HealerRangePlan(
        string HealerName,
        ulong HealerId,
        Vector2 HealerPosition,
        Vector2 PreferredEntryPosition,
        float DistanceToHealer,
        float DistanceToEntry,
        bool PlayerInRange)
    {
        public PartyHealerRangeOverlaySnapshot CreateOverlay(float y, bool injected)
        {
            return new(
                new Vector3(this.HealerPosition.X, y, this.HealerPosition.Y),
                new Vector3(this.PreferredEntryPosition.X, y, this.PreferredEntryPosition.Y),
                CoverageRadius,
                this.PlayerInRange,
                injected,
                this.HealerName,
                this.DistanceToHealer,
                this.DistanceToEntry);
        }

        public bool SameSource(HealerRangePlan other)
        {
            return this.HealerId == other.HealerId &&
                   this.PlayerInRange == other.PlayerInRange &&
                   Vector2.DistanceSquared(this.HealerPosition, other.HealerPosition) <= 1f &&
                   Vector2.DistanceSquared(this.PreferredEntryPosition, other.PreferredEntryPosition) <= 1f;
        }
    }
}

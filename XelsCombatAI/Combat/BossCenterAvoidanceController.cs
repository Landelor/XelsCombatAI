using System;
using System.Collections;
using System.Collections.Generic;
using System.Numerics;
using System.Reflection;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Game.ClientState.Objects.Types;
using XelsCombatAI.Game;
using XelsCombatAI.Integrations;

namespace XelsCombatAI.Combat;

internal sealed record BossCenterAvoidanceOverlaySnapshot(
    Vector3 TargetPosition,
    float Radius,
    bool Injected,
    string Reason);

internal sealed class BossCenterAvoidanceController(
    Configuration config,
    DalamudServices services,
    Func<bool> automatedMovementSuppressed,
    Func<bool> currentTargetHasBossModule,
    Func<BossModMechanicPressure> mechanicPressure)
    : IBossModGoalZoneContributor
{
    private const float BossHitboxAvoidanceMargin = 0.35f;
    private const float CandidateExtraDistance = 0.9f;
    internal const float BossLikeHitboxRadius = 4f;
    private static readonly TimeSpan PostMechanicCenterCooldown = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan InsideCenterDwell = TimeSpan.FromMilliseconds(750);
    private static readonly BindingFlags InstanceFlags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

    private FieldInfo? goalZonesField;
    private FieldInfo? forcedMovementField;
    private FieldInfo? forbiddenZonesField;
    private FieldInfo? recommendedPositionalField;
    private FieldInfo? wposXField;
    private FieldInfo? wposZField;
    private Type? resolvedHintsType;
    private Type? resolvedWPosType;
    private Delegate? lastGoalDelegate;
    private Vector2 lastTargetPosition;
    private float lastAvoidanceRadius;
    private BossCenterAvoidanceOverlaySnapshot? lastOverlay;
    private string hookState = "unresolved";
    private string lastReason = "not evaluated";
    private DateTime suppressComfortUntil = DateTime.MinValue;
    private DateTime insideCenterSince = DateTime.MinValue;
    private bool bmrMoveRequested;
    private bool bmrMoveImminent;

    public string LastReason => this.lastReason;

    public BossCenterAvoidanceOverlaySnapshot? Overlay => this.lastOverlay;

    internal static float AvoidanceRadius(float hitboxRadius)
    {
        return Math.Max(1.25f, hitboxRadius + BossHitboxAvoidanceMargin);
    }

    internal static bool IsBossLikeHitbox(float hitboxRadius) => hitboxRadius >= BossLikeHitboxRadius;

    public void SetHookState(string state)
    {
        this.hookState = state;
    }

    public void SetBossModMovementState(bool moveRequested, bool moveImminent)
    {
        this.bmrMoveRequested = moveRequested;
        this.bmrMoveImminent = moveImminent;
    }

    public void TryInjectGoal(object hints, ICollection<BossModGoalContribution> contributions)
    {
        _ = this.hookState;
        var now = DateTime.UtcNow;

        if (!config.Enabled || !config.ManageMovement || !config.AvoidStandingInsideEnemies)
        {
            this.lastReason = "disabled";
            this.insideCenterSince = DateTime.MinValue;
            this.lastOverlay = null;
            return;
        }

        if (!CombatEngagementDetector.IsEffectivelyInCombat(services) || services.Condition[ConditionFlag.Unconscious])
        {
            this.lastReason = "not active in combat";
            this.insideCenterSince = DateTime.MinValue;
            this.lastOverlay = null;
            return;
        }

        if (automatedMovementSuppressed())
        {
            this.lastReason = "manual movement suppression active";
            this.lastOverlay = null;
            return;
        }

        var player = services.ObjectTable.LocalPlayer;
        if (player == null || player.IsDead || player.CurrentHp == 0)
        {
            this.lastReason = "player unavailable";
            this.insideCenterSince = DateTime.MinValue;
            this.lastOverlay = null;
            return;
        }

        if (!currentTargetHasBossModule())
        {
            this.lastReason = "no boss module target";
            this.insideCenterSince = DateTime.MinValue;
            this.lastOverlay = null;
            return;
        }

        if (!this.EnsureResolved(hints.GetType()))
        {
            this.lastOverlay = null;
            return;
        }

        if (HasActiveLeyLinesGoal(contributions))
        {
            this.insideCenterSince = DateTime.MinValue;
            this.lastReason = "Ley Lines positioning active";
            this.lastOverlay = null;
            return;
        }

        if (HasHigherPriorityUtilityGoal(contributions))
        {
            this.insideCenterSince = DateTime.MinValue;
            this.lastReason = "defensive or utility positioning active";
            this.lastOverlay = null;
            return;
        }

        if (this.ShouldSuppressForBossModGoalZone(hints))
        {
            this.suppressComfortUntil = now.Add(PostMechanicCenterCooldown);
            this.insideCenterSince = DateTime.MinValue;
            this.lastReason = "BossMod goal zone active";
            this.lastOverlay = null;
            return;
        }

        if (this.BossModMechanicSafetyActive(hints))
        {
            this.suppressComfortUntil = now.Add(PostMechanicCenterCooldown);
            this.insideCenterSince = DateTime.MinValue;
            this.lastReason = "mechanic safety active";
            this.lastOverlay = null;
            return;
        }

        var pressure = mechanicPressure();
        if (pressure.DowntimeSoon || pressure.RaidwideOrDamageSoon || pressure.KnockbackSoon)
        {
            this.lastReason = pressure.FormatOptionalMovementHoldReason();
            this.lastOverlay = null;
            return;
        }

        if (now < this.suppressComfortUntil)
        {
            this.lastReason = "post-mechanic center cooldown";
            this.lastOverlay = null;
            return;
        }

        if (services.TargetManager.Target is not IBattleChara target ||
            target.IsDead ||
            target.CurrentHp == 0)
        {
            this.lastReason = "boss target unavailable";
            this.insideCenterSince = DateTime.MinValue;
            this.lastOverlay = null;
            return;
        }

        if (!IsBossLikeHitbox(target.HitboxRadius))
        {
            this.lastReason = "target hitbox not boss-like";
            this.insideCenterSince = DateTime.MinValue;
            this.lastOverlay = null;
            return;
        }

        var avoidanceRadius = AvoidanceRadius(target.HitboxRadius);
        var player2 = new Vector2(player.Position.X, player.Position.Z);
        var target2 = new Vector2(target.Position.X, target.Position.Z);
        if (Vector2.DistanceSquared(player2, target2) >= avoidanceRadius * avoidanceRadius)
        {
            this.lastReason = "outside boss center";
            this.insideCenterSince = DateTime.MinValue;
            this.lastOverlay = null;
            return;
        }

        if (this.insideCenterSince == DateTime.MinValue)
        {
            this.insideCenterSince = now;
            this.lastReason = "boss center dwell";
            this.lastOverlay = new(target.Position, avoidanceRadius, false, this.lastReason);
            return;
        }

        if (now - this.insideCenterSince < InsideCenterDwell)
        {
            this.lastReason = "boss center dwell";
            this.lastOverlay = new(target.Position, avoidanceRadius, false, this.lastReason);
            return;
        }

        if (this.lastGoalDelegate == null ||
            Vector2.DistanceSquared(this.lastTargetPosition, target2) > 1f ||
            MathF.Abs(this.lastAvoidanceRadius - avoidanceRadius) > 0.1f)
        {
            this.lastGoalDelegate = this.CreateGoalDelegate(target2, avoidanceRadius);
            this.lastTargetPosition = target2;
            this.lastAvoidanceRadius = avoidanceRadius;
        }

        contributions.Add(new(this.lastGoalDelegate!, BossModGoalPriority.Convenience, "Boss center avoidance"));
        this.lastReason = "avoiding boss center";
        this.lastOverlay = new(target.Position, avoidanceRadius, true, this.lastReason);
    }

    public void Reset()
    {
        this.hookState = "unresolved";
        this.lastReason = "reset";
        this.goalZonesField = null;
        this.forcedMovementField = null;
        this.forbiddenZonesField = null;
        this.recommendedPositionalField = null;
        this.wposXField = null;
        this.wposZField = null;
        this.resolvedHintsType = null;
        this.resolvedWPosType = null;
        this.lastGoalDelegate = null;
        this.lastTargetPosition = default;
        this.lastAvoidanceRadius = 0f;
        this.lastOverlay = null;
        this.suppressComfortUntil = DateTime.MinValue;
        this.insideCenterSince = DateTime.MinValue;
        this.bmrMoveRequested = false;
        this.bmrMoveImminent = false;
    }

    private bool EnsureResolved(Type hintsType)
    {
        if (this.resolvedHintsType == hintsType &&
            this.resolvedWPosType != null &&
            this.goalZonesField != null &&
            this.forcedMovementField != null &&
            this.forbiddenZonesField != null &&
            this.recommendedPositionalField != null &&
            this.wposXField != null &&
            this.wposZField != null)
        {
            return true;
        }

        var goalZones = hintsType.GetField("GoalZones", InstanceFlags);
        var forcedMovement = hintsType.GetField("ForcedMovement", InstanceFlags);
        var forbiddenZones = hintsType.GetField("ForbiddenZones", InstanceFlags);
        var recommendedPositional = hintsType.GetField("RecommendedPositional", InstanceFlags);
        var wposType = hintsType.Assembly.GetType("BossMod.WPos");
        var xField = wposType?.GetField("X", InstanceFlags);
        var zField = wposType?.GetField("Z", InstanceFlags);
        if (goalZones == null || forcedMovement == null || forbiddenZones == null || recommendedPositional == null || wposType == null || xField == null || zField == null)
        {
            this.lastReason = "BMR boss center goal reflection members unavailable";
            return false;
        }

        this.resolvedHintsType = hintsType;
        this.resolvedWPosType = wposType;
        this.goalZonesField = goalZones;
        this.forcedMovementField = forcedMovement;
        this.forbiddenZonesField = forbiddenZones;
        this.recommendedPositionalField = recommendedPositional;
        this.wposXField = xField;
        this.wposZField = zField;
        return true;
    }

    private bool ShouldSuppressForBossModGoalZone(object hints)
    {
        var goalZoneActive = this.goalZonesField?.GetValue(hints) is ICollection { Count: > 0 };
        return ShouldSuppressForBossModGoalZone(goalZoneActive, this.HasActiveRecommendedPositional(hints));
    }

    internal static bool ShouldSuppressForBossModGoalZone(bool goalZoneActive, bool recommendedPositionalActive)
    {
        return goalZoneActive && !recommendedPositionalActive;
    }

    internal static bool HasActiveLeyLinesGoal(IEnumerable<BossModGoalContribution> contributions)
    {
        foreach (var contribution in contributions)
        {
            if (contribution.ScoreMode == BossModGoalScoreMode.Advisory &&
                contribution.Priority >= BossModGoalPriority.Uptime &&
                contribution.Label.Equals(BlackMageLeyLinesPositioningController.GoalLabel, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    private bool HasActiveRecommendedPositional(object hints)
    {
        var positional = this.recommendedPositionalField?.GetValue(hints);
        if (positional == null)
        {
            return false;
        }

        return ReadBool(ReadTupleField(positional, "Imminent", "Item3")) &&
               !IsAnyPositional(ReadTupleField(positional, "Pos", "Item2"));
    }

    private bool BossModMechanicSafetyActive(object hints)
    {
        if (this.bmrMoveRequested || this.bmrMoveImminent)
        {
            return true;
        }

        if (this.forbiddenZonesField?.GetValue(hints) is ICollection { Count: > 0 })
        {
            return true;
        }

        return VectorLengthSquared(this.forcedMovementField?.GetValue(hints)) > 0.01f;
    }

    private Delegate CreateGoalDelegate(Vector2 targetPosition, float avoidanceRadius)
    {
        var parameter = System.Linq.Expressions.Expression.Parameter(this.resolvedWPosType!, "p");
        var call = System.Linq.Expressions.Expression.Call(
            typeof(BossCenterAvoidanceController).GetMethod(nameof(ScoreFromWPos), BindingFlags.Static | BindingFlags.NonPublic)!,
            System.Linq.Expressions.Expression.Convert(System.Linq.Expressions.Expression.Field(parameter, this.wposXField!), typeof(float)),
            System.Linq.Expressions.Expression.Convert(System.Linq.Expressions.Expression.Field(parameter, this.wposZField!), typeof(float)),
            System.Linq.Expressions.Expression.Constant(targetPosition.X),
            System.Linq.Expressions.Expression.Constant(targetPosition.Y),
            System.Linq.Expressions.Expression.Constant(avoidanceRadius));
        var delegateType = typeof(Func<,>).MakeGenericType(this.resolvedWPosType!, typeof(float));
        return System.Linq.Expressions.Expression.Lambda(delegateType, call, parameter).Compile();
    }

    private static float ScoreFromWPos(float x, float z, float targetX, float targetZ, float avoidanceRadius)
    {
        var dx = x - targetX;
        var dz = z - targetZ;
        var distance = MathF.Sqrt((dx * dx) + (dz * dz));
        if (distance <= avoidanceRadius)
        {
            return 0f;
        }

        var falloff = Math.Clamp((distance - avoidanceRadius) / CandidateExtraDistance, 0f, 1f);
        return GoalZoneScorePolicy.WeakPreference * falloff;
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
        var x = ReadFloat(type.GetField("X", InstanceFlags)?.GetValue(value));
        var y = ReadFloat(type.GetField("Y", InstanceFlags)?.GetValue(value));
        var z = ReadFloat(type.GetField("Z", InstanceFlags)?.GetValue(value));
        return x * x + y * y + z * z;
    }

    private static float ReadFloat(object? value)
    {
        return value switch
        {
            float f => f,
            double d => (float)d,
            _ => 0f
        };
    }

    private static bool ReadBool(object? value)
    {
        return value is bool b && b;
    }

    private static object? ReadTupleField(object? value, string namedField, string itemField)
    {
        if (value == null)
        {
            return null;
        }

        var type = value.GetType();
        return type.GetField(namedField, InstanceFlags)?.GetValue(value) ??
               type.GetField(itemField, InstanceFlags)?.GetValue(value);
    }

    private static bool IsAnyPositional(object? value)
    {
        return value == null ||
               value.ToString()?.Equals(Positional.Any.ToString(), StringComparison.Ordinal) == true;
    }

    private static bool HasHigherPriorityUtilityGoal(IEnumerable<BossModGoalContribution> contributions)
    {
        foreach (var contribution in contributions)
        {
            if (contribution.ScoreMode == BossModGoalScoreMode.Advisory &&
                contribution.Priority >= BossModGoalPriority.PartyUtility)
            {
                return true;
            }
        }

        return false;
    }
}

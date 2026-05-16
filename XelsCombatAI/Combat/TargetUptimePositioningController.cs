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

internal sealed class TargetUptimePositioningController(
    Configuration config,
    DalamudServices services,
    JobRangeProvider jobRangeProvider,
    Func<bool> automatedMovementSuppressed,
    Func<bool> currentTargetHasBossModule)
    : IBossModGoalZoneContributor
{
    private static readonly BindingFlags InstanceFlags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
    private const float BossLikeHitboxRadius = 4f;
    private const float CandidateMovementThreshold = 2f;
    private const float UptimeScore = GoalZoneScorePolicy.WeakPreference;

    private FieldInfo? goalZonesField;
    private FieldInfo? forcedMovementField;
    private FieldInfo? forbiddenZonesField;
    private FieldInfo? wposXField;
    private FieldInfo? wposZField;
    private Type? resolvedHintsType;
    private Type? resolvedWPosType;
    private Delegate? lastGoalDelegate;
    private Vector2 lastTargetPosition;
    private float lastTargetRadius;
    private float lastEngagementRange;
    private Vector2 lastCandidate;
    private ulong lastTargetId;
    private string hookState = "unresolved";
    private string lastReason = "not evaluated";
    private bool bmrMoveRequested;
    private bool bmrMoveImminent;

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

        if (!config.Enabled || !config.ManageMovement || !config.ManageTargetUptime)
        {
            this.lastReason = "disabled";
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

        var player = services.ObjectTable.LocalPlayer;
        if (player == null)
        {
            this.lastReason = "local player unavailable";
            return;
        }

        if (services.TargetManager.Target is not IBattleChara target ||
            target.IsDead ||
            target.CurrentHp == 0 ||
            !target.IsTargetable)
        {
            this.lastReason = "target unavailable";
            return;
        }

        if (!currentTargetHasBossModule() && target.HitboxRadius < BossLikeHitboxRadius)
        {
            this.lastReason = "not boss-like target uptime";
            return;
        }

        if (!this.EnsureResolved(hints.GetType()))
        {
            return;
        }

        if (this.goalZonesField!.GetValue(hints) is not IList)
        {
            this.lastReason = "BMR goal zone list unavailable";
            return;
        }

        var forcedMovementActive = VectorLengthSquared(this.forcedMovementField?.GetValue(hints)) > 0.01f;
        var forbiddenZonesActive = this.forbiddenZonesField?.GetValue(hints) is ICollection { Count: > 0 };
        if (!ShouldContributeDuringMechanic(
                forbiddenZonesActive,
                forcedMovementActive,
                this.bmrMoveRequested,
                this.bmrMoveImminent))
        {
            this.lastReason = forcedMovementActive
                ? "forced mechanic movement active"
                : forbiddenZonesActive
                ? "BMR movement inactive"
                : "no active mechanic exit";
            return;
        }

        var targetPosition = new Vector2(target.Position.X, target.Position.Z);
        var playerPosition = new Vector2(player.Position.X, player.Position.Z);
        var engagementRange = jobRangeProvider.EngagementRange;
        var candidate = ResolveCandidate(playerPosition, targetPosition, target.Rotation, target.HitboxRadius, engagementRange);
        if (this.lastGoalDelegate == null ||
            this.lastTargetId != target.GameObjectId ||
            Vector2.DistanceSquared(this.lastTargetPosition, targetPosition) > 1f ||
            MathF.Abs(this.lastTargetRadius - target.HitboxRadius) > 0.1f ||
            MathF.Abs(this.lastEngagementRange - engagementRange) > 0.1f ||
            Vector2.Distance(this.lastCandidate, candidate) > CandidateMovementThreshold)
        {
            this.lastGoalDelegate = this.CreateGoalDelegate(targetPosition, target.HitboxRadius, engagementRange);
            this.lastTargetId = target.GameObjectId;
            this.lastTargetPosition = targetPosition;
            this.lastTargetRadius = target.HitboxRadius;
            this.lastEngagementRange = engagementRange;
            this.lastCandidate = candidate;
        }

        contributions.Add(new(this.lastGoalDelegate, BossModGoalPriority.Uptime, "Target uptime", candidate, MechanicWhisperConfidence.Confident));
        this.lastReason = "mechanic exit target uptime";
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
        this.lastGoalDelegate = null;
        this.lastTargetPosition = default;
        this.lastTargetRadius = 0f;
        this.lastEngagementRange = 0f;
        this.lastCandidate = default;
        this.lastTargetId = 0;
        this.lastReason = "reset";
        this.bmrMoveRequested = false;
        this.bmrMoveImminent = false;
    }

    internal static bool ShouldContributeDuringMechanic(bool forbiddenZonesActive, bool forcedMovementActive, bool bmrMoveRequested, bool bmrMoveImminent)
    {
        return forbiddenZonesActive &&
               !forcedMovementActive &&
               (bmrMoveRequested || bmrMoveImminent);
    }

    internal static Vector2 ResolveCandidate(Vector2 playerPosition, Vector2 targetPosition, float targetRotation, float targetRadius, float engagementRange)
    {
        var awayFromTarget = playerPosition - targetPosition;
        if (awayFromTarget.LengthSquared() <= 0.01f)
        {
            var fallback = -Geometry.RotationToDirection(targetRotation);
            awayFromTarget = new Vector2(fallback.X, fallback.Z);
        }

        if (awayFromTarget.LengthSquared() <= 0.01f)
        {
            awayFromTarget = Vector2.UnitX;
        }

        awayFromTarget = Vector2.Normalize(awayFromTarget);
        var preferredSurfaceDistance = MathF.Max(0.5f, engagementRange - 0.5f);
        return targetPosition + awayFromTarget * (targetRadius + preferredSurfaceDistance);
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
            this.lastReason = "BMR target uptime reflection members unavailable";
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

    private Delegate CreateGoalDelegate(Vector2 targetPosition, float targetRadius, float engagementRange)
    {
        var parameter = Expression.Parameter(this.resolvedWPosType!, "p");
        var call = Expression.Call(
            Expression.Constant(new TargetUptimeGoalPlan(targetPosition, targetRadius, engagementRange)),
            TargetUptimeGoalPlan.ScoreFromWPosMethod,
            Expression.Convert(Expression.Field(parameter, this.wposXField!), typeof(float)),
            Expression.Convert(Expression.Field(parameter, this.wposZField!), typeof(float)));
        var delegateType = typeof(Func<,>).MakeGenericType(this.resolvedWPosType!, typeof(float));
        return Expression.Lambda(delegateType, call, parameter).Compile();
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

    private sealed class TargetUptimeGoalPlan(Vector2 targetPosition, float targetRadius, float engagementRange)
    {
        public static readonly MethodInfo ScoreFromWPosMethod =
            typeof(TargetUptimeGoalPlan).GetMethod(nameof(ScoreFromWPos), BindingFlags.Instance | BindingFlags.NonPublic)!;

        private float ScoreFromWPos(float x, float z)
        {
            var position = new Vector2(x, z);
            var surfaceDistance = Vector2.Distance(position, targetPosition) - targetRadius;
            return surfaceDistance >= 0f && surfaceDistance <= engagementRange
                ? UptimeScore
                : 0f;
        }
    }
}

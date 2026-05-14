using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq.Expressions;
using System.Numerics;
using System.Reflection;
using Dalamud.Game.ClientState.Conditions;

namespace XelsCombatAI.Combat;

internal sealed class ArenaEdgePositioningController(Configuration config, DalamudServices services)
    : IBossModGoalZoneContributor, IMovementCandidateSource
{
    private const float EdgeBand = 0.75f;
    private const float ActivationDistance = 0.85f;
    private const float NudgeDistance = 0.9f;
    private const float AcceptanceRadius = 0.75f;
    private static readonly TimeSpan GoalLinger = TimeSpan.FromMilliseconds(500);
    private static readonly TimeSpan PostMechanicEdgeCooldown = TimeSpan.FromSeconds(2);
    private static readonly BindingFlags InstanceFlags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
    private static readonly MethodInfo ScoreCircleMethod = typeof(ArenaEdgePositioningController).GetMethod(nameof(ScoreCircle), BindingFlags.Static | BindingFlags.NonPublic)!;
    private static readonly MethodInfo ScoreRectMethod = typeof(ArenaEdgePositioningController).GetMethod(nameof(ScoreRect), BindingFlags.Static | BindingFlags.NonPublic)!;

    private FieldInfo? centerField;
    private FieldInfo? boundsField;
    private FieldInfo? wposXField;
    private FieldInfo? wposZField;
    private FieldInfo? radiusField;
    private FieldInfo? forcedMovementField;
    private FieldInfo? forbiddenZonesField;
    private Type? resolvedHintsType;
    private Delegate? lastGoalDelegate;
    private object? lastBounds;
    private float lastCenterX;
    private float lastCenterZ;
    private string hookState = "unresolved";
    private string lastReason = "not evaluated";
    private DateTime goalLingerUntil = DateTime.MinValue;
    private DateTime suppressComfortUntil = DateTime.MinValue;
    private bool bossModEncounterActive;
    private object? lastObservedBounds;
    private bool hasLastObservedBounds;
    private float lastObservedCenterX;
    private float lastObservedCenterZ;
    private float lastObservedRadius;

    public string LastReason => this.lastReason;

    public void AddMovementCandidates(MovementPlannerContext context, ICollection<MovementCandidate> candidates)
    {
        if (!string.Equals(this.lastReason, "near arena edge", StringComparison.Ordinal) || context.PathfindMapCenter == null)
        {
            return;
        }

        var towardCenter = context.PathfindMapCenter.Value - context.PlayerPosition;
        towardCenter.Y = 0f;
        if (towardCenter.LengthSquared() <= 0.01f)
        {
            return;
        }

        towardCenter = Vector3.Normalize(towardCenter);
        candidates.Add(new(
            "Arena edge",
            this.lastReason,
            context.PlayerPosition + towardCenter * NudgeDistance,
            AcceptanceRadius,
            MovementCandidatePriority.Comfort,
            1f,
            0f,
            0f,
            0.45f));
    }

    public void SetHookState(string state)
    {
        this.hookState = state;
    }

    public void SetBossModEncounterState(bool activeModule)
    {
        this.bossModEncounterActive = activeModule;
    }

    public void TryInjectGoal(object hints, ICollection<BossModGoalContribution> contributions)
    {
        _ = this.hookState;
        if (!config.Enabled || !config.ManageMovement || !config.AvoidArenaEdge)
        {
            this.lastReason = "disabled";
            return;
        }

        if (!CombatEngagementDetector.IsEffectivelyInCombat(services) || services.Condition[ConditionFlag.Unconscious])
        {
            this.lastReason = "not active in combat";
            return;
        }

        var player = services.ObjectTable.LocalPlayer;
        if (player == null || player.IsDead || player.CurrentHp == 0)
        {
            this.lastReason = "player unavailable";
            return;
        }

        if (!this.bossModEncounterActive && !this.HasBossLikeCurrentTarget())
        {
            this.lastReason = "not boss context";
            return;
        }

        if (!this.EnsureResolved(hints.GetType()))
        {
            this.lastReason = "BMR arena edge reflection members unavailable";
            return;
        }

        var center = this.centerField!.GetValue(hints);
        var bounds = this.boundsField!.GetValue(hints);
        if (center == null || bounds == null)
        {
            this.lastReason = "BMR arena bounds unavailable";
            return;
        }

        var centerX = ReadFloat(this.wposXField!.GetValue(center));
        var centerZ = ReadFloat(this.wposZField!.GetValue(center));
        var radius = ReadFloat(this.radiusField!.GetValue(bounds));
        var edgeDistance = this.GetEdgeDistance(bounds, centerX, centerZ, player.Position);
        var now = DateTime.UtcNow;
        if (this.BoundsShapeChanged(centerX, centerZ, radius))
        {
            this.suppressComfortUntil = now.Add(PostMechanicEdgeCooldown);
        }

        this.RecordObservedBounds(bounds, centerX, centerZ, radius);

        if (this.BossModMechanicSafetyActive(hints))
        {
            this.suppressComfortUntil = now.Add(PostMechanicEdgeCooldown);
            this.lastReason = "mechanic safety active";
            return;
        }

        if (now < this.suppressComfortUntil)
        {
            this.lastReason = "post-mechanic edge cooldown";
            return;
        }

        if (edgeDistance <= ActivationDistance)
        {
            this.goalLingerUntil = now.Add(GoalLinger);
        }

        if (edgeDistance > ActivationDistance && now > this.goalLingerUntil)
        {
            this.lastReason = "away from arena edge";
            return;
        }

        if (this.lastGoalDelegate == null ||
            !ReferenceEquals(this.lastBounds, bounds) ||
            MathF.Abs(this.lastCenterX - centerX) > 0.1f ||
            MathF.Abs(this.lastCenterZ - centerZ) > 0.1f)
        {
            if (!this.TryCreateGoalDelegate(bounds, centerX, centerZ, out this.lastGoalDelegate))
            {
                this.lastReason = "arena bounds too small";
                return;
            }

            this.lastBounds = bounds;
            this.lastCenterX = centerX;
            this.lastCenterZ = centerZ;
        }

        if (this.lastGoalDelegate != null)
        {
            contributions.Add(new(this.lastGoalDelegate, BossModGoalPriority.Convenience, "Arena edge avoidance"));
            this.lastReason = "near arena edge";
        }
    }

    public void Reset()
    {
        this.centerField = null;
        this.boundsField = null;
        this.wposXField = null;
        this.wposZField = null;
        this.radiusField = null;
        this.forcedMovementField = null;
        this.forbiddenZonesField = null;
        this.resolvedHintsType = null;
        this.lastGoalDelegate = null;
        this.lastBounds = null;
        this.lastCenterX = 0f;
        this.lastCenterZ = 0f;
        this.lastReason = "reset";
        this.goalLingerUntil = DateTime.MinValue;
        this.suppressComfortUntil = DateTime.MinValue;
        this.bossModEncounterActive = false;
        this.lastObservedBounds = null;
        this.hasLastObservedBounds = false;
        this.lastObservedCenterX = 0f;
        this.lastObservedCenterZ = 0f;
        this.lastObservedRadius = 0f;
    }

    public bool TryGetObservedEdgeDistance(Vector3 position, out float edgeDistance)
    {
        if (!this.hasLastObservedBounds || this.lastObservedBounds == null)
        {
            edgeDistance = 0f;
            return false;
        }

        edgeDistance = this.GetEdgeDistance(this.lastObservedBounds, this.lastObservedCenterX, this.lastObservedCenterZ, position);
        return true;
    }

    private bool EnsureResolved(Type hintsType)
    {
        if (this.resolvedHintsType == hintsType &&
            this.centerField != null &&
            this.boundsField != null &&
            this.wposXField != null &&
            this.wposZField != null &&
            this.radiusField != null &&
            this.forcedMovementField != null &&
            this.forbiddenZonesField != null)
        {
            return true;
        }

        var center = hintsType.GetField("PathfindMapCenter", InstanceFlags);
        var bounds = hintsType.GetField("PathfindMapBounds", InstanceFlags);
        var wposType = center?.FieldType;
        var xField = wposType?.GetField("X", InstanceFlags);
        var zField = wposType?.GetField("Z", InstanceFlags);
        var radius = bounds?.FieldType.GetField("Radius", InstanceFlags);
        var forcedMovement = hintsType.GetField("ForcedMovement", InstanceFlags);
        var forbiddenZones = hintsType.GetField("ForbiddenZones", InstanceFlags);
        if (center == null || bounds == null || xField == null || zField == null || radius == null || forcedMovement == null || forbiddenZones == null)
        {
            return false;
        }

        this.centerField = center;
        this.boundsField = bounds;
        this.wposXField = xField;
        this.wposZField = zField;
        this.radiusField = radius;
        this.forcedMovementField = forcedMovement;
        this.forbiddenZonesField = forbiddenZones;
        this.resolvedHintsType = hintsType;
        this.lastGoalDelegate = null;
        this.lastBounds = null;
        return true;
    }

    private bool BossModMechanicSafetyActive(object hints)
    {
        if (this.forbiddenZonesField?.GetValue(hints) is ICollection { Count: > 0 })
        {
            return true;
        }

        return VectorLengthSquared(this.forcedMovementField?.GetValue(hints)) > 0.01f;
    }

    private bool BoundsShapeChanged(float centerX, float centerZ, float radius)
    {
        return this.hasLastObservedBounds &&
               (MathF.Abs(this.lastObservedCenterX - centerX) > 0.25f ||
                MathF.Abs(this.lastObservedCenterZ - centerZ) > 0.25f ||
                MathF.Abs(this.lastObservedRadius - radius) > 0.25f);
    }

    private void RecordObservedBounds(object bounds, float centerX, float centerZ, float radius)
    {
        this.lastObservedBounds = bounds;
        this.hasLastObservedBounds = true;
        this.lastObservedCenterX = centerX;
        this.lastObservedCenterZ = centerZ;
        this.lastObservedRadius = radius;
    }

    private bool TryCreateGoalDelegate(object bounds, float centerX, float centerZ, out Delegate? goal)
    {
        goal = null;
        var radius = ReadFloat(this.radiusField!.GetValue(bounds));
        if (radius <= EdgeBand + 1f)
        {
            return false;
        }

        var boundsType = bounds.GetType();
        var wposType = this.wposXField!.DeclaringType!;
        var parameter = Expression.Parameter(wposType, "p");
        var x = Expression.Convert(Expression.Field(parameter, this.wposXField), typeof(float));
        var z = Expression.Convert(Expression.Field(parameter, this.wposZField!), typeof(float));

        var halfWidthField = boundsType.GetField("HalfWidth", InstanceFlags);
        var halfHeightField = boundsType.GetField("HalfHeight", InstanceFlags);
        var orientationField = boundsType.GetField("Orientation", InstanceFlags);
        var orientation = orientationField?.GetValue(bounds);
        var orientationXField = orientation?.GetType().GetField("X", InstanceFlags);
        var orientationZField = orientation?.GetType().GetField("Z", InstanceFlags);

        Expression call;
        if (halfWidthField != null && halfHeightField != null && orientation != null && orientationXField != null && orientationZField != null)
        {
            var halfWidth = ReadFloat(halfWidthField.GetValue(bounds));
            var halfHeight = ReadFloat(halfHeightField.GetValue(bounds));
            var orientationX = ReadFloat(orientationXField.GetValue(orientation));
            var orientationZ = ReadFloat(orientationZField.GetValue(orientation));
            call = Expression.Call(
                ScoreRectMethod,
                x,
                z,
                Expression.Constant(centerX),
                Expression.Constant(centerZ),
                Expression.Constant(halfWidth),
                Expression.Constant(halfHeight),
                Expression.Constant(orientationX),
                Expression.Constant(orientationZ));
        }
        else
        {
            call = Expression.Call(
                ScoreCircleMethod,
                x,
                z,
                Expression.Constant(centerX),
                Expression.Constant(centerZ),
                Expression.Constant(radius));
        }

        var delegateType = typeof(Func<,>).MakeGenericType(wposType, typeof(float));
        goal = Expression.Lambda(delegateType, call, parameter).Compile();
        return true;
    }

    private bool HasBossLikeCurrentTarget()
    {
        return services.TargetManager.Target is { HitboxRadius: >= 4f };
    }

    private float GetEdgeDistance(object bounds, float centerX, float centerZ, Vector3 playerPosition)
    {
        var boundsType = bounds.GetType();
        var halfWidthField = boundsType.GetField("HalfWidth", InstanceFlags);
        var halfHeightField = boundsType.GetField("HalfHeight", InstanceFlags);
        var orientationField = boundsType.GetField("Orientation", InstanceFlags);
        var orientation = orientationField?.GetValue(bounds);
        var orientationXField = orientation?.GetType().GetField("X", InstanceFlags);
        var orientationZField = orientation?.GetType().GetField("Z", InstanceFlags);
        if (halfWidthField != null && halfHeightField != null && orientation != null && orientationXField != null && orientationZField != null)
        {
            var halfWidth = ReadFloat(halfWidthField.GetValue(bounds));
            var halfHeight = ReadFloat(halfHeightField.GetValue(bounds));
            var orientationX = ReadFloat(orientationXField.GetValue(orientation));
            var orientationZ = ReadFloat(orientationZField.GetValue(orientation));
            return EdgeDistanceRect(playerPosition.X, playerPosition.Z, centerX, centerZ, halfWidth, halfHeight, orientationX, orientationZ);
        }

        var radius = ReadFloat(this.radiusField!.GetValue(bounds));
        return EdgeDistanceCircle(playerPosition.X, playerPosition.Z, centerX, centerZ, radius);
    }

    private static float ScoreCircle(float x, float z, float centerX, float centerZ, float radius)
    {
        return ScoreEdgeDistance(EdgeDistanceCircle(x, z, centerX, centerZ, radius));
    }

    private static float ScoreRect(float x, float z, float centerX, float centerZ, float halfWidth, float halfHeight, float orientationX, float orientationZ)
    {
        return ScoreEdgeDistance(EdgeDistanceRect(x, z, centerX, centerZ, halfWidth, halfHeight, orientationX, orientationZ));
    }

    private static float EdgeDistanceCircle(float x, float z, float centerX, float centerZ, float radius)
    {
        var dx = x - centerX;
        var dz = z - centerZ;
        return radius - MathF.Sqrt(dx * dx + dz * dz);
    }

    private static float EdgeDistanceRect(float x, float z, float centerX, float centerZ, float halfWidth, float halfHeight, float orientationX, float orientationZ)
    {
        var dx = x - centerX;
        var dz = z - centerZ;
        var parallel = dx * orientationX + dz * orientationZ;
        var ortho = dx * orientationZ - dz * orientationX;
        return MathF.Min(halfHeight - MathF.Abs(parallel), halfWidth - MathF.Abs(ortho));
    }

    private static float ScoreEdgeDistance(float edgeDistance)
    {
        if (edgeDistance <= 0f)
        {
            return 0f;
        }

        if (edgeDistance >= EdgeBand)
        {
            return GoalZoneScorePolicy.WeakPreference;
        }

        return GoalZoneScorePolicy.WeakPreference * edgeDistance / EdgeBand;
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
}

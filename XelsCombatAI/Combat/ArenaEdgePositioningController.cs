using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq.Expressions;
using System.Numerics;
using System.Reflection;
using System.Linq;
using Dalamud.Game.ClientState.Conditions;

namespace XelsCombatAI.Combat;

internal sealed class ArenaEdgePositioningController(Configuration config, DalamudServices services)
    : IBossModGoalZoneContributor
{
    private const float EdgeBand = 2.5f;
    private const float ActivationDistance = 2.25f;
    private static readonly TimeSpan GoalLinger = TimeSpan.FromMilliseconds(1500);
    private static readonly TimeSpan PostMechanicEdgeCooldown = TimeSpan.FromSeconds(2);
    private static readonly BindingFlags InstanceFlags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
    private static readonly MethodInfo ScoreCircleMethod = typeof(ArenaEdgePositioningController).GetMethod(nameof(ScoreCircle), BindingFlags.Static | BindingFlags.NonPublic)!;
    private static readonly MethodInfo ScoreRectMethod = typeof(ArenaEdgePositioningController).GetMethod(nameof(ScoreRect), BindingFlags.Static | BindingFlags.NonPublic)!;
    private static readonly Vector2[] BoundaryProbeDirections =
    [
        Vector2.UnitX,
        -Vector2.UnitX,
        Vector2.UnitY,
        -Vector2.UnitY,
        Vector2.Normalize(new Vector2(1f, 1f)),
        Vector2.Normalize(new Vector2(1f, -1f)),
        Vector2.Normalize(new Vector2(-1f, 1f)),
        Vector2.Normalize(new Vector2(-1f, -1f))
    ];

    private FieldInfo? centerField;
    private FieldInfo? boundsField;
    private FieldInfo? wposXField;
    private FieldInfo? wposZField;
    private FieldInfo? radiusField;
    private FieldInfo? forcedMovementField;
    private FieldInfo? forbiddenZonesField;
    private FieldInfo? pathfindMapCenterField;
    private FieldInfo? pathfindMapBoundsField;
    private ConstructorInfo? wdirConstructor;
    private MethodInfo? boundsContainsMethod;
    private Type? resolvedHintsType;
    private Delegate? lastGoalDelegate;
    private Delegate? lastRecoveryGoalDelegate;
    private object? lastBounds;
    private object? lastRecoveryBounds;
    private float lastCenterX;
    private float lastCenterZ;
    private Vector2 lastRecoveryPoint;
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
            if (!this.lastReason.StartsWith("BMR arena edge reflection members unavailable", StringComparison.Ordinal))
            {
                this.lastReason = "BMR arena edge reflection members unavailable";
            }

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
        var pathfindBounds = this.CreatePathfindBoundsSnapshot(hints);
        var customRecoveryActive = this.TryFindCustomBoundsRecoveryPoint(pathfindBounds, bounds, centerX, centerZ, player.Position, out var recoveryPoint);
        var edgeDistance = this.GetEdgeDistance(bounds, centerX, centerZ, player.Position);
        var nearEdge = customRecoveryActive || edgeDistance <= ActivationDistance;
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

        if (nearEdge)
        {
            this.goalLingerUntil = now.Add(GoalLinger);
        }

        if (now < this.suppressComfortUntil && !nearEdge)
        {
            this.lastReason = "post-mechanic edge cooldown";
            return;
        }

        if (!nearEdge && now > this.goalLingerUntil)
        {
            this.lastReason = "away from arena edge";
            return;
        }

        if (customRecoveryActive)
        {
            if (this.lastRecoveryGoalDelegate == null ||
                !ReferenceEquals(this.lastRecoveryBounds, bounds) ||
                Vector2.DistanceSquared(this.lastRecoveryPoint, recoveryPoint) > 0.25f)
            {
                this.lastRecoveryGoalDelegate = this.CreateRecoveryGoalDelegate(recoveryPoint);
                this.lastRecoveryBounds = bounds;
                this.lastRecoveryPoint = recoveryPoint;
            }

            contributions.Add(new(this.lastRecoveryGoalDelegate, BossModGoalPriority.Convenience, "Arena edge recovery", recoveryPoint));
            this.lastReason = "custom arena edge recovery";
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
            contributions.Add(new(this.lastGoalDelegate, BossModGoalPriority.Convenience, "Arena edge avoidance", new Vector2(centerX, centerZ)));
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
        this.pathfindMapCenterField = null;
        this.pathfindMapBoundsField = null;
        this.wdirConstructor = null;
        this.boundsContainsMethod = null;
        this.resolvedHintsType = null;
        this.lastGoalDelegate = null;
        this.lastRecoveryGoalDelegate = null;
        this.lastBounds = null;
        this.lastRecoveryBounds = null;
        this.lastCenterX = 0f;
        this.lastCenterZ = 0f;
        this.lastRecoveryPoint = default;
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
            this.forbiddenZonesField != null &&
            this.pathfindMapCenterField != null &&
            this.pathfindMapBoundsField != null)
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
        var wdirType = hintsType.Assembly.GetType("BossMod.WDir");
        var wdirConstructor = wdirType?.GetConstructor([typeof(float), typeof(float)]);
        var boundsContainsMethod = wdirType == null
            ? null
            : bounds?.FieldType.GetMethods(InstanceFlags)
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
        if (center == null || bounds == null || xField == null || zField == null || radius == null || forcedMovement == null || forbiddenZones == null)
        {
            this.lastReason = $"BMR arena edge reflection members unavailable: {FormatMissing(
                (center == null, "AIHints.PathfindMapCenter"),
                (bounds == null, "AIHints.PathfindMapBounds"),
                (xField == null, "BossMod.WPos.X"),
                (zField == null, "BossMod.WPos.Z"),
                (radius == null, "ArenaBounds.Radius"),
                (forcedMovement == null, "AIHints.ForcedMovement"),
                (forbiddenZones == null, "AIHints.ForbiddenZones"))}";
            return false;
        }

        this.centerField = center;
        this.boundsField = bounds;
        this.wposXField = xField;
        this.wposZField = zField;
        this.radiusField = radius;
        this.forcedMovementField = forcedMovement;
        this.forbiddenZonesField = forbiddenZones;
        this.pathfindMapCenterField = center;
        this.pathfindMapBoundsField = bounds;
        this.wdirConstructor = wdirConstructor;
        this.boundsContainsMethod = boundsContainsMethod;
        this.resolvedHintsType = hintsType;
        this.lastGoalDelegate = null;
        this.lastRecoveryGoalDelegate = null;
        this.lastBounds = null;
        this.lastRecoveryBounds = null;
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

    private bool TryFindCustomBoundsRecoveryPoint(
        BossModPathfindBoundsSnapshot? bounds,
        object rawBounds,
        float centerX,
        float centerZ,
        Vector3 playerPosition,
        out Vector2 recoveryPoint)
    {
        recoveryPoint = default;
        if (bounds == null || !IsCustomBounds(rawBounds))
        {
            return false;
        }

        var player = new Vector2(playerPosition.X, playerPosition.Z);
        var center = new Vector2(centerX, centerZ);
        if (!IsNearCustomBoundary(bounds, player, center))
        {
            return false;
        }

        var towardCenter = center - player;
        if (towardCenter.LengthSquared() <= 0.01f)
        {
            return false;
        }

        towardCenter = Vector2.Normalize(towardCenter);
        Vector2? firstInside = null;
        for (var distance = 1f; distance <= 10f; distance += 0.5f)
        {
            var candidate = player + towardCenter * distance;
            if (!bounds.Contains(candidate))
            {
                continue;
            }

            firstInside ??= candidate;
            if (IsInsideCustomBoundsWithMargin(bounds, candidate, EdgeBand))
            {
                recoveryPoint = candidate;
                return true;
            }
        }

        if (firstInside.HasValue)
        {
            recoveryPoint = firstInside.Value;
            return true;
        }

        if (bounds.Contains(center))
        {
            recoveryPoint = center;
            return true;
        }

        return false;
    }

    private Delegate CreateRecoveryGoalDelegate(Vector2 point)
    {
        var wposType = this.wposXField!.DeclaringType!;
        var parameter = Expression.Parameter(wposType, "p");
        var x = Expression.Convert(Expression.Field(parameter, this.wposXField), typeof(float));
        var z = Expression.Convert(Expression.Field(parameter, this.wposZField!), typeof(float));
        var dx = Expression.Subtract(x, Expression.Constant(point.X));
        var dz = Expression.Subtract(z, Expression.Constant(point.Y));
        var distSq = Expression.Add(Expression.Multiply(dx, dx), Expression.Multiply(dz, dz));
        const float RecoveryRadius = 2.5f;
        var score = Expression.Condition(
            Expression.LessThanOrEqual(distSq, Expression.Constant(RecoveryRadius * RecoveryRadius)),
            Expression.Constant(GoalZoneScorePolicy.WeakPreference),
            Expression.Constant(0f));
        var delegateType = typeof(Func<,>).MakeGenericType(wposType, typeof(float));
        return Expression.Lambda(delegateType, score, parameter).Compile();
    }

    private static bool IsCustomBounds(object bounds)
    {
        var type = bounds.GetType();
        return type.Name.Contains("Custom", StringComparison.Ordinal) ||
               type.GetField("Vertices", InstanceFlags) != null;
    }

    private static bool IsNearCustomBoundary(BossModPathfindBoundsSnapshot bounds, Vector2 point, Vector2 center)
    {
        if (!bounds.Contains(point))
        {
            return true;
        }

        var awayFromCenter = point - center;
        if (awayFromCenter.LengthSquared() > 0.01f)
        {
            awayFromCenter = Vector2.Normalize(awayFromCenter);
            if (!bounds.Contains(point + awayFromCenter * ActivationDistance))
            {
                return true;
            }
        }

        foreach (var direction in BoundaryProbeDirections)
        {
            if (!bounds.Contains(point + direction * ActivationDistance))
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsInsideCustomBoundsWithMargin(BossModPathfindBoundsSnapshot bounds, Vector2 point, float margin)
    {
        if (!bounds.Contains(point))
        {
            return false;
        }

        foreach (var direction in BoundaryProbeDirections)
        {
            if (!bounds.Contains(point + direction * margin))
            {
                return false;
            }
        }

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

using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;
using System.Numerics;
using System.Reflection;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Game.ClientState.Objects.Enums;
using Dalamud.Game.ClientState.Objects.Types;
using XelsCombatAI.Game;
using XelsCombatAI.Integrations;

namespace XelsCombatAI.Combat;

internal sealed class SocialSpacingPositioningController(
    Configuration config,
    DalamudServices services,
    BossModReflectionSafety bossModSafety,
    Func<bool> automatedMovementSuppressed)
    : IBossModGoalZoneContributor
{
    private const float ActivationDistance = 0.65f;
    private const float MinimumPersonalSpace = 0.35f;
    private const float PreferredPlayerSpacing = 1.15f;
    private const float NearbyPlayerConsiderationDistance = 3f;
    private const float LocalFalloffStartDistance = 1.25f;
    private const float MaxComfortMoveDistance = 2.25f;
    private static readonly BindingFlags InstanceFlags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

    private FieldInfo? goalZonesField;
    private FieldInfo? forcedMovementField;
    private FieldInfo? forbiddenZonesField;
    private FieldInfo? wposXField;
    private FieldInfo? wposZField;
    private Type? resolvedHintsType;
    private Type? resolvedWPosType;
    private Delegate? goalDelegate;
    private Vector2 playerPosition;
    private Vector2 personalBiasDirection = Vector2.UnitX;
    private Vector2[] avoidancePoints = [];
    private string hookState = "unresolved";
    private string lastReason = "not evaluated";
    private bool bmrMoveRequested;
    private bool bmrMoveImminent;

    public string LastReason => this.lastReason;

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
        if (!config.Enabled || !config.ManageMovement || !config.ManageSocialSpacing)
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
        if (player == null || player.IsDead || player.CurrentHp == 0)
        {
            this.lastReason = "player unavailable";
            return;
        }

        if (!this.EnsureResolved(hints.GetType()))
        {
            return;
        }

        if (this.ShouldSuppressForMechanicSafety(hints, player, out var mechanicReason))
        {
            this.lastReason = mechanicReason;
            return;
        }

        var player2 = new Vector2(player.Position.X, player.Position.Z);
        var visiblePlayers = PartyAllyProvider.EnumerateVisiblePartyAllies(services, player)
            .Where(ally => ally.ObjectKind == ObjectKind.Pc)
            .Select(ally => new Vector2(ally.Position.X, ally.Position.Z))
            .Where(position => Vector2.Distance(position, player2) <= NearbyPlayerConsiderationDistance)
            .ToArray();
        if (visiblePlayers.Length == 0)
        {
            this.lastReason = "no nearby visible players";
            return;
        }

        if (!visiblePlayers.Any(position => Vector2.Distance(position, player2) <= ActivationDistance))
        {
            this.lastReason = "not exactly stacked";
            return;
        }

        this.playerPosition = player2;
        this.personalBiasDirection = ResolvePersonalBiasDirection(player.GameObjectId);
        this.avoidancePoints = visiblePlayers;
        this.goalDelegate ??= this.CreateGoalDelegate();

        contributions.Add(new(this.goalDelegate, BossModGoalPriority.Convenience, "Social spacing"));
        this.lastReason = "avoiding exact player stack";
    }

    public void Reset()
    {
        this.hookState = "unresolved";
        this.lastReason = "reset";
        this.goalZonesField = null;
        this.forcedMovementField = null;
        this.forbiddenZonesField = null;
        this.wposXField = null;
        this.wposZField = null;
        this.resolvedHintsType = null;
        this.resolvedWPosType = null;
        this.goalDelegate = null;
        this.playerPosition = default;
        this.personalBiasDirection = Vector2.UnitX;
        this.avoidancePoints = [];
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
            this.lastReason = "BMR social spacing reflection members unavailable";
            return false;
        }

        this.resolvedHintsType = hintsType;
        this.resolvedWPosType = wposType;
        this.goalZonesField = goalZones;
        this.forcedMovementField = forcedMovement;
        this.forbiddenZonesField = forbiddenZones;
        this.wposXField = xField;
        this.wposZField = zField;
        this.goalDelegate = null;
        return true;
    }

    private bool ShouldSuppressForMechanicSafety(object hints, IBattleChara player, out string reason)
    {
        if (this.bmrMoveRequested || this.bmrMoveImminent)
        {
            reason = "BossMod safety movement active";
            return true;
        }

        if (VectorLengthSquared(this.forcedMovementField?.GetValue(hints)) > 0.01f)
        {
            reason = "forced mechanic movement active";
            return true;
        }

        var mechanicHintsActive =
            this.goalZonesField?.GetValue(hints) is ICollection { Count: > 0 } ||
            this.forbiddenZonesField?.GetValue(hints) is ICollection { Count: > 0 };
        if (!mechanicHintsActive)
        {
            reason = string.Empty;
            return false;
        }

        if (!bossModSafety.TryIsPositionSafe(player.Position, out var currentSafe, out var safetyReason))
        {
            reason = $"mechanic safety unknown: {safetyReason}";
            return true;
        }

        if (!currentSafe)
        {
            reason = "current position unsafe";
            return true;
        }

        reason = string.Empty;
        return false;
    }

    private Delegate CreateGoalDelegate()
    {
        var parameter = Expression.Parameter(this.resolvedWPosType!, "p");
        var call = Expression.Call(
            Expression.Constant(this),
            typeof(SocialSpacingPositioningController).GetMethod(nameof(ScoreFromWPos), BindingFlags.Instance | BindingFlags.NonPublic)!,
            Expression.Convert(Expression.Field(parameter, this.wposXField!), typeof(float)),
            Expression.Convert(Expression.Field(parameter, this.wposZField!), typeof(float)));
        var delegateType = typeof(Func<,>).MakeGenericType(this.resolvedWPosType!, typeof(float));
        return Expression.Lambda(delegateType, call, parameter).Compile();
    }

    private float ScoreFromWPos(float x, float z)
    {
        var candidate = new Vector2(x, z);
        var localDistance = Vector2.Distance(candidate, this.playerPosition);
        if (localDistance >= MaxComfortMoveDistance)
        {
            return 0f;
        }

        var minPlayerDistance = float.MaxValue;
        foreach (var point in this.avoidancePoints)
        {
            minPlayerDistance = Math.Min(minPlayerDistance, Vector2.Distance(candidate, point));
        }

        if (minPlayerDistance <= MinimumPersonalSpace)
        {
            return 0f;
        }

        var spacingScore = Math.Clamp(
            (minPlayerDistance - MinimumPersonalSpace) / (PreferredPlayerSpacing - MinimumPersonalSpace),
            0f,
            1f);
        var localScore = localDistance <= LocalFalloffStartDistance
            ? 1f
            : 1f - Math.Clamp((localDistance - LocalFalloffStartDistance) / (MaxComfortMoveDistance - LocalFalloffStartDistance), 0f, 1f);
        var biasScore = this.CalculatePersonalBias(candidate);
        return GoalZoneScorePolicy.WeakPreference * spacingScore * localScore * biasScore;
    }

    private float CalculatePersonalBias(Vector2 candidate)
    {
        var offset = candidate - this.playerPosition;
        if (offset.LengthSquared() <= 0.0001f)
        {
            return 0.85f;
        }

        var dot = Vector2.Dot(Vector2.Normalize(offset), this.personalBiasDirection);
        return 0.85f + (0.15f * Math.Clamp(dot, -1f, 1f));
    }

    private static Vector2 ResolvePersonalBiasDirection(ulong gameObjectId)
    {
        var hash = unchecked((uint)(gameObjectId ^ gameObjectId >> 32));
        hash ^= hash >> 16;
        hash *= 0x7feb352d;
        hash ^= hash >> 15;
        hash *= 0x846ca68b;
        hash ^= hash >> 16;
        var angle = hash / (float)uint.MaxValue * MathF.PI * 2f;
        return new Vector2(MathF.Cos(angle), MathF.Sin(angle));
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

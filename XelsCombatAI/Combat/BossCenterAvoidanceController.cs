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

internal sealed class BossCenterAvoidanceController(
    Configuration config,
    DalamudServices services,
    Func<bool> currentTargetHasBossModule)
    : IBossModGoalZoneContributor, IMovementCandidateSource
{
    private const float BossHitboxAvoidanceMargin = 0.35f;
    private const float CandidateExtraDistance = 0.9f;
    private const float AcceptanceRadius = 0.75f;
    private const float BossLikeHitboxRadius = 4f;
    private static readonly TimeSpan PostMechanicCenterCooldown = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan InsideCenterDwell = TimeSpan.FromMilliseconds(750);
    private static readonly BindingFlags InstanceFlags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

    private FieldInfo? forcedMovementField;
    private FieldInfo? forbiddenZonesField;
    private Type? resolvedHintsType;
    private string hookState = "unresolved";
    private string lastReason = "not evaluated";
    private Vector3? candidate;
    private DateTime suppressComfortUntil = DateTime.MinValue;
    private DateTime insideCenterSince = DateTime.MinValue;
    private bool bmrMoveRequested;
    private bool bmrMoveImminent;

    public string LastReason => this.lastReason;

    internal static float AvoidanceRadius(float hitboxRadius)
    {
        return Math.Max(1.25f, hitboxRadius + BossHitboxAvoidanceMargin);
    }

    public void AddMovementCandidates(MovementPlannerContext context, ICollection<MovementCandidate> candidates)
    {
        if (this.candidate == null)
        {
            return;
        }

        candidates.Add(new(
            "Boss center avoidance",
            this.lastReason,
            this.candidate.Value,
            AcceptanceRadius,
            MovementCandidatePriority.Comfort,
            1f,
            0f,
            0f,
            0.9f));
    }

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
        _ = contributions;
        this.candidate = null;
        var now = DateTime.UtcNow;

        if (!config.Enabled || !config.ManageMovement || !config.AvoidStandingInsideEnemies)
        {
            this.lastReason = "disabled";
            this.insideCenterSince = DateTime.MinValue;
            return;
        }

        if (!services.Condition[ConditionFlag.InCombat] || services.Condition[ConditionFlag.Unconscious])
        {
            this.lastReason = "not active in combat";
            this.insideCenterSince = DateTime.MinValue;
            return;
        }

        var player = services.ObjectTable.LocalPlayer;
        if (player == null || player.IsDead || player.CurrentHp == 0)
        {
            this.lastReason = "player unavailable";
            this.insideCenterSince = DateTime.MinValue;
            return;
        }

        if (!currentTargetHasBossModule())
        {
            this.lastReason = "no boss module target";
            this.insideCenterSince = DateTime.MinValue;
            return;
        }

        if (this.BossModMechanicSafetyActive(hints))
        {
            this.suppressComfortUntil = now.Add(PostMechanicCenterCooldown);
            this.insideCenterSince = DateTime.MinValue;
            this.lastReason = "mechanic safety active";
            return;
        }

        if (now < this.suppressComfortUntil)
        {
            this.lastReason = "post-mechanic center cooldown";
            return;
        }

        if (services.TargetManager.Target is not IBattleChara target ||
            target.IsDead ||
            target.CurrentHp == 0)
        {
            this.lastReason = "boss target unavailable";
            this.insideCenterSince = DateTime.MinValue;
            return;
        }

        if (target.HitboxRadius < BossLikeHitboxRadius)
        {
            this.lastReason = "target hitbox not boss-like";
            this.insideCenterSince = DateTime.MinValue;
            return;
        }

        var avoidanceRadius = AvoidanceRadius(target.HitboxRadius);
        var player2 = new Vector2(player.Position.X, player.Position.Z);
        var target2 = new Vector2(target.Position.X, target.Position.Z);
        if (Vector2.DistanceSquared(player2, target2) >= avoidanceRadius * avoidanceRadius)
        {
            this.lastReason = "outside boss center";
            this.insideCenterSince = DateTime.MinValue;
            return;
        }

        if (this.insideCenterSince == DateTime.MinValue)
        {
            this.insideCenterSince = now;
            this.lastReason = "boss center dwell";
            return;
        }

        if (now - this.insideCenterSince < InsideCenterDwell)
        {
            this.lastReason = "boss center dwell";
            return;
        }

        var away = player.Position - target.Position;
        away.Y = 0f;
        if (away.LengthSquared() <= 0.01f)
        {
            away = -Geometry.RotationToDirection(target.Rotation);
        }

        away = Vector3.Normalize(away);
        this.candidate = new Vector3(target.Position.X, player.Position.Y, target.Position.Z) +
                         away * (avoidanceRadius + CandidateExtraDistance);
        this.lastReason = "avoiding boss center";
    }

    public void Reset()
    {
        this.hookState = "unresolved";
        this.lastReason = "reset";
        this.candidate = null;
        this.forcedMovementField = null;
        this.forbiddenZonesField = null;
        this.resolvedHintsType = null;
        this.suppressComfortUntil = DateTime.MinValue;
        this.insideCenterSince = DateTime.MinValue;
        this.bmrMoveRequested = false;
        this.bmrMoveImminent = false;
    }

    private bool BossModMechanicSafetyActive(object hints)
    {
        if (this.bmrMoveRequested || this.bmrMoveImminent)
        {
            return true;
        }

        if (!this.EnsureResolved(hints.GetType()))
        {
            return false;
        }

        if (this.forbiddenZonesField?.GetValue(hints) is ICollection { Count: > 0 })
        {
            return true;
        }

        return VectorLengthSquared(this.forcedMovementField?.GetValue(hints)) > 0.01f;
    }

    private bool EnsureResolved(Type hintsType)
    {
        if (this.resolvedHintsType == hintsType &&
            this.forcedMovementField != null &&
            this.forbiddenZonesField != null)
        {
            return true;
        }

        var forcedMovement = hintsType.GetField("ForcedMovement", InstanceFlags);
        var forbiddenZones = hintsType.GetField("ForbiddenZones", InstanceFlags);
        if (forcedMovement == null || forbiddenZones == null)
        {
            return false;
        }

        this.forcedMovementField = forcedMovement;
        this.forbiddenZonesField = forbiddenZones;
        this.resolvedHintsType = hintsType;
        return true;
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

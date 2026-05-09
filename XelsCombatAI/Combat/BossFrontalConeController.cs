using System;
using System.Collections.Generic;
using System.Linq.Expressions;
using System.Numerics;
using System.Reflection;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Game.ClientState.Objects.Enums;
using Dalamud.Game.ClientState.Objects.Types;
using XelsCombatAI.Game;
using XelsCombatAI.Integrations;

namespace XelsCombatAI.Combat;

internal sealed record BossFrontalConeOverlaySnapshot(
    Vector3 BossPosition,
    float BossRotation,
    float ConeRadius,
    float ConeHalfAngle,
    bool PlayerInCone,
    bool GoalActive);

internal sealed class BossFrontalConeController(Configuration config, DalamudServices services, BossModIpc bossMod) : IBossModGoalZoneContributor
{
    private static readonly MethodInfo ScoreFromWPosMethod = typeof(BossFrontalConeController).GetMethod(nameof(ScoreFromWPos), BindingFlags.Instance | BindingFlags.NonPublic)!;
    private static readonly BindingFlags InstanceFlags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

    private string hookState = "unresolved";
    private string lastReason = "not evaluated";
    private Vector2 bossPosition;
    private float bossRotation;
    private float bossReach;
    private Type? resolvedHintsType;
    private Type? resolvedWPosType;
    private FieldInfo? wposXField;
    private FieldInfo? wposZField;
    private Delegate? lastGoalDelegate;

    public string LastReason => this.lastReason;
    public BossFrontalConeOverlaySnapshot? Overlay { get; private set; }

    public void SetHookState(string state)
    {
        this.hookState = state;
    }

    public void TryInjectGoal(object hints, ICollection<BossModGoalContribution> contributions)
    {
        this.Overlay = null;

        if (!config.Enabled || !config.ManageMovement || !config.AvoidBossFrontalCone)
        {
            this.lastReason = "disabled";
            return;
        }

        if (!services.Condition[ConditionFlag.InCombat])
        {
            this.lastReason = "out of combat";
            return;
        }

        var player = services.ObjectTable.LocalPlayer;
        if (player == null || player.IsDead || player.CurrentHp == 0)
        {
            this.lastReason = "player unavailable";
            return;
        }

        if (JobRoles.IsTankJob(player.ClassJob.RowId))
        {
            this.lastReason = "player is tank";
            return;
        }

        if (services.TargetManager.Target is not IBattleChara boss || boss.IsDead || boss.CurrentHp == 0)
        {
            this.lastReason = "no valid boss target";
            return;
        }

        var dataId = boss.BaseId;
        if (dataId == 0)
        {
            this.lastReason = "target has no data ID";
            return;
        }

        bool hasBossModule;
        try
        {
            hasBossModule = bossMod.HasModuleByDataId(dataId);
        }
        catch (Exception ex)
        {
            services.Log.Verbose(ex, "BossFrontalConeController: could not query BossMod module state.");
            this.lastReason = "boss module query failed";
            return;
        }

        if (!hasBossModule)
        {
            this.lastReason = "no boss module for target";
            return;
        }

        if (!this.TrySelectTank(player, out var selectedTank) || selectedTank == null)
        {
            this.lastReason = "no party tank";
            return;
        }

        if (!this.EnsureResolved(hints.GetType()))
        {
            return;
        }

        this.bossPosition = new Vector2(boss.Position.X, boss.Position.Z);
        this.bossRotation = boss.Rotation;
        this.bossReach = boss.HitboxRadius + 10f;

        var playerInCone = this.IsPositionInFrontalCone(player.Position);
        this.Overlay = new(boss.Position, boss.Rotation, this.bossReach, MathF.PI / 4f, playerInCone, GoalActive: false);

        var bossTargetId = boss is IBattleNpc bossNpc ? bossNpc.TargetObjectId : boss.TargetObjectId;
        if (bossTargetId != selectedTank.GameObjectId)
        {
            this.lastReason = "boss not targeting tank";
            return;
        }

        if (!this.IsPositionInFrontalCone(selectedTank.Position))
        {
            this.lastReason = "tank not in frontal cone";
            return;
        }

        this.lastGoalDelegate = this.CreateGoalDelegate();
        contributions.Add(new(this.lastGoalDelegate, BossModGoalPriority.Convenience, "Avoid boss front"));
        this.Overlay = this.Overlay with { GoalActive = true };
        this.lastReason = "goal injected";
    }

    public void Reset()
    {
        this.resolvedHintsType = null;
        this.resolvedWPosType = null;
        this.wposXField = null;
        this.wposZField = null;
        this.lastGoalDelegate = null;
        this.Overlay = null;
        this.lastReason = "reset";
    }

    private bool IsPositionInFrontalCone(Vector3 pos)
    {
        var toPlayer = new Vector2(pos.X, pos.Z) - this.bossPosition;
        var dist = toPlayer.Length();
        if (dist < 0.5f || dist > this.bossReach)
        {
            return false;
        }

        var frontDir = new Vector2(MathF.Sin(this.bossRotation), MathF.Cos(this.bossRotation));
        return Vector2.Dot(toPlayer / dist, frontDir) >= CombatConstants.PositionalDotThreshold;
    }

    private bool TrySelectTank(IBattleChara player, out IBattleChara? tank)
    {
        tank = PartyAllyProvider.SelectBestTank(services, player);
        return tank != null;
    }

    private bool EnsureResolved(Type hintsType)
    {
        if (this.resolvedHintsType == hintsType &&
            this.resolvedWPosType != null &&
            this.wposXField != null &&
            this.wposZField != null)
        {
            return true;
        }

        var wposType = hintsType.Assembly.GetType("BossMod.WPos");
        var xField = wposType?.GetField("X", InstanceFlags);
        var zField = wposType?.GetField("Z", InstanceFlags);
        if (wposType == null || xField == null || zField == null)
        {
            this.lastReason = "BMR frontal cone reflection members unavailable";
            return false;
        }

        this.resolvedHintsType = hintsType;
        this.resolvedWPosType = wposType;
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
        var toPoint = new Vector2(x, z) - this.bossPosition;
        var dist = toPoint.Length();
        if (dist < 0.5f || dist > this.bossReach)
        {
            return GoalZoneScorePolicy.WeakPreference;
        }

        var frontDir = new Vector2(MathF.Sin(this.bossRotation), MathF.Cos(this.bossRotation));
        var dot = Vector2.Dot(toPoint / dist, frontDir);
        return dot >= CombatConstants.PositionalDotThreshold
            ? 0f
            : GoalZoneScorePolicy.WeakPreference;
    }
}

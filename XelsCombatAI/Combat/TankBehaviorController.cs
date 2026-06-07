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
using ECommons.GameFunctions;
using FFXIVClientStructs.FFXIV.Client.Game;
using XelsCombatAI.Game;

namespace XelsCombatAI.Combat;

internal sealed class TankBehaviorController(
    Configuration config,
    DalamudServices services,
    Func<bool> currentTargetHasBossModule)
    : IBossModGoalZoneContributor
{
    private static readonly TimeSpan TargetSwitchCooldown = TimeSpan.FromMilliseconds(1500);
    private static readonly TimeSpan ActionCooldown = TimeSpan.FromMilliseconds(1500);
    private static readonly MethodInfo ScoreConeAwayFromPartyMethod = typeof(TankBehaviorController).GetMethod(nameof(ScoreConeAwayFromParty), BindingFlags.Static | BindingFlags.NonPublic)!;

    private const float ConeCasualtyAvoidanceScoreWeight = 0.98f;
    private const float ConeAwayTieBreakerScoreWeight = 1f - ConeCasualtyAvoidanceScoreWeight;
    private const float NearbyAggroSurfaceRange = 8f;
    private const float RangedAggroMinimumSurfaceRange = CombatConstants.MeleeActionRange;
    private const float RangedAggroSurfaceRange = 20f;
    private const float ProvokeSurfaceRange = 25f;
    private const float TrashPackScanRange = 35f;
    private const float ConeOriginTolerance = 3f;
    private const float CleaveHintConeMinimumRadius = 90f;
    private const float ConePartyGoalMinimumPartyDistance = 6f;

    private FieldInfo? forbiddenZonesField;
    private FieldInfo? wposXField;
    private FieldInfo? wposZField;
    private FieldInfo? coneOriginXField;
    private FieldInfo? coneOriginZField;
    private FieldInfo? coneRadiusField;
    private FieldInfo? coneFactorField;
    private FieldInfo? coneLeftNormalXField;
    private FieldInfo? coneLeftNormalZField;
    private FieldInfo? coneRightNormalXField;
    private FieldInfo? coneRightNormalZField;
    private Type? resolvedHintsType;
    private Type? resolvedWPosType;
    private Delegate? coneAwayFromPartyGoal;
    private Vector2 coneAwayFromPartyTarget;
    private Vector2 coneAwayFromPartyParty;
    private Vector2[] coneAwayFromPartyMembers = [];
    private float coneAwayFromPartyHalfAngleCos;
    private DateTime lastTargetSwitchAt = DateTime.MinValue;
    private DateTime lastActionAt = DateTime.MinValue;
    private string hookState = "unresolved";

    public string LastReason { get; private set; } = "not evaluated";

    public void Tick()
    {
        if (!this.CanRun(out var player))
        {
            return;
        }

        if (config.TankDropStanceWhenCoTankHasStance &&
            currentTargetHasBossModule() &&
            this.TryDropStanceForCoTank(player))
        {
            return;
        }

        if (!config.TankTargetLostTrashAggro && !config.TankUseRangedAggroRecovery)
        {
            return;
        }

        if (currentTargetHasBossModule())
        {
            this.LastReason = "boss module active";
            return;
        }

        var lostAggroTargets = this.FindLostAggroTrashTargets(player);
        if (lostAggroTargets.Count == 0)
        {
            this.LastReason = "no lost trash aggro";
            return;
        }

        if (config.TankUseRangedAggroRecovery && this.TryUseRangedAggroRecovery(player, lostAggroTargets))
        {
            return;
        }

        if (config.TankTargetLostTrashAggro)
        {
            this.TrySelectNearbyLostAggroTarget(player, lostAggroTargets);
        }
    }

    public void SetHookState(string state)
    {
        this.hookState = state;
    }

    public void TryInjectGoal(object hints, ICollection<BossModGoalContribution> contributions)
    {
        if (!this.CanRun(out var player))
        {
            return;
        }

        if (!this.EnsureResolved(hints))
        {
            this.LastReason = $"{this.hookState}: tank hint fields unavailable";
            return;
        }

        TankConeHint? tankConeHint = null;
        if (config.TankIgnoreFrontConeMovement)
        {
            tankConeHint = this.FilterTankFrontConeForbiddenZones(hints, player);
        }
        else
        {
            tankConeHint = this.FindTankFrontConeForbiddenZone(hints, player);
        }

        if (config.TankKeepFrontConeAwayFromParty && tankConeHint is { } hint)
        {
            this.TryAddConeAwayFromPartyGoal(player, hint, contributions);
        }
    }

    public void Reset()
    {
        this.forbiddenZonesField = null;
        this.wposXField = null;
        this.wposZField = null;
        this.coneOriginXField = null;
        this.coneOriginZField = null;
        this.coneRadiusField = null;
        this.coneFactorField = null;
        this.coneLeftNormalXField = null;
        this.coneLeftNormalZField = null;
        this.coneRightNormalXField = null;
        this.coneRightNormalZField = null;
        this.resolvedHintsType = null;
        this.resolvedWPosType = null;
        this.coneAwayFromPartyGoal = null;
        this.coneAwayFromPartyMembers = [];
        this.coneAwayFromPartyHalfAngleCos = default;
        this.lastTargetSwitchAt = DateTime.MinValue;
        this.lastActionAt = DateTime.MinValue;
        this.LastReason = "reset";
    }

    private bool CanRun(out IBattleChara player)
    {
        player = null!;
        if (!config.Enabled ||
            services.Condition[ConditionFlag.Unconscious] ||
            services.ObjectTable.LocalPlayer is not { } localPlayer ||
            localPlayer.IsDead ||
            localPlayer.CurrentHp == 0 ||
            !JobRoles.IsTankJob(localPlayer.ClassJob.RowId))
        {
            return false;
        }

        player = localPlayer;
        return true;
    }

    private unsafe bool TryDropStanceForCoTank(IBattleChara player)
    {
        if (!PartyAllyProvider.HasTankStance(player) ||
            !TryGetTankStanceAction(player.ClassJob.RowId, out _, out var releaseActionId, out _))
        {
            return false;
        }

        var otherTankHasStance = PartyAllyProvider
            .GetVisiblePartyAllies(services, player)
            .Members
            .Any(ally => ally.ObjectKind == ObjectKind.Pc &&
                         ally.GameObjectId != player.GameObjectId &&
                         JobRoles.IsTankJob(ally.ClassJob.RowId) &&
                         PartyAllyProvider.HasTankStance(ally));

        if (!otherTankHasStance)
        {
            this.LastReason = "no co-tank stance";
            return false;
        }

        if (!this.CanUseActionNow(releaseActionId))
        {
            this.LastReason = "stance release unavailable";
            return false;
        }

        var used = ActionManager.Instance()->UseAction(ActionType.Action, releaseActionId, player.GameObjectId);
        this.RecordAction($"drop stance for co-tank: {used}");
        return used;
    }

    private List<IBattleNpc> FindLostAggroTrashTargets(IBattleChara player)
    {
        var nonTankPartyIds = PartyAllyProvider
            .GetVisiblePartyAllies(services, player)
            .Members
            .Where(ally => !JobRoles.IsTankJob(ally.ClassJob.RowId))
            .Select(ally => ally.GameObjectId)
            .ToHashSet();

        if (nonTankPartyIds.Count == 0)
        {
            return [];
        }

        var hostileCombatants = services.ObjectTable
            .OfType<IBattleNpc>()
            .Where(npc => npc.BattleNpcKind == BattleNpcSubKind.Combatant &&
                          npc.StatusFlags.HasFlag(StatusFlags.InCombat) &&
                          npc.IsHostile() &&
                          !npc.IsDead &&
                          npc.CurrentHp > 0 &&
                          Geometry.Distance2D(player.Position, npc.Position) <= TrashPackScanRange)
            .ToArray();

        if (hostileCombatants.Length < 2)
        {
            return [];
        }

        return hostileCombatants
            .Where(npc => npc.TargetObjectId != player.GameObjectId && nonTankPartyIds.Contains(npc.TargetObjectId))
            .OrderBy(npc => Geometry.DistanceToHitbox(player.Position, player.HitboxRadius, npc.Position, npc.HitboxRadius))
            .ToList();
    }

    private unsafe bool TryUseRangedAggroRecovery(IBattleChara player, IReadOnlyList<IBattleNpc> lostAggroTargets)
    {
        if (!TryGetTankStanceAction(player.ClassJob.RowId, out _, out _, out var rangedActionId) ||
            DateTime.UtcNow - this.lastActionAt < ActionCooldown ||
            ActionUse.HasAnimationLock())
        {
            return false;
        }

        foreach (var target in lostAggroTargets)
        {
            var surfaceDistance = Geometry.DistanceToHitbox(player.Position, player.HitboxRadius, target.Position, target.HitboxRadius);
            if (surfaceDistance <= RangedAggroMinimumSurfaceRange ||
                surfaceDistance > ProvokeSurfaceRange)
            {
                continue;
            }

            if (surfaceDistance <= RangedAggroSurfaceRange && this.CanUseActionNow(rangedActionId))
            {
                var used = ActionManager.Instance()->UseAction(ActionType.Action, rangedActionId, target.GameObjectId);
                this.RecordAction($"ranged aggro recovery: {used}");
                return used;
            }

            if (this.CanUseActionNow(ActionUse.ProvokeActionId))
            {
                var used = ActionManager.Instance()->UseAction(ActionType.Action, ActionUse.ProvokeActionId, target.GameObjectId);
                this.RecordAction($"provoke aggro recovery: {used}");
                return used;
            }
        }

        return false;
    }

    private void TrySelectNearbyLostAggroTarget(IBattleChara player, IReadOnlyList<IBattleNpc> lostAggroTargets)
    {
        if (DateTime.UtcNow - this.lastTargetSwitchAt < TargetSwitchCooldown)
        {
            this.LastReason = "target switch cooldown";
            return;
        }

        var target = lostAggroTargets.FirstOrDefault(npc =>
            Geometry.DistanceToHitbox(player.Position, player.HitboxRadius, npc.Position, npc.HitboxRadius) <= NearbyAggroSurfaceRange);
        if (target == null)
        {
            this.LastReason = "lost aggro target not nearby";
            return;
        }

        if (services.TargetManager.Target?.GameObjectId == target.GameObjectId)
        {
            this.LastReason = "lost aggro target already selected";
            return;
        }

        services.TargetManager.Target = target;
        this.lastTargetSwitchAt = DateTime.UtcNow;
        this.LastReason = "selected nearby lost aggro target";
    }

    private unsafe bool CanUseActionNow(uint actionId)
    {
        return actionId != 0 &&
               !ActionUse.HasAnimationLock() &&
               ActionUse.CanUseAction(actionId);
    }

    private unsafe void RecordAction(string reason)
    {
        this.lastActionAt = DateTime.UtcNow;
        this.LastReason = reason;
    }

    private bool EnsureResolved(object hints)
    {
        var hintsType = hints.GetType();
        if (this.resolvedHintsType == hintsType &&
            this.forbiddenZonesField != null &&
            this.resolvedWPosType != null &&
            this.wposXField != null &&
            this.wposZField != null)
        {
            return true;
        }

        const BindingFlags Flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        this.resolvedHintsType = hintsType;
        this.forbiddenZonesField = hintsType.GetField("ForbiddenZones", Flags);
        this.resolvedWPosType = hintsType.Assembly.GetType("BossMod.WPos");
        this.wposXField = this.resolvedWPosType?.GetField("X", Flags);
        this.wposZField = this.resolvedWPosType?.GetField("Z", Flags);
        return this.forbiddenZonesField != null &&
               this.resolvedWPosType != null &&
               this.wposXField != null &&
               this.wposZField != null;
    }

    private TankConeHint? FilterTankFrontConeForbiddenZones(object hints, IBattleChara player)
    {
        if (!this.IsCurrentTargetTankFrontConeSource(player, out var target) ||
            this.forbiddenZonesField?.GetValue(hints) is not IList zones)
        {
            return null;
        }

        var removed = 0;
        TankConeHint? firstHint = null;
        for (var i = zones.Count - 1; i >= 0; --i)
        {
            if (this.IsTankFrontConeZone(zones[i], target, persistentCleaveOnly: true, out var hint))
            {
                firstHint ??= hint;
                zones.RemoveAt(i);
                removed++;
            }
        }

        if (removed > 0)
        {
            this.LastReason = $"ignored tank front cone zones: {removed}";
            return firstHint;
        }

        return null;
    }

    private TankConeHint? FindTankFrontConeForbiddenZone(object hints, IBattleChara player)
    {
        if (!this.IsCurrentTargetTankFrontConeSource(player, out var target) ||
            this.forbiddenZonesField?.GetValue(hints) is not IList zones)
        {
            return null;
        }

        foreach (var zone in zones)
        {
            if (this.IsTankFrontConeZone(zone, target, persistentCleaveOnly: false, out var hint))
            {
                return hint;
            }
        }

        return null;
    }

    private bool IsCurrentTargetTankFrontConeSource(IBattleChara player, out IBattleChara target)
    {
        target = null!;
        if (services.TargetManager.Target is not IBattleChara currentTarget)
        {
            return false;
        }

        if (currentTarget.IsDead ||
            currentTarget.CurrentHp == 0 ||
            currentTarget.TargetObjectId != player.GameObjectId ||
            !currentTarget.IsHostile())
        {
            return false;
        }

        target = currentTarget;
        return true;
    }

    private bool IsTankFrontConeZone(object? zone, IBattleChara target, bool persistentCleaveOnly, out TankConeHint hint)
    {
        hint = default;
        if (persistentCleaveOnly &&
            (!TryReadZoneActivation(zone, out var activation) ||
            activation != default))
        {
            return false;
        }

        var shape = ReadTupleField(zone, "shapeDistance", "Item1");
        if (shape == null || !string.Equals(shape.GetType().Name, "SDCone", StringComparison.Ordinal))
        {
            return false;
        }

        if (!this.TryReadConeOrigin(shape, out var origin) ||
            !this.TryReadConeHalfAngleCos(shape, out var halfAngleCos))
        {
            return false;
        }

        if (persistentCleaveOnly &&
            (!this.TryReadConeRadius(shape, out var radius) ||
            radius < CleaveHintConeMinimumRadius))
        {
            return false;
        }

        var targetPos = new Vector2(target.Position.X, target.Position.Z);
        if (Vector2.Distance(origin, targetPos) > target.HitboxRadius + ConeOriginTolerance)
        {
            return false;
        }

        hint = new TankConeHint(halfAngleCos);
        return true;
    }

    private bool TryReadConeRadius(object shape, out float radius)
    {
        radius = default;
        const BindingFlags Flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        var shapeType = shape.GetType();
        this.coneRadiusField ??= shapeType.GetField("radius", Flags);
        if (this.coneRadiusField == null)
        {
            return false;
        }

        radius = Convert.ToSingle(this.coneRadiusField.GetValue(shape));
        return true;
    }

    private bool TryReadConeHalfAngleCos(object shape, out float halfAngleCos)
    {
        halfAngleCos = default;
        const BindingFlags Flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        var shapeType = shape.GetType();
        this.coneFactorField ??= shapeType.GetField("coneFactor", Flags);
        this.coneLeftNormalXField ??= shapeType.GetField("nlX", Flags);
        this.coneLeftNormalZField ??= shapeType.GetField("nlZ", Flags);
        this.coneRightNormalXField ??= shapeType.GetField("nrX", Flags);
        this.coneRightNormalZField ??= shapeType.GetField("nrZ", Flags);
        if (this.coneFactorField == null ||
            this.coneLeftNormalXField == null ||
            this.coneLeftNormalZField == null ||
            this.coneRightNormalXField == null ||
            this.coneRightNormalZField == null)
        {
            return false;
        }

        var coneFactor = Convert.ToSingle(this.coneFactorField.GetValue(shape));
        var left = new Vector2(
            Convert.ToSingle(this.coneLeftNormalXField.GetValue(shape)),
            Convert.ToSingle(this.coneLeftNormalZField.GetValue(shape)));
        var right = new Vector2(
            Convert.ToSingle(this.coneRightNormalXField.GetValue(shape)),
            Convert.ToSingle(this.coneRightNormalZField.GetValue(shape)));
        var normalLengthProduct = left.Length() * right.Length();
        if (normalLengthProduct <= 0.0001f)
        {
            return false;
        }

        var normalDot = Vector2.Dot(left, right) / normalLengthProduct;
        var cosDoubleAngle = Math.Clamp(-normalDot, -1f, 1f);
        var magnitude = MathF.Sqrt(Math.Clamp((1f + cosDoubleAngle) * 0.5f, 0f, 1f));
        halfAngleCos = coneFactor < 0f ? -magnitude : magnitude;
        return true;
    }

    private bool TryReadConeOrigin(object shape, out Vector2 origin)
    {
        origin = default;
        const BindingFlags Flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        var shapeType = shape.GetType();
        this.coneOriginXField ??= shapeType.GetField("originX", Flags);
        this.coneOriginZField ??= shapeType.GetField("originZ", Flags);
        if (this.coneOriginXField == null ||
            this.coneOriginZField == null)
        {
            return false;
        }

        origin = new Vector2(
            Convert.ToSingle(this.coneOriginXField.GetValue(shape)),
            Convert.ToSingle(this.coneOriginZField.GetValue(shape)));
        return true;
    }

    private void TryAddConeAwayFromPartyGoal(IBattleChara player, TankConeHint hint, ICollection<BossModGoalContribution> contributions)
    {
        if (services.TargetManager.Target is not IBattleChara target)
        {
            return;
        }

        var party = PartyAllyProvider
            .GetVisiblePartyAllies(services, player)
            .Members
            .Where(ally => Geometry.Distance2D(target.Position, ally.Position) >= ConePartyGoalMinimumPartyDistance)
            .ToArray();
        if (party.Length == 0)
        {
            return;
        }

        var targetPosition = new Vector2(target.Position.X, target.Position.Z);
        var partyPositions = party
            .Select(ally => new Vector2(ally.Position.X, ally.Position.Z))
            .ToArray();
        var partyPosition = partyPositions.Aggregate(Vector2.Zero, (sum, position) => sum + position) / partyPositions.Length;
        if (this.coneAwayFromPartyGoal == null ||
            Vector2.Distance(this.coneAwayFromPartyTarget, targetPosition) > 1f ||
            Vector2.Distance(this.coneAwayFromPartyParty, partyPosition) > 1f ||
            MathF.Abs(this.coneAwayFromPartyHalfAngleCos - hint.HalfAngleCos) > 0.01f ||
            !SamePartyPositions(this.coneAwayFromPartyMembers, partyPositions))
        {
            this.coneAwayFromPartyGoal = this.CreateConeAwayFromPartyGoal(targetPosition, partyPosition, partyPositions, hint.HalfAngleCos);
            this.coneAwayFromPartyTarget = targetPosition;
            this.coneAwayFromPartyParty = partyPosition;
            this.coneAwayFromPartyMembers = partyPositions;
            this.coneAwayFromPartyHalfAngleCos = hint.HalfAngleCos;
        }

        contributions.Add(new(
            this.coneAwayFromPartyGoal,
            BossModGoalPriority.PartyUtility,
            "Tank cone away from party"));
    }

    private Delegate CreateConeAwayFromPartyGoal(Vector2 targetPosition, Vector2 partyPosition, Vector2[] partyPositions, float halfAngleCos)
    {
        var parameter = Expression.Parameter(this.resolvedWPosType!, "p");
        var x = Expression.Convert(Expression.Field(parameter, this.wposXField!), typeof(float));
        var z = Expression.Convert(Expression.Field(parameter, this.wposZField!), typeof(float));
        var score = Expression.Call(
            ScoreConeAwayFromPartyMethod,
            x,
            z,
            Expression.Constant(targetPosition.X),
            Expression.Constant(targetPosition.Y),
            Expression.Constant(partyPosition.X),
            Expression.Constant(partyPosition.Y),
            Expression.Constant(partyPositions),
            Expression.Constant(halfAngleCos));
        var delegateType = typeof(Func<,>).MakeGenericType(this.resolvedWPosType!, typeof(float));
        return Expression.Lambda(delegateType, score, parameter).Compile();
    }

    private static float ScoreConeAwayFromParty(
        float candidateX,
        float candidateZ,
        float targetX,
        float targetZ,
        float partyX,
        float partyZ,
        Vector2[] partyPositions,
        float halfAngleCos)
    {
        var tankVector = new Vector2(candidateX - targetX, candidateZ - targetZ);
        var partyVector = new Vector2(partyX - targetX, partyZ - targetZ);
        if (tankVector.LengthSquared() <= 0.01f || partyVector.LengthSquared() <= 0.01f || partyPositions.Length == 0)
        {
            return 0f;
        }

        tankVector = Vector2.Normalize(tankVector);
        var hitCount = 0;
        foreach (var partyPosition in partyPositions)
        {
            var memberVector = partyPosition - new Vector2(targetX, targetZ);
            if (memberVector.LengthSquared() <= 0.01f)
            {
                continue;
            }

            memberVector = Vector2.Normalize(memberVector);
            if (Vector2.Dot(tankVector, memberVector) >= halfAngleCos)
            {
                hitCount++;
            }
        }

        partyVector = Vector2.Normalize(partyVector);
        var casualtyScore = 1f - hitCount / (float)partyPositions.Length;
        var awayScore = Math.Clamp(-Vector2.Dot(tankVector, partyVector), 0f, 1f);
        return casualtyScore * ConeCasualtyAvoidanceScoreWeight +
               awayScore * ConeAwayTieBreakerScoreWeight;
    }

    private static bool SamePartyPositions(IReadOnlyList<Vector2> previous, IReadOnlyList<Vector2> current)
    {
        if (previous.Count != current.Count)
        {
            return false;
        }

        for (var i = 0; i < previous.Count; i++)
        {
            if (Vector2.Distance(previous[i], current[i]) > 1f)
            {
                return false;
            }
        }

        return true;
    }

    private static object? ReadTupleField(object? value, string namedField, string itemField)
    {
        if (value == null)
        {
            return null;
        }

        const BindingFlags Flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        var type = value.GetType();
        return type.GetField(namedField, Flags)?.GetValue(value) ??
               type.GetField(itemField, Flags)?.GetValue(value);
    }

    private static bool TryReadZoneActivation(object? zone, out DateTime activation)
    {
        activation = default;
        if (ReadTupleField(zone, "activation", "Item2") is not DateTime value)
        {
            return false;
        }

        activation = value;
        return true;
    }

    private readonly record struct TankConeHint(float HalfAngleCos);

    private static bool TryGetTankStanceAction(uint classJobId, out uint stanceActionId, out uint releaseActionId, out uint rangedActionId)
    {
        switch (classJobId)
        {
            case 1:
            case 19:
                stanceActionId = ActionUse.PaladinIronWillActionId;
                releaseActionId = ActionUse.PaladinReleaseIronWillActionId;
                rangedActionId = ActionUse.PaladinShieldLobActionId;
                break;
            case 3:
            case 21:
                stanceActionId = ActionUse.WarriorDefianceActionId;
                releaseActionId = ActionUse.WarriorReleaseDefianceActionId;
                rangedActionId = ActionUse.WarriorTomahawkActionId;
                break;
            case 32:
                stanceActionId = ActionUse.DarkKnightGritActionId;
                releaseActionId = ActionUse.DarkKnightReleaseGritActionId;
                rangedActionId = ActionUse.DarkKnightUnmendActionId;
                break;
            case 37:
                stanceActionId = ActionUse.GunbreakerRoyalGuardActionId;
                releaseActionId = ActionUse.GunbreakerReleaseRoyalGuardActionId;
                rangedActionId = ActionUse.GunbreakerLightningShotActionId;
                break;
            default:
                stanceActionId = 0;
                releaseActionId = 0;
                rangedActionId = 0;
                break;
        }

        return stanceActionId != 0;
    }
}

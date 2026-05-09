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

internal sealed record AggroSafetyStatus(
    string HookState,
    string LastReason,
    bool Injected,
    ulong ActiveMobId,
    ulong SelectedTankId,
    float AggroSeconds,
    bool PriorityDevalued);

internal sealed class AggroSafetyController(Configuration config, DalamudServices services) : IBossModGoalZoneContributor
{
    private static readonly TimeSpan AggroThreshold = TimeSpan.FromSeconds(3);
    private static readonly MethodInfo ScoreFromWPosMethod = typeof(AggroSafetyController).GetMethod(nameof(ScoreFromWPos), BindingFlags.Instance | BindingFlags.NonPublic)!;

    private readonly Dictionary<ulong, DateTime> aggroStartByMob = [];
    private string hookState = "unresolved";
    private string lastReason = "not evaluated";
    private bool injected;
    private ulong activeMobId;
    private ulong selectedTankId;
    private float aggroSeconds;
    private bool priorityDevalued;
    private Vector2 tankPosition;
    private float tankRadius;
    private Type? resolvedHintsType;
    private Type? resolvedWPosType;
    private FieldInfo? potentialTargetsField;
    private FieldInfo? highestPotentialTargetPriorityField;
    private FieldInfo? enemyActorField;
    private PropertyInfo? enemyPriorityProperty;
    private FieldInfo? actorInstanceIdField;
    private FieldInfo? wposXField;
    private FieldInfo? wposZField;
    private Delegate? lastGoalDelegate;

    public AggroSafetyStatus Status => new(
        this.hookState,
        this.lastReason,
        this.injected,
        this.activeMobId,
        this.selectedTankId,
        this.aggroSeconds,
        this.priorityDevalued);

    public void SetHookState(string state)
    {
        this.hookState = state;
    }

    public void TryInjectGoal(object hints, ICollection<BossModGoalContribution> contributions)
    {
        this.injected = false;
        this.activeMobId = 0;
        this.selectedTankId = 0;
        this.aggroSeconds = 0f;
        this.priorityDevalued = false;

        var player = services.ObjectTable.LocalPlayer;
        if (!config.Enabled || !config.ManageMovement || !config.ManageAggroSafetyMovement)
        {
            this.ResetTracking("disabled");
            return;
        }

        if (!services.Condition[ConditionFlag.InCombat])
        {
            this.ResetTracking("out of combat");
            return;
        }

        if (player == null || player.IsDead || player.CurrentHp == 0)
        {
            this.ResetTracking("player unavailable");
            return;
        }

        if (JobRoles.IsTankJob(player.ClassJob.RowId))
        {
            this.ResetTracking("player is tank");
            return;
        }

        var now = DateTime.UtcNow;
        var attackers = this.GetAttackers(player).ToArray();
        this.UpdateAggroTracking(attackers, now);
        var active = attackers
            .Select(mob => (Mob: mob, Started: this.aggroStartByMob.TryGetValue(mob.GameObjectId, out var started) ? started : now))
            .Where(entry => now - entry.Started >= AggroThreshold)
            .OrderBy(entry => entry.Started)
            .FirstOrDefault();
        if (active.Mob == null)
        {
            this.aggroSeconds = attackers.Length == 0
                ? 0f
                : (float)attackers.Max(mob => (now - this.aggroStartByMob[mob.GameObjectId]).TotalSeconds);
            this.lastReason = attackers.Length == 0 ? "no mob aggro" : "aggro below threshold";
            return;
        }

        if (!this.TrySelectTank(player, out var tank))
        {
            this.activeMobId = active.Mob.GameObjectId;
            this.aggroSeconds = (float)(now - active.Started).TotalSeconds;
            this.lastReason = "no party tank available";
            return;
        }

        if (!this.EnsureResolved(hints.GetType()))
        {
            return;
        }

        this.activeMobId = active.Mob.GameObjectId;
        this.selectedTankId = tank.GameObjectId;
        this.aggroSeconds = (float)(now - active.Started).TotalSeconds;
        this.tankPosition = new Vector2(tank.Position.X, tank.Position.Z);
        this.tankRadius = MathF.Max(1.5f, tank.HitboxRadius + 1.5f);
        this.priorityDevalued = this.TryDevalueTarget(hints, active.Mob.GameObjectId);
        this.lastGoalDelegate = this.CreateGoalDelegate();
        contributions.Add(new(this.lastGoalDelegate, BossModGoalPriority.ImmediateAction, "Aggro safety"));
        this.injected = true;
        this.lastReason = this.priorityDevalued ? "bringing aggro to tank" : "bringing aggro to tank; priority unchanged";
    }

    public void Reset()
    {
        this.ResetTracking("reset");
    }

    private IEnumerable<IBattleNpc> GetAttackers(IBattleChara player)
    {
        foreach (var npc in services.ObjectTable.OfType<IBattleNpc>())
        {
            if (this.IsHostileCombatNpc(npc) && npc.TargetObjectId == player.GameObjectId)
            {
                yield return npc;
            }
        }
    }

    private void UpdateAggroTracking(IReadOnlyCollection<IBattleNpc> attackers, DateTime now)
    {
        var activeIds = attackers.Select(mob => mob.GameObjectId).ToHashSet();
        foreach (var id in this.aggroStartByMob.Keys.Where(id => !activeIds.Contains(id)).ToArray())
        {
            this.aggroStartByMob.Remove(id);
        }

        foreach (var mob in attackers)
        {
            this.aggroStartByMob.TryAdd(mob.GameObjectId, now);
        }
    }

    private bool TrySelectTank(IBattleChara player, out IBattleChara tank)
    {
        tank = PartyAllyProvider.SelectBestTank(services, player)!;
        return tank != null;
    }

    private bool IsHostileCombatNpc(IBattleNpc npc)
    {
        return npc.BattleNpcKind == BattleNpcSubKind.Combatant &&
               npc.StatusFlags.HasFlag(StatusFlags.InCombat) &&
               npc.StatusFlags.HasFlag(StatusFlags.Hostile) &&
               !npc.IsDead &&
               npc.CurrentHp > 0;
    }

    private bool EnsureResolved(Type hintsType)
    {
        if (this.resolvedHintsType == hintsType &&
            this.resolvedWPosType != null &&
            this.potentialTargetsField != null &&
            this.highestPotentialTargetPriorityField != null &&
            this.enemyActorField != null &&
            this.enemyPriorityProperty != null &&
            this.actorInstanceIdField != null &&
            this.wposXField != null &&
            this.wposZField != null)
        {
            return true;
        }

        const BindingFlags InstanceFlags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        var wposType = hintsType.Assembly.GetType("BossMod.WPos");
        var potentialTargets = hintsType.GetField("PotentialTargets", InstanceFlags);
        var highestPotentialTargetPriority = hintsType.GetField("HighestPotentialTargetPriority", InstanceFlags);
        var enemyType = hintsType.GetNestedType("Enemy", BindingFlags.Public);
        var actorField = enemyType?.GetField("Actor", InstanceFlags);
        var actorType = actorField?.FieldType;
        var priorityProperty = enemyType?.GetProperty("Priority", InstanceFlags);
        var instanceField = actorType?.GetField("InstanceID", InstanceFlags);
        var xField = wposType?.GetField("X", InstanceFlags);
        var zField = wposType?.GetField("Z", InstanceFlags);
        if (wposType == null ||
            potentialTargets == null ||
            highestPotentialTargetPriority == null ||
            actorField == null ||
            priorityProperty == null ||
            instanceField == null ||
            xField == null ||
            zField == null)
        {
            this.lastReason = $"BMR aggro safety members unavailable: {FormatMissing(
                (wposType == null, "BossMod.WPos"),
                (potentialTargets == null, "AIHints.PotentialTargets"),
                (highestPotentialTargetPriority == null, "AIHints.HighestPotentialTargetPriority"),
                (enemyType == null, "AIHints.Enemy"),
                (actorField == null, "AIHints.Enemy.Actor"),
                (priorityProperty == null, "AIHints.Enemy.Priority"),
                (instanceField == null, "Actor.InstanceID"),
                (xField == null, "BossMod.WPos.X"),
                (zField == null, "BossMod.WPos.Z"))}";
            return false;
        }

        this.resolvedHintsType = hintsType;
        this.resolvedWPosType = wposType;
        this.potentialTargetsField = potentialTargets;
        this.highestPotentialTargetPriorityField = highestPotentialTargetPriority;
        this.enemyActorField = actorField;
        this.enemyPriorityProperty = priorityProperty;
        this.actorInstanceIdField = instanceField;
        this.wposXField = xField;
        this.wposZField = zField;
        return true;
    }

    private bool TryDevalueTarget(object hints, ulong mobId)
    {
        if (this.potentialTargetsField!.GetValue(hints) is not IList potentialTargets)
        {
            this.lastReason = "BMR target list unavailable";
            return false;
        }

        var changed = false;
        foreach (var enemy in potentialTargets)
        {
            var actor = this.enemyActorField!.GetValue(enemy);
            if (actor == null)
            {
                continue;
            }

            var instanceId = Convert.ToUInt64(this.actorInstanceIdField!.GetValue(actor), System.Globalization.CultureInfo.InvariantCulture);
            if (instanceId != mobId)
            {
                continue;
            }

            var priority = Convert.ToInt32(this.enemyPriorityProperty!.GetValue(enemy), System.Globalization.CultureInfo.InvariantCulture);
            if (priority > -3)
            {
                this.enemyPriorityProperty.SetValue(enemy, -3);
                changed = true;
            }

            break;
        }

        if (changed)
        {
            this.SortPotentialTargets(hints, potentialTargets);
        }

        return changed;
    }

    private void SortPotentialTargets(object hints, IList potentialTargets)
    {
        var sorted = potentialTargets
            .Cast<object>()
            .OrderByDescending(enemy => Convert.ToInt32(this.enemyPriorityProperty!.GetValue(enemy), System.Globalization.CultureInfo.InvariantCulture))
            .ToArray();
        potentialTargets.Clear();
        foreach (var enemy in sorted)
        {
            potentialTargets.Add(enemy);
        }

        var highest = sorted.Length == 0
            ? 0
            : Math.Max(0, Convert.ToInt32(this.enemyPriorityProperty!.GetValue(sorted[0]), System.Globalization.CultureInfo.InvariantCulture));
        this.highestPotentialTargetPriorityField!.SetValue(hints, highest);
    }

    private Delegate CreateGoalDelegate()
    {
        var parameter = Expression.Parameter(this.resolvedWPosType!, "p");
        var call = Expression.Call(
            Expression.Constant(this),
            ScoreFromWPosMethod,
            Expression.Field(parameter, this.wposXField!),
            Expression.Field(parameter, this.wposZField!));
        var delegateType = typeof(Func<,>).MakeGenericType(this.resolvedWPosType!, typeof(float));
        return Expression.Lambda(delegateType, call, parameter).Compile();
    }

    private float ScoreFromWPos(float x, float z)
    {
        var distance = Vector2.Distance(new Vector2(x, z), this.tankPosition);
        if (distance <= this.tankRadius)
        {
            return GoalZoneScorePolicy.StrongPreference;
        }

        var falloff = 8f;
        if (distance >= this.tankRadius + falloff)
        {
            return 0f;
        }

        return GoalZoneScorePolicy.StrongPreference * (1f - ((distance - this.tankRadius) / falloff));
    }

    private void ResetTracking(string reason)
    {
        this.aggroStartByMob.Clear();
        this.injected = false;
        this.activeMobId = 0;
        this.selectedTankId = 0;
        this.aggroSeconds = 0f;
        this.priorityDevalued = false;
        this.lastGoalDelegate = null;
        this.lastReason = reason;
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
}

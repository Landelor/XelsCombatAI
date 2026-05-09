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

internal sealed record SurvivabilityZonePositioningStatus(
    string HookState,
    string LastReason,
    bool Injected,
    string ZoneName,
    string CasterName,
    float DistanceToCenter);

internal sealed record SurvivabilityZoneOverlaySnapshot(
    Vector3 ZoneCenter,
    Vector3 CasterPosition,
    float Radius,
    bool Injected,
    bool PlayerInZone,
    string ZoneName,
    string CasterName);

internal sealed class SurvivabilityZonePositioningController(
    Configuration config,
    DalamudServices services,
    Func<bool> automatedMovementSuppressed)
    : IBossModGoalZoneContributor
{
    private const float NearCenterRadius = 1f;
    private const float NearCenterScore = GoalZoneScorePolicy.StrongPreference;
    private const float InsideScore = GoalZoneScorePolicy.NormalPreference;
    private static readonly BindingFlags InstanceFlags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

    private static readonly ZoneDefinition[] ZoneDefinitions =
    [
        new("Asylum",                 StatusId: 1911, DataId: 0x659u, Radius: 8f),
        new("Earthly Star",           StatusId: 1224, DataId: 0x5B9u, Radius: 4f),
        new("Earthly Star",           StatusId: 1248, DataId: 0x5B9u, Radius: 4f),
        new("Collective Unconscious", StatusId: 849,  DataId: 0u,     Radius: 5f),
        new("Sacred Soil",            StatusId: 299,  DataId: 0x5D8u, Radius: 5f),
    ];

    private FieldInfo? goalZonesField;
    private FieldInfo? wposXField;
    private FieldInfo? wposZField;
    private Type? resolvedHintsType;
    private Type? resolvedWPosType;
    private string hookState = "unresolved";
    private string lastReason = "not evaluated";
    private bool lastInjected;
    private string lastZoneName = "<none>";
    private string lastCasterName = "<none>";
    private float lastDistanceToCenter;
    private Delegate? lastGoalDelegate;
    private SurvivabilityZoneGoalPlan? lastPlan;
    private SurvivabilityZoneOverlaySnapshot? lastOverlay;

    public SurvivabilityZonePositioningStatus Status => new(
        this.hookState,
        this.lastReason,
        this.lastInjected,
        this.lastZoneName,
        this.lastCasterName,
        this.lastDistanceToCenter);

    public SurvivabilityZoneOverlaySnapshot? Overlay => this.lastOverlay;

    public void SetHookState(string state)
    {
        this.hookState = state;
    }

    public void Reset()
    {
        this.goalZonesField = null;
        this.wposXField = null;
        this.wposZField = null;
        this.resolvedHintsType = null;
        this.resolvedWPosType = null;
        this.lastReason = "reset";
        this.lastInjected = false;
        this.lastZoneName = "<none>";
        this.lastCasterName = "<none>";
        this.lastDistanceToCenter = 0f;
        this.lastGoalDelegate = null;
        this.lastPlan = null;
        this.lastOverlay = null;
    }

    public void TryInjectGoal(object hints, ICollection<BossModGoalContribution> contributions)
    {
        this.lastInjected = false;
        this.lastOverlay = null;

        if (!config.Enabled || !config.ManageDefensiveGroundZonePositioning)
        {
            this.SetInactive("disabled");
            return;
        }

        if (!config.ManageMovement)
        {
            this.SetInactive("movement management disabled");
            return;
        }

        if (!services.Condition[ConditionFlag.InCombat] || services.Condition[ConditionFlag.Unconscious])
        {
            this.SetInactive("not active in combat");
            return;
        }

        if (automatedMovementSuppressed())
        {
            this.SetInactive("manual movement suppression active");
            return;
        }

        var player = services.ObjectTable.LocalPlayer;
        if (player == null)
        {
            this.SetInactive("local player unavailable");
            return;
        }

        if (!this.EnsureResolved(hints.GetType()))
        {
            return;
        }

        var plan = this.FindBestPlan(player);
        if (plan == null)
        {
            this.SetInactive("no active survivability zones");
            return;
        }

        var goalZones = this.goalZonesField!.GetValue(hints) as IList;
        if (goalZones == null)
        {
            this.SetInactive("BMR goal zone list unavailable");
            return;
        }

        if (this.lastGoalDelegate == null || this.lastPlan == null || !this.lastPlan.SameSource(plan))
        {
            this.lastGoalDelegate = plan.CreateGoalDelegate(this.resolvedWPosType!, this.wposXField!, this.wposZField!);
            this.lastPlan = plan;
        }

        contributions.Add(new(this.lastGoalDelegate, BossModGoalPriority.DefensiveMechanic, "Defensive zone"));
        this.lastInjected = true;
        this.lastZoneName = plan.ZoneName;
        this.lastCasterName = plan.CasterName;
        this.lastDistanceToCenter = plan.DistanceToCenter;
        this.lastOverlay = plan.CreateOverlay(player.Position.Y, injected: true);
        this.lastReason = plan.PlayerInZone ? $"holding inside {plan.ZoneName}" : $"goal injected toward {plan.ZoneName}";
    }

    public void RefreshOverlay()
    {
        if (!config.ShowDecisionOverlay)
        {
            this.lastOverlay = null;
            return;
        }

        var player = services.ObjectTable.LocalPlayer;
        if (player == null || services.Condition[ConditionFlag.Unconscious])
        {
            this.lastOverlay = null;
            return;
        }

        var plan = this.FindBestPlan(player);
        if (plan == null)
        {
            this.lastOverlay = null;
            return;
        }

        this.lastZoneName = plan.ZoneName;
        this.lastCasterName = plan.CasterName;
        this.lastDistanceToCenter = plan.DistanceToCenter;
        this.lastOverlay = plan.CreateOverlay(player.Position.Y, this.lastInjected);
    }

    private void SetInactive(string reason)
    {
        this.lastReason = reason;
        this.lastZoneName = "<none>";
        this.lastCasterName = "<none>";
        this.lastDistanceToCenter = 0f;
        this.lastGoalDelegate = null;
        this.lastPlan = null;
        this.lastOverlay = null;
    }

    private bool EnsureResolved(Type hintsType)
    {
        if (this.resolvedHintsType == hintsType &&
            this.goalZonesField != null &&
            this.wposXField != null &&
            this.wposZField != null)
        {
            return true;
        }

        var goalZones = hintsType.GetField("GoalZones", InstanceFlags);
        var wposType = hintsType.Assembly.GetType("BossMod.WPos");
        var xField = wposType?.GetField("X", InstanceFlags);
        var zField = wposType?.GetField("Z", InstanceFlags);
        if (goalZones == null || wposType == null || xField == null || zField == null)
        {
            this.lastReason = "BMR survivability zone reflection members unavailable";
            return false;
        }

        this.resolvedHintsType = hintsType;
        this.resolvedWPosType = wposType;
        this.goalZonesField = goalZones;
        this.wposXField = xField;
        this.wposZField = zField;
        return true;
    }

    private SurvivabilityZoneGoalPlan? FindBestPlan(IBattleChara player)
    {
        var playerPos = new Vector2(player.Position.X, player.Position.Z);
        SurvivabilityZoneGoalPlan? best = null;

        foreach (var caster in PartyAllyProvider.EnumerateVisiblePartyAllies(services, player))
        {

            foreach (var zone in ZoneDefinitions)
            {
                var matchingStatus = caster.StatusList.FirstOrDefault(s => s.StatusId == zone.StatusId && s.RemainingTime > 0f);
                if (matchingStatus == null)
                {
                    continue;
                }

                Vector2 center;
                Vector2 casterPos2 = new(caster.Position.X, caster.Position.Z);

                if (zone.DataId == 0u)
                {
                    // Aura-based: zone follows the caster (e.g. Collective Unconscious)
                    center = casterPos2;
                }
                else
                {
                    // Object-based: find the placed ground object owned by this caster
                    var obj = this.FindZoneObject(zone.DataId, caster.GameObjectId);
                    if (obj == null)
                    {
                        continue;
                    }

                    center = new Vector2(obj.Position.X, obj.Position.Z);
                }

                var distanceToCenter = Vector2.Distance(playerPos, center);
                var plan = new SurvivabilityZoneGoalPlan(
                    zone.Name,
                    caster.Name.TextValue,
                    center,
                    casterPos2,
                    caster.GameObjectId,
                    zone.Radius,
                    distanceToCenter,
                    distanceToCenter <= zone.Radius);

                if (best == null || plan.DistanceToCenter < best.DistanceToCenter)
                {
                    best = plan;
                }
            }
        }

        return best;
    }

    private IGameObject? FindZoneObject(uint dataId, ulong ownerObjectId)
    {
        foreach (var obj in services.ObjectTable)
        {
            if (obj.BaseId == dataId && obj.OwnerId == ownerObjectId)
            {
                return obj;
            }
        }

        return null;
    }

    private readonly record struct ZoneDefinition(string Name, uint StatusId, uint DataId, float Radius);

    private sealed class SurvivabilityZoneGoalPlan
    {
        private static readonly MethodInfo ScoreFromWPosMethod =
            typeof(SurvivabilityZoneGoalPlan).GetMethod(nameof(ScoreFromWPos), BindingFlags.Instance | BindingFlags.NonPublic)!;

        private readonly ulong casterId;
        private readonly string zoneName;
        private readonly string casterName;
        private readonly Vector2 center;
        private readonly Vector2 casterPosition;
        private readonly float radius;
        private readonly bool playerInZone;

        public SurvivabilityZoneGoalPlan(
            string zoneName,
            string casterName,
            Vector2 center,
            Vector2 casterPosition,
            ulong casterId,
            float radius,
            float distanceToCenter,
            bool playerInZone)
        {
            this.zoneName = zoneName;
            this.casterName = casterName;
            this.center = center;
            this.casterPosition = casterPosition;
            this.casterId = casterId;
            this.radius = radius;
            this.DistanceToCenter = distanceToCenter;
            this.playerInZone = playerInZone;
        }

        public string ZoneName => this.zoneName;
        public string CasterName => this.casterName;
        public float DistanceToCenter { get; }
        public bool PlayerInZone => this.playerInZone;

        public bool SameSource(SurvivabilityZoneGoalPlan other)
        {
            return this.casterId == other.casterId &&
                   string.Equals(this.zoneName, other.zoneName, StringComparison.Ordinal) &&
                   Vector2.DistanceSquared(this.center, other.center) <= 0.25f;
        }

        public Delegate CreateGoalDelegate(Type wposType, FieldInfo xField, FieldInfo zField)
        {
            var parameter = Expression.Parameter(wposType, "p");
            var call = Expression.Call(
                Expression.Constant(this),
                ScoreFromWPosMethod,
                Expression.Convert(Expression.Field(parameter, xField), typeof(float)),
                Expression.Convert(Expression.Field(parameter, zField), typeof(float)));
            var delegateType = typeof(Func<,>).MakeGenericType(wposType, typeof(float));
            return Expression.Lambda(delegateType, call, parameter).Compile();
        }

        public SurvivabilityZoneOverlaySnapshot CreateOverlay(float y, bool injected)
        {
            return new(
                new Vector3(this.center.X, y, this.center.Y),
                new Vector3(this.casterPosition.X, y, this.casterPosition.Y),
                this.radius,
                injected,
                this.playerInZone,
                this.zoneName,
                this.casterName);
        }

        private float ScoreFromWPos(float x, float z)
        {
            var distance = Vector2.Distance(new Vector2(x, z), this.center);
            if (distance > this.radius)
            {
                return 0f;
            }

            return distance <= NearCenterRadius ? NearCenterScore : InsideScore;
        }
    }
}

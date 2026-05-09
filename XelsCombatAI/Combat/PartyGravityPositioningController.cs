using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;
using System.Numerics;
using System.Reflection;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Game.ClientState.Objects.Types;
using XelsCombatAI.Integrations;

namespace XelsCombatAI.Combat;

internal sealed record PartyGravityPositioningStatus(
    string HookState,
    string LastReason,
    bool Injected,
    int PartyMembers,
    int PartyListMembers,
    int DutySupportMembers,
    int ClusterMembers,
    float DistanceToCluster);

internal sealed record PartyGravityOverlaySnapshot(
    Vector3 Center,
    float Radius,
    float PullRadius,
    bool Injected,
    float DistanceToCluster,
    IReadOnlyList<Vector3> Members);

internal sealed class PartyGravityPositioningController(
    Configuration config,
    DalamudServices services,
    BossModReflectionSafety bossModSafety,
    Func<bool> automatedMovementSuppressed,
    Func<bool> shouldDeferToMeleeUptime)
    : IBossModGoalZoneContributor
{
    private const float ClusterRadius = 8f;
    private const float MinPreferredRadius = 3f;
    private const float PreferredRadiusPadding = 8f;
    private const float PreferredScore = GoalZoneScorePolicy.PartyGravityPreference;
    private const double MinBossModIdleDelaySeconds = 1d;
    private const double MaxBossModIdleDelaySeconds = 3d;
    private static readonly BindingFlags InstanceFlags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

    private FieldInfo? goalZonesField;
    private FieldInfo? wposXField;
    private FieldInfo? wposZField;
    private Type? resolvedHintsType;
    private Type? resolvedWPosType;
    private string hookState = "unresolved";
    private string lastReason = "not evaluated";
    private bool lastInjected;
    private int lastPartyMembers;
    private int lastPartyListMembers;
    private int lastDutySupportMembers;
    private int lastClusterMembers;
    private float lastDistanceToCluster;
    private Delegate? lastGoalDelegate;
    private PartyGravityGoalPlan? lastPlan;
    private PartyGravityOverlaySnapshot? lastOverlay;
    private bool lastBossModMoveImminent;
    private bool receivedBossModMovementState;
    private DateTime partyGravityAllowedAfter = DateTime.MaxValue;

    public PartyGravityPositioningStatus Status => new(
        this.hookState,
        this.lastReason,
        this.lastInjected,
        this.lastPartyMembers,
        this.lastPartyListMembers,
        this.lastDutySupportMembers,
        this.lastClusterMembers,
        this.lastDistanceToCluster);

    public PartyGravityOverlaySnapshot? Overlay => this.lastOverlay;

    public void SetHookState(string state)
    {
        this.hookState = state;
    }

    public void SetBossModMovementState(bool moveRequested, bool moveImminent)
    {
        _ = moveRequested;
        var now = DateTime.UtcNow;
        this.receivedBossModMovementState = true;
        if (moveImminent)
        {
            this.lastBossModMoveImminent = true;
            this.partyGravityAllowedAfter = DateTime.MaxValue;
            return;
        }

        if (this.lastBossModMoveImminent || this.partyGravityAllowedAfter == DateTime.MaxValue)
        {
            this.partyGravityAllowedAfter = now.AddSeconds(MinBossModIdleDelaySeconds + (Random.Shared.NextDouble() * (MaxBossModIdleDelaySeconds - MinBossModIdleDelaySeconds)));
        }

        this.lastBossModMoveImminent = false;
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
        this.lastPartyMembers = 0;
        this.lastPartyListMembers = 0;
        this.lastDutySupportMembers = 0;
        this.lastClusterMembers = 0;
        this.lastDistanceToCluster = 0f;
        this.lastGoalDelegate = null;
        this.lastPlan = null;
        this.lastOverlay = null;
        this.lastBossModMoveImminent = false;
        this.receivedBossModMovementState = false;
        this.partyGravityAllowedAfter = DateTime.MaxValue;
    }

    public void TryInjectGoal(object hints, ICollection<BossModGoalContribution> contributions)
    {
        this.lastInjected = false;
        this.lastOverlay = null;

        if (!this.CanEvaluate(out var player, out var reason))
        {
            this.SetInactive(reason);
            return;
        }
        var localPlayer = player!;

        if (!this.EnsureResolved(hints.GetType()))
        {
            return;
        }

        var plan = this.FindBestPlan(localPlayer);
        if (plan == null)
        {
            this.SetInactive("not enough visible party members");
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

        contributions.Add(new(this.lastGoalDelegate, BossModGoalPriority.Convenience, "Party gravity"));
        this.lastInjected = true;
        this.lastPartyMembers = plan.PartyMemberCount;
        this.lastPartyListMembers = plan.PartyListMemberCount;
        this.lastDutySupportMembers = plan.DutySupportMemberCount;
        this.lastClusterMembers = plan.ClusterMemberCount;
        this.lastDistanceToCluster = plan.DistanceToCluster;
        this.lastOverlay = plan.CreateOverlay(localPlayer.Position.Y, injected: true);
        this.lastReason = plan.DistanceToCluster <= plan.PreferredRadius ? "holding near party" : "goal injected";
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

        this.lastPartyMembers = plan.PartyMemberCount;
        this.lastPartyListMembers = plan.PartyListMemberCount;
        this.lastDutySupportMembers = plan.DutySupportMemberCount;
        this.lastClusterMembers = plan.ClusterMemberCount;
        this.lastDistanceToCluster = plan.DistanceToCluster;
        this.lastOverlay = plan.CreateOverlay(player.Position.Y, this.lastInjected);
    }

    private bool CanEvaluate(out IBattleChara? player, out string reason)
    {
        player = null;
        if (!config.Enabled || !config.ManagePartyGravityPositioning)
        {
            reason = "disabled";
            return false;
        }

        if (!config.ManageMovement)
        {
            reason = "movement management disabled";
            return false;
        }

        if (!services.Condition[ConditionFlag.InCombat] || services.Condition[ConditionFlag.Unconscious])
        {
            reason = "not active in combat";
            return false;
        }

        if (automatedMovementSuppressed())
        {
            reason = "manual movement suppression active";
            return false;
        }

        if (shouldDeferToMeleeUptime())
        {
            reason = "deferred to melee target uptime";
            return false;
        }

        player = services.ObjectTable.LocalPlayer;
        if (player == null)
        {
            reason = "local player unavailable";
            return false;
        }

        var isMagicRanged = JobRoles.GetRangeRole(player) == RangeRole.MagicRanged;
        if (isMagicRanged && this.ShouldDeferForCasterUptime(player))
        {
            reason = "deferred for caster uptime";
            return false;
        }

        if (!isMagicRanged && !this.BossModIdleWindowElapsed(out reason))
        {
            return false;
        }

        reason = string.Empty;
        return true;
    }

    private bool ShouldDeferForCasterUptime(IBattleChara player)
    {
        if (JobRoles.GetRangeRole(player) != RangeRole.MagicRanged ||
            !this.HasUsableRangedTarget(player))
        {
            return false;
        }

        return !this.AlreadyNeedsMovement(player);
    }

    private bool BossModIdleWindowElapsed(out string reason)
    {
        if (!this.receivedBossModMovementState)
        {
            reason = "waiting for BMR movement state";
            return false;
        }

        if (this.lastBossModMoveImminent)
        {
            reason = "deferred while BMR movement active";
            return false;
        }

        var now = DateTime.UtcNow;
        if (now < this.partyGravityAllowedAfter)
        {
            reason = $"waiting for BMR movement idle ({Math.Max(0d, (this.partyGravityAllowedAfter - now).TotalSeconds):0.0}s)";
            return false;
        }

        reason = string.Empty;
        return true;
    }

    private bool HasUsableRangedTarget(IBattleChara player)
    {
        if (services.TargetManager.Target is not IBattleChara target ||
            target.IsDead ||
            target.GameObjectId == player.GameObjectId)
        {
            return false;
        }

        return Geometry.DistanceToHitbox(
            player.Position,
            player.HitboxRadius,
            target.Position,
            target.HitboxRadius) <= Configuration.InternalRangedUptimeRange;
    }

    private bool AlreadyNeedsMovement(IBattleChara player)
    {
        if (bossModSafety.TryIsPositionSafe(player.Position, out var currentSafe, out _) && !currentSafe)
        {
            return true;
        }

        return bossModSafety.TryGetSafeMovementIntent(player.Position, out _, out _);
    }

    private void SetInactive(string reason)
    {
        this.lastReason = reason;
        this.lastInjected = false;
        this.lastPartyMembers = 0;
        this.lastPartyListMembers = 0;
        this.lastDutySupportMembers = 0;
        this.lastClusterMembers = 0;
        this.lastDistanceToCluster = 0f;
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
            this.lastReason = $"BMR party gravity reflection members unavailable: {FormatMissing(
                (goalZones == null, "AIHints.GoalZones"),
                (wposType == null, "BossMod.WPos"),
                (xField == null, "BossMod.WPos.X"),
                (zField == null, "BossMod.WPos.Z"))}";
            return false;
        }

        this.resolvedHintsType = hintsType;
        this.resolvedWPosType = wposType;
        this.goalZonesField = goalZones;
        this.wposXField = xField;
        this.wposZField = zField;
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

    private PartyGravityGoalPlan? FindBestPlan(IBattleChara player)
    {
        var snapshot = PartyAllyProvider.GetVisiblePartyAllies(services, player);
        var members = snapshot.Members.ToArray();
        if (members.Length < 2)
        {
            this.lastPartyMembers = members.Length;
            this.lastPartyListMembers = snapshot.PartyListMembers;
            this.lastDutySupportMembers = snapshot.DutySupportMembers;
            return null;
        }

        ClusterCandidate? best = null;
        foreach (var seed in members)
        {
            var seedPosition = new Vector2(seed.Position.X, seed.Position.Z);
            var cluster = members
                .Where(member => Vector2.Distance(seedPosition, new Vector2(member.Position.X, member.Position.Z)) <= ClusterRadius)
                .ToArray();
            var center = new Vector2(cluster.Average(member => member.Position.X), cluster.Average(member => member.Position.Z));
            var distanceToPlayer = Vector2.Distance(new Vector2(player.Position.X, player.Position.Z), center);
            var averageDistanceToCenter = cluster.Average(member => Vector2.Distance(new Vector2(member.Position.X, member.Position.Z), center));
            var candidate = new ClusterCandidate(cluster, center, distanceToPlayer, averageDistanceToCenter);
            if (best == null ||
                candidate.Members.Length > best.Value.Members.Length ||
                (candidate.Members.Length == best.Value.Members.Length && candidate.AverageDistanceToCenter < best.Value.AverageDistanceToCenter) ||
                (candidate.Members.Length == best.Value.Members.Length &&
                 MathF.Abs(candidate.AverageDistanceToCenter - best.Value.AverageDistanceToCenter) <= 0.1f &&
                 candidate.DistanceToPlayer < best.Value.DistanceToPlayer))
            {
                best = candidate;
            }
        }

        if (best == null || best.Value.Members.Length < 2)
        {
            return null;
        }

        var partySpread = MathF.Max(MinPreferredRadius, best.Value.AverageDistanceToCenter);
        var preferredRadius = partySpread + PreferredRadiusPadding;
        return new PartyGravityGoalPlan(
            best.Value.Center,
            best.Value.Members.Select(member => new Vector2(member.Position.X, member.Position.Z)).ToArray(),
            members.Length,
            snapshot.PartyListMembers,
            snapshot.DutySupportMembers,
            best.Value.DistanceToPlayer,
            preferredRadius,
            partySpread);
    }

    private readonly record struct ClusterCandidate(IBattleChara[] Members, Vector2 Center, float DistanceToPlayer, float AverageDistanceToCenter);

    private sealed class PartyGravityGoalPlan(
        Vector2 center,
        Vector2[] members,
        int partyMemberCount,
        int partyListMemberCount,
        int dutySupportMemberCount,
        float distanceToCluster,
        float preferredRadius,
        float overlayRadius)
    {
        private static readonly MethodInfo ScoreFromWPosMethod = typeof(PartyGravityGoalPlan).GetMethod(nameof(ScoreFromWPos), BindingFlags.Instance | BindingFlags.NonPublic)!;
        private readonly Vector2 center = center;
        private readonly Vector2[] members = members;
        private readonly float preferredRadius = preferredRadius;

        public int PartyMemberCount => partyMemberCount;
        public int PartyListMemberCount => partyListMemberCount;
        public int DutySupportMemberCount => dutySupportMemberCount;
        public int ClusterMemberCount => this.members.Length;
        public float DistanceToCluster => distanceToCluster;
        public float PreferredRadius => overlayRadius;

        public bool SameSource(PartyGravityGoalPlan other)
        {
            return Vector2.DistanceSquared(this.center, other.center) <= 1f &&
                   this.members.Length == other.members.Length;
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

        public PartyGravityOverlaySnapshot CreateOverlay(float y, bool injected)
        {
            return new(
                new Vector3(this.center.X, y, this.center.Y),
                overlayRadius,
                preferredRadius,
                injected,
                distanceToCluster,
                this.members.Select(member => new Vector3(member.X, y, member.Y)).ToArray());
        }

        private float ScoreFromWPos(float x, float z)
        {
            var distance = Vector2.Distance(new Vector2(x, z), this.center);
            return distance <= this.preferredRadius ? PreferredScore : 0f;
        }
    }
}

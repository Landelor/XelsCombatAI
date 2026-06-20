using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Linq.Expressions;
using System.Numerics;
using System.Reflection;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Game.ClientState.Objects.Enums;
using Dalamud.Game.ClientState.Objects.Types;
using Dalamud.Game.ClientState.Statuses;
using ECommons.GameFunctions;
using ECommons.Hooks;
using ECommons.Hooks.ActionEffectTypes;
using XelsCombatAI.Game;
using XelsCombatAI.Integrations;

namespace XelsCombatAI.Combat;

internal sealed record SurvivabilityZonePositioningStatus(
    string HookState,
    string LastReason,
    bool Injected,
    string ZoneName,
    string CasterName,
    float DistanceToCenter,
    string Diagnostics,
    Vector3? ZoneCenter,
    Vector3? CasterPosition);

internal sealed record SurvivabilityZoneOverlaySnapshot(
    Vector3 ZoneCenter,
    Vector3 CasterPosition,
    Vector3 PreferredEntryPosition,
    float Radius,
    bool Injected,
    bool PlayerInZone,
    bool PlayerSatisfiesPlan,
    bool TargetRangeConstrained,
    string ZoneName,
    string CasterName);

internal enum DefensiveGroundZoneMoveMode
{
    None,
    DamagePressure,
    PhysicalRangedComfort
}

internal sealed class SurvivabilityZonePositioningController : IBossModGoalZoneContributor, IDisposable
{
    private const float PreferredEntryRadius = 1.5f;
    private const float ZoneEntryMargin = 1.5f;
    private const float TargetRangeSlack = 0.2f;
    private const float CircleTolerance = 0.01f;
    private const float PreferredEntryScore = GoalZoneScorePolicy.StrongPreference;
    private const float InsideScore = GoalZoneScorePolicy.NormalPreference;
    private const float LowHealthThreshold = 0.60f;
    private static readonly TimeSpan CachedZoneGrace = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan OverlayRefreshInterval = TimeSpan.FromMilliseconds(250);
    private static readonly BindingFlags InstanceFlags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

    private static readonly ZoneDefinition[] ZoneDefinitions =
    [
        new("Asylum",                 StatusIds: [739, 1911, 1912], DataId: 0x659u, Radius: 15f, ActionId: 3569, Duration: TimeSpanDefaults.Asylum),
        new("Earthly Star",           StatusIds: [1224, 1248], DataId: 0x5B9u, Radius: 20f, ActionId: 7439, Duration: TimeSpanDefaults.EarthlyStar),
        new("Collective Unconscious", StatusIds: [847, 848],   DataId: 0u,     Radius: 8f, Mode: ZoneDetectionMode.CasterAura),
        new("Sacred Soil",            StatusIds: [298, 299, 1944, 2637, 2638], DataId: 0x5D8u, Radius: 15f, ActionId: 188, Duration: TimeSpanDefaults.SacredSoil),
    ];

    private readonly Configuration config;
    private readonly DalamudServices services;
    private readonly JobRangeProvider jobRangeProvider;
    private readonly Func<bool> automatedMovementSuppressed;
    private readonly Func<BossModMechanicPressure> mechanicPressure;
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
    private string lastDiagnostics = "not evaluated";
    private Delegate? lastGoalDelegate;
    private SurvivabilityZoneGoalPlan? lastPlan;
    private SurvivabilityZoneOverlaySnapshot? lastOverlay;
    private DateTime nextOverlayRefresh = DateTime.MinValue;
    private readonly List<CachedPlacedZone> cachedPlacedZones = [];

    public SurvivabilityZonePositioningController(
        Configuration config,
        DalamudServices services,
        JobRangeProvider jobRangeProvider,
        Func<bool> automatedMovementSuppressed,
        Func<BossModMechanicPressure> mechanicPressure)
    {
        this.config = config;
        this.services = services;
        this.jobRangeProvider = jobRangeProvider;
        this.automatedMovementSuppressed = automatedMovementSuppressed;
        this.mechanicPressure = mechanicPressure;
        ActionEffect.ActionEffectEvent += this.OnActionEffect;
    }

    public SurvivabilityZonePositioningStatus Status => new(
        this.hookState,
        this.lastReason,
        this.lastInjected,
        this.lastZoneName,
        this.lastCasterName,
        this.lastDistanceToCenter,
        this.lastDiagnostics,
        this.lastOverlay?.ZoneCenter,
        this.lastOverlay?.CasterPosition);

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
        this.lastDiagnostics = "reset";
        this.lastGoalDelegate = null;
        this.lastPlan = null;
        this.lastOverlay = null;
        this.nextOverlayRefresh = DateTime.MinValue;
        this.cachedPlacedZones.Clear();
    }

    public void Dispose()
    {
        ActionEffect.ActionEffectEvent -= this.OnActionEffect;
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

        if (!CombatEngagementDetector.IsEffectivelyInCombat(services) || services.Condition[ConditionFlag.Unconscious])
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

        if (ShouldSkipDefensiveZoneMovementForRole(player.ClassJob.RowId))
        {
            this.SetInactive("tank role skips defensive zone movement");
            return;
        }

        var pressure = this.mechanicPressure();
        var moveMode = this.ResolveDefensiveGroundZoneMoveMode(player, pressure);
        if (moveMode == DefensiveGroundZoneMoveMode.None)
        {
            this.SetInactive("no defensive damage pressure");
            return;
        }

        TargetRangeConstraint? targetRangeConstraint = null;
        if (moveMode == DefensiveGroundZoneMoveMode.PhysicalRangedComfort &&
            !this.TryCreatePhysicalRangedTargetRangeConstraint(player, out targetRangeConstraint, out var targetRangeReason))
        {
            this.SetInactive(targetRangeReason);
            return;
        }

        if (!this.EnsureResolved(hints.GetType()))
        {
            return;
        }

        var plan = this.FindBestPlan(player, targetRangeConstraint);
        if (plan == null)
        {
            this.SetInactive(targetRangeConstraint.HasValue
                ? "no active survivability zones within target range"
                : "no active survivability zones");
            return;
        }

        var goalZones = this.goalZonesField!.GetValue(hints) as IList;
        if (goalZones == null)
        {
            this.SetInactive("BMR goal zone list unavailable");
            return;
        }

        var previousPlan = this.lastPlan;
        this.lastZoneName = plan.ZoneName;
        this.lastCasterName = plan.CasterName;
        this.lastDistanceToCenter = plan.DistanceToCenter;
        this.lastPlan = plan;

        if (plan.PlayerSatisfiesPlan)
        {
            this.lastGoalDelegate = null;
            this.lastInjected = false;
            this.lastOverlay = plan.CreateOverlay(player.Position.Y, injected: false);
            this.nextOverlayRefresh = DateTime.UtcNow.Add(OverlayRefreshInterval);
            this.lastReason = targetRangeConstraint.HasValue
                ? $"holding inside {plan.ZoneName} within target range"
                : $"holding inside {plan.ZoneName}";
            return;
        }

        if (this.lastGoalDelegate == null || previousPlan == null || !previousPlan.SameSource(plan))
        {
            this.lastGoalDelegate = plan.CreateGoalDelegate(this.resolvedWPosType!, this.wposXField!, this.wposZField!);
        }

        contributions.Add(new(
            this.lastGoalDelegate,
            ResolveGoalPriority(moveMode),
            "Defensive zone",
            plan.PreferredEntryPosition,
            ResolveGoalConfidence(moveMode)));
        this.lastInjected = true;
        this.lastOverlay = plan.CreateOverlay(player.Position.Y, injected: true);
        this.nextOverlayRefresh = DateTime.UtcNow.Add(OverlayRefreshInterval);
        this.lastReason = moveMode == DefensiveGroundZoneMoveMode.PhysicalRangedComfort
            ? $"physical ranged comfort toward {plan.ZoneName}"
            : $"goal injected toward {plan.ZoneName}";
    }

    public void RefreshOverlay()
    {
        if (!config.ShowDecisionOverlay)
        {
            this.lastOverlay = null;
            this.nextOverlayRefresh = DateTime.MinValue;
            return;
        }

        var player = services.ObjectTable.LocalPlayer;
        if (player == null || services.Condition[ConditionFlag.Unconscious])
        {
            this.lastOverlay = null;
            this.nextOverlayRefresh = DateTime.MinValue;
            return;
        }

        if (ShouldSkipDefensiveZoneMovementForRole(player.ClassJob.RowId))
        {
            this.lastOverlay = null;
            this.nextOverlayRefresh = DateTime.MinValue;
            return;
        }

        var moveMode = this.ResolveDefensiveGroundZoneMoveMode(player, this.mechanicPressure());
        if (moveMode == DefensiveGroundZoneMoveMode.None)
        {
            this.lastOverlay = null;
            this.nextOverlayRefresh = DateTime.MinValue;
            return;
        }

        TargetRangeConstraint? targetRangeConstraint = null;
        if (moveMode == DefensiveGroundZoneMoveMode.PhysicalRangedComfort &&
            !this.TryCreatePhysicalRangedTargetRangeConstraint(player, out targetRangeConstraint, out _))
        {
            this.lastOverlay = null;
            this.nextOverlayRefresh = DateTime.MinValue;
            return;
        }

        var now = DateTime.UtcNow;
        if (now < this.nextOverlayRefresh)
        {
            return;
        }

        this.nextOverlayRefresh = now.Add(OverlayRefreshInterval);
        var plan = this.FindBestPlan(player, targetRangeConstraint);
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
        this.lastDiagnostics = reason;
        this.lastGoalDelegate = null;
        this.lastPlan = null;
        this.lastOverlay = null;
        this.nextOverlayRefresh = DateTime.MinValue;
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
            this.lastReason = $"BMR survivability zone reflection members unavailable: {FormatMissing(
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

    private SurvivabilityZoneGoalPlan? FindBestPlan(IBattleChara player, TargetRangeConstraint? targetRangeConstraint = null)
    {
        var playerPos = new Vector2(player.Position.X, player.Position.Z);
        var partyAllies = PartyAllyProvider.EnumerateVisiblePartyAllies(services, player).ToList();
        var friendlyActors = new List<IBattleChara>(partyAllies.Count + 1) { player };
        friendlyActors.AddRange(partyAllies);
        var friendlyIds = BuildFriendlyIds(friendlyActors);
        var diagnostics = new ZoneDiagnostics(playerPos);
        SurvivabilityZoneGoalPlan? best = null;
        var matchedPlacedObject = false;

        this.PruneCachedZones();

        foreach (var zone in ZoneDefinitions.Where(zone => zone.Mode == ZoneDetectionMode.PlacedObject))
        {
            var matchedDirectObject = false;
            foreach (var obj in this.FindZoneObjects(zone.DataId))
            {
                matchedDirectObject = true;
                matchedPlacedObject = true;
                var plan = this.CreateObjectPlan(zone, obj, partyAllies, playerPos, diagnostics, "object", targetRangeConstraint);
                best = SelectBest(best, plan);
            }

            if (matchedDirectObject)
            {
                continue;
            }

            if (this.TryGetCachedZonePlan(zone, friendlyActors, friendlyIds, playerPos, diagnostics, targetRangeConstraint, out var cachedPlan))
            {
                best = SelectBest(best, cachedPlan);
                continue;
            }

            foreach (var actor in friendlyActors)
            {
                foreach (var status in actor.StatusList)
                {
                    if (status == null || status.RemainingTime <= 0f || !zone.StatusIds.Contains(status.StatusId))
                    {
                        continue;
                    }

                    var source = status.SourceObject;
                    if (source != null && IsPlacedZoneSource(source))
                    {
                        var plan = this.CreateObjectPlan(zone, source, partyAllies, playerPos, diagnostics, $"status {status.StatusId} source", targetRangeConstraint);
                        best = SelectBest(best, plan);
                    }
                    else
                    {
                        diagnostics.AddRejectedStatus(zone, actor, status);
                    }
                }
            }
        }

        foreach (var actor in friendlyActors)
        {
            foreach (var zone in ZoneDefinitions.Where(zone => zone.Mode == ZoneDetectionMode.CasterAura))
            {
                if (!HasAnyStatus(actor, zone.StatusIds))
                {
                    continue;
                }

                var actorPos = new Vector2(actor.Position.X, actor.Position.Z);
                var distanceToCenter = Vector2.Distance(playerPos, actorPos);
                var plan = SurvivabilityZoneGoalPlan.TryCreate(
                    zone.Name,
                    actor.Name.TextValue,
                    actorPos,
                    actorPos,
                    actor.GameObjectId,
                    zone.Radius,
                    playerPos,
                    distanceToCenter,
                    distanceToCenter <= zone.Radius,
                    targetRangeConstraint);

                diagnostics.AddAuraMatch(zone, actor, distanceToCenter);
                best = SelectBest(best, plan);
            }
        }

        if (!matchedPlacedObject)
        {
            diagnostics.AddNearbyCandidates(services.ObjectTable);
        }

        this.lastDiagnostics = diagnostics.Build();
        return best;
    }

    private void OnActionEffect(ActionEffectSet set)
    {
        try
        {
            var zone = ZoneDefinitions.FirstOrDefault(z => z.Mode == ZoneDetectionMode.PlacedObject && z.ActionId == set.Header.ActionID);
            if (zone.ActionId == 0 || set.Position == default)
            {
                return;
            }

            var source = set.Source;
            var now = DateTime.UtcNow;
            this.cachedPlacedZones.RemoveAll(cached =>
                cached.Zone.ActionId == zone.ActionId &&
                (cached.SourceGameObjectId != 0 && cached.SourceGameObjectId == (source?.GameObjectId ?? 0) ||
                 cached.SourceEntityId != 0 && cached.SourceEntityId == (source?.EntityId ?? 0)));
            this.cachedPlacedZones.Add(new CachedPlacedZone(
                zone,
                set.Position,
                source?.GameObjectId ?? 0,
                source?.EntityId ?? set.Source?.GameObjectId ?? 0,
                source?.Name.TextValue ?? "<unknown>",
                now,
                now.Add(zone.Duration).Add(CachedZoneGrace)));
        }
        catch (Exception ex)
        {
            services.Log.Verbose($"Survivability zone action-effect tracking failed: {ex.Message}");
        }
    }

    private SurvivabilityZoneGoalPlan? CreateObjectPlan(
        ZoneDefinition zone,
        IGameObject obj,
        IReadOnlyCollection<IBattleChara> partyAllies,
        Vector2 playerPos,
        ZoneDiagnostics diagnostics,
        string matchKind,
        TargetRangeConstraint? targetRangeConstraint)
    {
        var center = new Vector2(obj.Position.X, obj.Position.Z);
        var caster = FindCaster(partyAllies, obj.OwnerId);
        var casterPos = caster != null ? new Vector2(caster.Position.X, caster.Position.Z) : center;
        var casterId = caster?.GameObjectId ?? obj.GameObjectId;
        var casterName = caster?.Name.TextValue ?? "<unknown>";
        var distanceToCenter = Vector2.Distance(playerPos, center);
        diagnostics.AddObjectMatch(zone, obj, distanceToCenter, matchKind);
        return SurvivabilityZoneGoalPlan.TryCreate(
            zone.Name,
            casterName,
            center,
            casterPos,
            casterId,
            zone.Radius,
            playerPos,
            distanceToCenter,
            distanceToCenter <= zone.Radius,
            targetRangeConstraint);
    }

    private IEnumerable<IGameObject> FindZoneObjects(uint dataId)
    {
        foreach (var obj in services.ObjectTable)
        {
            if (obj.BaseId == dataId)
            {
                yield return obj;
            }
        }
    }

    private bool TryGetCachedZonePlan(
        ZoneDefinition zone,
        IReadOnlyCollection<IBattleChara> friendlyActors,
        IReadOnlySet<ulong> friendlyIds,
        Vector2 playerPos,
        ZoneDiagnostics diagnostics,
        TargetRangeConstraint? targetRangeConstraint,
        [NotNullWhen(true)] out SurvivabilityZoneGoalPlan? plan)
    {
        plan = null;
        if (zone.ActionId == 0)
        {
            return false;
        }

        SurvivabilityZoneGoalPlan? best = null;
        foreach (var cached in this.cachedPlacedZones)
        {
            if (cached.Zone.ActionId != zone.ActionId ||
                cached.ExpiresAtUtc <= DateTime.UtcNow ||
                (!friendlyIds.Contains(cached.SourceGameObjectId) && !friendlyIds.Contains(cached.SourceEntityId)))
            {
                continue;
            }

            var center = new Vector2(cached.Center.X, cached.Center.Z);
            var caster = FindCaster(friendlyActors, cached.SourceGameObjectId) ?? FindCaster(friendlyActors, cached.SourceEntityId);
            var casterPos = caster != null ? new Vector2(caster.Position.X, caster.Position.Z) : center;
            var casterId = caster?.GameObjectId ?? cached.SourceGameObjectId;
            var casterName = caster?.Name.TextValue ?? cached.SourceName;
            var distanceToCenter = Vector2.Distance(playerPos, center);
            diagnostics.AddCachedMatch(cached, distanceToCenter);
            var candidate = SurvivabilityZoneGoalPlan.TryCreate(
                zone.Name,
                casterName,
                center,
                casterPos,
                casterId,
                zone.Radius,
                playerPos,
                distanceToCenter,
                distanceToCenter <= zone.Radius,
                targetRangeConstraint);
            best = SelectBest(best, candidate);
        }

        if (best == null)
        {
            return false;
        }

        plan = best;
        return true;
    }

    private static IBattleChara? FindCaster(IEnumerable<IBattleChara> partyAllies, ulong ownerId)
    {
        return ownerId == 0 ? null : partyAllies.FirstOrDefault(ally => ally.GameObjectId == ownerId || ally.EntityId == ownerId);
    }

    private static bool HasAnyStatus(IBattleChara actor, IReadOnlyCollection<uint> statusIds)
    {
        return actor.StatusList.Any(status => status.RemainingTime > 0f && statusIds.Contains(status.StatusId));
    }

    private static HashSet<ulong> BuildFriendlyIds(IEnumerable<IBattleChara> actors)
    {
        var ids = new HashSet<ulong>();
        foreach (var actor in actors)
        {
            ids.Add(actor.GameObjectId);
            ids.Add(actor.EntityId);
        }

        return ids;
    }

    private void PruneCachedZones()
    {
        var now = DateTime.UtcNow;
        this.cachedPlacedZones.RemoveAll(entry => entry.ExpiresAtUtc <= now);
    }

    private static SurvivabilityZoneGoalPlan? SelectBest(SurvivabilityZoneGoalPlan? current, SurvivabilityZoneGoalPlan? candidate)
    {
        if (candidate == null)
        {
            return current;
        }

        return current == null || candidate.DistanceToCenter < current.DistanceToCenter ? candidate : current;
    }

    internal static bool ShouldSkipDefensiveZoneMovementForRole(uint classJobId)
    {
        return JobRoles.IsTankJob(classJobId);
    }

    private DefensiveGroundZoneMoveMode ResolveDefensiveGroundZoneMoveMode(IBattleChara player, BossModMechanicPressure pressure)
    {
        return ResolveDefensiveGroundZoneMoveMode(player.ClassJob.RowId, pressure, this.IsPlayerTargetedByHostile(player), IsLowHealth(player));
    }

    internal static bool ShouldMoveForDefensiveGroundZone(IBattleChara player, BossModMechanicPressure pressure, bool playerHasPersonalThreat)
    {
        return ResolveDefensiveGroundZoneMoveMode(player.ClassJob.RowId, pressure, playerHasPersonalThreat, IsLowHealth(player)) != DefensiveGroundZoneMoveMode.None;
    }

    internal static bool ShouldMoveForDefensiveGroundZone(uint classJobId, BossModMechanicPressure pressure, bool playerHasPersonalThreat, bool playerLowHealth)
    {
        return ResolveDefensiveGroundZoneMoveMode(classJobId, pressure, playerHasPersonalThreat, playerLowHealth) != DefensiveGroundZoneMoveMode.None;
    }

    internal static DefensiveGroundZoneMoveMode ResolveDefensiveGroundZoneMoveMode(uint classJobId, BossModMechanicPressure pressure, bool playerHasPersonalThreat, bool playerLowHealth)
    {
        if (HasDefensiveDamagePressure(pressure, playerHasPersonalThreat, playerLowHealth))
        {
            return DefensiveGroundZoneMoveMode.DamagePressure;
        }

        return JobRoles.GetRangeRole(classJobId) == RangeRole.PhysicalRanged
            ? DefensiveGroundZoneMoveMode.PhysicalRangedComfort
            : DefensiveGroundZoneMoveMode.None;
    }

    private static bool HasDefensiveDamagePressure(BossModMechanicPressure pressure, bool playerHasPersonalThreat, bool playerLowHealth)
    {
        var personalHeavyDamageSoon = pressure.TankbusterSoon ||
                                      (pressure.NextDamageType == BossModPredictedDamageType.Tankbuster && pressure.DamageSoon);
        return pressure.RaidwideSoon ||
               pressure.SharedDamageSoon ||
               (personalHeavyDamageSoon && playerHasPersonalThreat) ||
               playerLowHealth;
    }

    private static BossModGoalPriority ResolveGoalPriority(DefensiveGroundZoneMoveMode moveMode)
    {
        return moveMode == DefensiveGroundZoneMoveMode.PhysicalRangedComfort
            ? BossModGoalPriority.PartyUtility
            : BossModGoalPriority.DefensiveMechanic;
    }

    private static MechanicWhisperConfidence ResolveGoalConfidence(DefensiveGroundZoneMoveMode moveMode)
    {
        return moveMode == DefensiveGroundZoneMoveMode.PhysicalRangedComfort
            ? MechanicWhisperConfidence.Routine
            : MechanicWhisperConfidence.Confident;
    }

    private bool TryCreatePhysicalRangedTargetRangeConstraint(
        IBattleChara player,
        out TargetRangeConstraint? constraint,
        out string reason)
    {
        constraint = null;
        if (services.TargetManager.Target is not IBattleNpc target ||
            target.BattleNpcKind != BattleNpcSubKind.Combatant ||
            target.GameObjectId == 0 ||
            target.IsDead ||
            target.CurrentHp == 0 ||
            !target.StatusFlags.HasFlag(StatusFlags.Hostile))
        {
            reason = "no attackable target for physical ranged defensive zone";
            return false;
        }

        var range = jobRangeProvider.EngagementRange;
        if (range <= 0f || range >= Configuration.InternalDisabledUptimeRange - 0.5f)
        {
            reason = "target range unavailable for physical ranged defensive zone";
            return false;
        }

        var radius = target.HitboxRadius + player.HitboxRadius + range + TargetRangeSlack;
        constraint = new TargetRangeConstraint(new Vector2(target.Position.X, target.Position.Z), radius);
        reason = string.Empty;
        return true;
    }

    private bool IsPlayerTargetedByHostile(IBattleChara player)
    {
        foreach (var actor in this.services.ObjectTable.OfType<IBattleChara>())
        {
            if (actor.TargetObjectId == player.GameObjectId &&
                actor.CurrentHp > 0 &&
                !actor.IsDead &&
                actor.IsHostile())
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsLowHealth(IBattleChara player)
    {
        if (player.MaxHp == 0)
        {
            return false;
        }

        return (float)player.CurrentHp / player.MaxHp <= LowHealthThreshold;
    }

    private static bool IsPlacedZoneSource(IGameObject obj)
    {
        return obj is not IBattleChara &&
               obj.ObjectKind is not ObjectKind.Pc and not ObjectKind.Companion and not ObjectKind.Mount;
    }

    private enum ZoneDetectionMode
    {
        PlacedObject,
        CasterAura,
    }

    private readonly record struct ZoneDefinition(
        string Name,
        IReadOnlyCollection<uint> StatusIds,
        uint DataId,
        float Radius,
        uint ActionId = 0,
        TimeSpan Duration = default,
        ZoneDetectionMode Mode = ZoneDetectionMode.PlacedObject);

    private readonly record struct CachedPlacedZone(
        ZoneDefinition Zone,
        Vector3 Center,
        ulong SourceGameObjectId,
        ulong SourceEntityId,
        string SourceName,
        DateTime CreatedAtUtc,
        DateTime ExpiresAtUtc);

    private readonly record struct TargetRangeConstraint(Vector2 Center, float Radius)
    {
        public bool Contains(Vector2 point)
        {
            return Vector2.DistanceSquared(point, this.Center) <= this.Radius * this.Radius;
        }
    }

    private static class TimeSpanDefaults
    {
        public static readonly TimeSpan Asylum = TimeSpan.FromSeconds(24);
        public static readonly TimeSpan EarthlyStar = TimeSpan.FromSeconds(20);
        public static readonly TimeSpan SacredSoil = TimeSpan.FromSeconds(15);
    }

    private sealed class ZoneDiagnostics
    {
        private const int MaxEntries = 8;
        private const float CandidateRadius = 35f;
        private readonly Vector2 playerPos;
        private readonly List<string> objectMatches = [];
        private readonly List<string> rejectedStatuses = [];
        private readonly List<string> auraMatches = [];
        private readonly List<string> nearbyCandidates = [];

        public ZoneDiagnostics(Vector2 playerPos)
        {
            this.playerPos = playerPos;
        }

        public void AddObjectMatch(ZoneDefinition zone, IGameObject obj, float distance, string matchKind)
        {
            if (this.objectMatches.Count >= MaxEntries)
            {
                return;
            }

            this.objectMatches.Add($"{matchKind}:{zone.Name} {FormatObject(obj)} dist={distance:0.0} pos={FormatPos(obj.Position)}");
        }

        public void AddCachedMatch(CachedPlacedZone cached, float distance)
        {
            if (this.objectMatches.Count >= MaxEntries)
            {
                return;
            }

            this.objectMatches.Add($"action:{cached.Zone.Name} action={cached.Zone.ActionId} source={cached.SourceName} sourceId=0x{cached.SourceGameObjectId:X}/0x{cached.SourceEntityId:X} dist={distance:0.0} pos={FormatPos(cached.Center)} expires={cached.ExpiresAtUtc:O}");
        }

        public void AddRejectedStatus(ZoneDefinition zone, IBattleChara actor, IStatus status)
        {
            if (this.rejectedStatuses.Count >= MaxEntries)
            {
                return;
            }

            IGameObject? source = status.SourceObject;
            var sourceText = source == null ? "source=<none>" : $"source={FormatObject(source)} pos={FormatPos(source.Position)}";
            this.rejectedStatuses.Add($"rejected:{zone.Name} actor={actor.Name.TextValue} status={status.StatusId} sourceId=0x{status.SourceId:X} {sourceText}");
        }

        public void AddAuraMatch(ZoneDefinition zone, IBattleChara actor, float distance)
        {
            if (this.auraMatches.Count >= MaxEntries)
            {
                return;
            }

            this.auraMatches.Add($"aura:{zone.Name} actor={actor.Name.TextValue} id=0x{actor.GameObjectId:X} dist={distance:0.0}");
        }

        public void AddNearbyCandidates(IEnumerable<IGameObject> objects)
        {
            foreach (var obj in objects)
            {
                if (this.nearbyCandidates.Count >= MaxEntries ||
                    obj.ObjectKind is ObjectKind.Pc or ObjectKind.Companion or ObjectKind.Mount)
                {
                    continue;
                }

                var objPos = new Vector2(obj.Position.X, obj.Position.Z);
                var distance = Vector2.Distance(this.playerPos, objPos);
                if (distance > CandidateRadius)
                {
                    continue;
                }

                this.nearbyCandidates.Add($"candidate:{FormatObject(obj)} dist={distance:0.0} pos={FormatPos(obj.Position)}");
            }
        }

        public string Build()
        {
            var parts = new List<string>();
            if (this.objectMatches.Count > 0)
            {
                parts.Add("matches=[" + string.Join("; ", this.objectMatches) + "]");
            }

            if (this.auraMatches.Count > 0)
            {
                parts.Add("auras=[" + string.Join("; ", this.auraMatches) + "]");
            }

            if (this.rejectedStatuses.Count > 0)
            {
                parts.Add("rejectedStatuses=[" + string.Join("; ", this.rejectedStatuses) + "]");
            }

            if (this.nearbyCandidates.Count > 0)
            {
                parts.Add("nearby=[" + string.Join("; ", this.nearbyCandidates) + "]");
            }

            return parts.Count == 0 ? "no survivability zone evidence" : string.Join(" | ", parts);
        }

        private static string FormatObject(IGameObject obj)
        {
            return $"base=0x{obj.BaseId:X} id=0x{obj.GameObjectId:X} entity=0x{obj.EntityId:X} owner=0x{obj.OwnerId:X} kind={obj.ObjectKind}";
        }

        private static string FormatPos(Vector3 pos)
        {
            return $"({pos.X:0.0},{pos.Y:0.0},{pos.Z:0.0})";
        }
    }

    private sealed class SurvivabilityZoneGoalPlan
    {
        private static readonly MethodInfo ScoreFromWPosMethod =
            typeof(SurvivabilityZoneGoalPlan).GetMethod(nameof(ScoreFromWPos), BindingFlags.Instance | BindingFlags.NonPublic)!;

        private readonly ulong casterId;
        private readonly string zoneName;
        private readonly string casterName;
        private readonly Vector2 center;
        private readonly Vector2 casterPosition;
        private readonly Vector2 preferredEntryPosition;
        private readonly TargetRangeConstraint? targetRangeConstraint;
        private readonly float radius;
        private readonly bool playerInZone;
        private readonly bool playerSatisfiesPlan;

        private SurvivabilityZoneGoalPlan(
            string zoneName,
            string casterName,
            Vector2 center,
            Vector2 casterPosition,
            ulong casterId,
            float radius,
            float distanceToCenter,
            bool playerInZone,
            bool playerSatisfiesPlan,
            Vector2 preferredEntryPosition,
            TargetRangeConstraint? targetRangeConstraint)
        {
            this.zoneName = zoneName;
            this.casterName = casterName;
            this.center = center;
            this.casterPosition = casterPosition;
            this.casterId = casterId;
            this.radius = radius;
            this.preferredEntryPosition = preferredEntryPosition;
            this.targetRangeConstraint = targetRangeConstraint;
            this.DistanceToCenter = distanceToCenter;
            this.playerInZone = playerInZone;
            this.playerSatisfiesPlan = playerSatisfiesPlan;
        }

        public string ZoneName => this.zoneName;
        public string CasterName => this.casterName;
        public float DistanceToCenter { get; }
        public bool PlayerInZone => this.playerInZone;
        public bool PlayerSatisfiesPlan => this.playerSatisfiesPlan;
        public Vector2 PreferredEntryPosition => this.preferredEntryPosition;

        public static SurvivabilityZoneGoalPlan? TryCreate(
            string zoneName,
            string casterName,
            Vector2 center,
            Vector2 casterPosition,
            ulong casterId,
            float radius,
            Vector2 playerPosition,
            float distanceToCenter,
            bool playerInZone,
            TargetRangeConstraint? targetRangeConstraint)
        {
            if (!TryFindPreferredEntryPosition(center, playerPosition, radius, distanceToCenter, playerInZone, targetRangeConstraint, out var preferredEntryPosition))
            {
                return null;
            }

            var playerSatisfiesPlan = playerInZone &&
                                      (!targetRangeConstraint.HasValue || targetRangeConstraint.Value.Contains(playerPosition));
            return new(
                zoneName,
                casterName,
                center,
                casterPosition,
                casterId,
                radius,
                distanceToCenter,
                playerInZone,
                playerSatisfiesPlan,
                preferredEntryPosition,
                targetRangeConstraint);
        }

        public Vector3 MovementDestination(float y)
        {
            return new Vector3(this.preferredEntryPosition.X, y, this.preferredEntryPosition.Y);
        }

        public bool SameSource(SurvivabilityZoneGoalPlan other)
        {
            return this.casterId == other.casterId &&
                   string.Equals(this.zoneName, other.zoneName, StringComparison.Ordinal) &&
                   this.playerSatisfiesPlan == other.playerSatisfiesPlan &&
                   Vector2.DistanceSquared(this.center, other.center) <= 0.25f &&
                   Vector2.DistanceSquared(this.preferredEntryPosition, other.preferredEntryPosition) <= 0.25f &&
                   SameTargetRangeConstraint(this.targetRangeConstraint, other.targetRangeConstraint);
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
                new Vector3(this.preferredEntryPosition.X, y, this.preferredEntryPosition.Y),
                this.radius,
                injected,
                this.playerInZone,
                this.playerSatisfiesPlan,
                this.targetRangeConstraint.HasValue,
                this.zoneName,
                this.casterName);
        }

        private float ScoreFromWPos(float x, float z)
        {
            var point = new Vector2(x, z);
            var distance = Vector2.Distance(point, this.center);
            if (distance > this.radius ||
                this.targetRangeConstraint.HasValue && !this.targetRangeConstraint.Value.Contains(point))
            {
                return 0f;
            }

            if (this.playerSatisfiesPlan)
            {
                return InsideScore;
            }

            var preferredDistance = Vector2.Distance(point, this.preferredEntryPosition);
            if (preferredDistance <= PreferredEntryRadius)
            {
                return PreferredEntryScore;
            }

            return GoalZoneScorePolicy.WeakPreference;
        }

        private static bool SameTargetRangeConstraint(TargetRangeConstraint? left, TargetRangeConstraint? right)
        {
            if (!left.HasValue || !right.HasValue)
            {
                return left.HasValue == right.HasValue;
            }

            return MathF.Abs(left.Value.Radius - right.Value.Radius) <= 0.1f &&
                   Vector2.DistanceSquared(left.Value.Center, right.Value.Center) <= 0.25f;
        }

        private static bool TryFindPreferredEntryPosition(
            Vector2 center,
            Vector2 playerPosition,
            float radius,
            float distanceToCenter,
            bool playerInZone,
            TargetRangeConstraint? targetRangeConstraint,
            out Vector2 preferredEntryPosition)
        {
            if (!targetRangeConstraint.HasValue)
            {
                preferredEntryPosition = FindPreferredEntryPosition(center, playerPosition, radius, distanceToCenter, playerInZone);
                return true;
            }

            var constraint = targetRangeConstraint.Value;
            if (playerInZone && constraint.Contains(playerPosition))
            {
                preferredEntryPosition = playerPosition;
                return true;
            }

            var innerRadius = MathF.Max(0f, radius - ZoneEntryMargin);
            if (TryFindClosestPointInCircleIntersection(playerPosition, center, innerRadius, constraint.Center, constraint.Radius, out preferredEntryPosition) ||
                TryFindClosestPointInCircleIntersection(playerPosition, center, radius, constraint.Center, constraint.Radius, out preferredEntryPosition))
            {
                return true;
            }

            preferredEntryPosition = default;
            return false;
        }

        private static Vector2 FindPreferredEntryPosition(Vector2 center, Vector2 playerPosition, float radius, float distanceToCenter, bool playerInZone)
        {
            if (playerInZone)
            {
                return playerPosition;
            }

            if (distanceToCenter <= 0.01f)
            {
                return center;
            }

            var directionFromCenter = Vector2.Normalize(playerPosition - center);
            return center + directionFromCenter * MathF.Max(0f, radius - ZoneEntryMargin);
        }

        private static bool TryFindClosestPointInCircleIntersection(
            Vector2 origin,
            Vector2 centerA,
            float radiusA,
            Vector2 centerB,
            float radiusB,
            out Vector2 best)
        {
            best = default;
            var bestCandidate = default(Vector2);
            var bestDistanceSquared = float.PositiveInfinity;
            Consider(origin);
            Consider(ProjectToCircle(origin, centerA, radiusA));
            Consider(ProjectToCircle(origin, centerB, radiusB));
            Consider(centerA);
            Consider(centerB);

            var delta = centerB - centerA;
            var distanceSquared = delta.LengthSquared();
            if (distanceSquared > 0.0001f)
            {
                var distance = MathF.Sqrt(distanceSquared);
                if (distance <= radiusA + radiusB + CircleTolerance &&
                    distance >= MathF.Abs(radiusA - radiusB) - CircleTolerance)
                {
                    var fromAToMid = ((radiusA * radiusA) - (radiusB * radiusB) + distanceSquared) / (2f * distance);
                    var heightSquared = (radiusA * radiusA) - (fromAToMid * fromAToMid);
                    if (heightSquared >= -CircleTolerance)
                    {
                        var height = MathF.Sqrt(MathF.Max(0f, heightSquared));
                        var unit = delta / distance;
                        var midpoint = centerA + (unit * fromAToMid);
                        var perpendicular = new Vector2(-unit.Y, unit.X);
                        Consider(midpoint + (perpendicular * height));
                        if (height > CircleTolerance)
                        {
                            Consider(midpoint - (perpendicular * height));
                        }
                    }
                }
            }

            best = bestCandidate;
            return !float.IsPositiveInfinity(bestDistanceSquared);

            void Consider(Vector2 candidate)
            {
                if (!IsInsideCircle(candidate, centerA, radiusA) ||
                    !IsInsideCircle(candidate, centerB, radiusB))
                {
                    return;
                }

                var distanceToOriginSquared = Vector2.DistanceSquared(origin, candidate);
                if (distanceToOriginSquared < bestDistanceSquared)
                {
                    bestCandidate = candidate;
                    bestDistanceSquared = distanceToOriginSquared;
                }
            }
        }

        private static Vector2 ProjectToCircle(Vector2 point, Vector2 center, float radius)
        {
            if (radius <= 0f)
            {
                return center;
            }

            var offset = point - center;
            var distanceSquared = offset.LengthSquared();
            if (distanceSquared <= radius * radius)
            {
                return point;
            }

            if (distanceSquared <= 0.0001f)
            {
                return center + new Vector2(radius, 0f);
            }

            return center + (offset * (radius / MathF.Sqrt(distanceSquared)));
        }

        private static bool IsInsideCircle(Vector2 point, Vector2 center, float radius)
        {
            var effectiveRadius = radius + CircleTolerance;
            return Vector2.DistanceSquared(point, center) <= effectiveRadius * effectiveRadius;
        }
    }
}

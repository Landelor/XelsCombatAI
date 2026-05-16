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
    float Radius,
    bool Injected,
    bool PlayerInZone,
    string ZoneName,
    string CasterName);

internal sealed class SurvivabilityZonePositioningController : IBossModGoalZoneContributor, IDisposable
{
    private const float PreferredEntryRadius = 1.5f;
    private const float ZoneEntryMargin = 1.5f;
    private const float PreferredEntryScore = GoalZoneScorePolicy.StrongPreference;
    private const float InsideScore = GoalZoneScorePolicy.NormalPreference;
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
    private readonly Func<bool> automatedMovementSuppressed;
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
        Func<bool> automatedMovementSuppressed)
    {
        this.config = config;
        this.services = services;
        this.automatedMovementSuppressed = automatedMovementSuppressed;
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

        var previousPlan = this.lastPlan;
        this.lastZoneName = plan.ZoneName;
        this.lastCasterName = plan.CasterName;
        this.lastDistanceToCenter = plan.DistanceToCenter;
        this.lastPlan = plan;

        if (plan.PlayerInZone)
        {
            this.lastGoalDelegate = null;
            this.lastInjected = false;
            this.lastOverlay = plan.CreateOverlay(player.Position.Y, injected: false);
            this.nextOverlayRefresh = DateTime.UtcNow.Add(OverlayRefreshInterval);
            this.lastReason = $"holding inside {plan.ZoneName}";
            return;
        }

        if (this.lastGoalDelegate == null || previousPlan == null || !previousPlan.SameSource(plan))
        {
            this.lastGoalDelegate = plan.CreateGoalDelegate(this.resolvedWPosType!, this.wposXField!, this.wposZField!);
        }

        contributions.Add(new(this.lastGoalDelegate, BossModGoalPriority.DefensiveMechanic, "Defensive zone", plan.PreferredEntryPosition, MechanicWhisperConfidence.Confident));
        this.lastInjected = true;
        this.lastOverlay = plan.CreateOverlay(player.Position.Y, injected: true);
        this.nextOverlayRefresh = DateTime.UtcNow.Add(OverlayRefreshInterval);
        this.lastReason = $"goal injected toward {plan.ZoneName}";
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

        var now = DateTime.UtcNow;
        if (now < this.nextOverlayRefresh)
        {
            return;
        }

        this.nextOverlayRefresh = now.Add(OverlayRefreshInterval);
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

    private SurvivabilityZoneGoalPlan? FindBestPlan(IBattleChara player)
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
                var plan = this.CreateObjectPlan(zone, obj, partyAllies, playerPos, diagnostics, "object");
                best = SelectBest(best, plan);
            }

            if (matchedDirectObject)
            {
                continue;
            }

            if (this.TryGetCachedZonePlan(zone, friendlyActors, friendlyIds, playerPos, diagnostics, out var cachedPlan))
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
                        var plan = this.CreateObjectPlan(zone, source, partyAllies, playerPos, diagnostics, $"status {status.StatusId} source");
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
                var plan = new SurvivabilityZoneGoalPlan(
                    zone.Name,
                    actor.Name.TextValue,
                    actorPos,
                    actorPos,
                    actor.GameObjectId,
                    zone.Radius,
                    playerPos,
                    distanceToCenter,
                    distanceToCenter <= zone.Radius);

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

    private SurvivabilityZoneGoalPlan CreateObjectPlan(
        ZoneDefinition zone,
        IGameObject obj,
        IReadOnlyCollection<IBattleChara> partyAllies,
        Vector2 playerPos,
        ZoneDiagnostics diagnostics,
        string matchKind)
    {
        var center = new Vector2(obj.Position.X, obj.Position.Z);
        var caster = FindCaster(partyAllies, obj.OwnerId);
        var casterPos = caster != null ? new Vector2(caster.Position.X, caster.Position.Z) : center;
        var casterId = caster?.GameObjectId ?? obj.GameObjectId;
        var casterName = caster?.Name.TextValue ?? "<unknown>";
        var distanceToCenter = Vector2.Distance(playerPos, center);
        diagnostics.AddObjectMatch(zone, obj, distanceToCenter, matchKind);
        return new SurvivabilityZoneGoalPlan(
            zone.Name,
            casterName,
            center,
            casterPos,
            casterId,
            zone.Radius,
            playerPos,
            distanceToCenter,
            distanceToCenter <= zone.Radius);
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
            var candidate = new SurvivabilityZoneGoalPlan(
                zone.Name,
                casterName,
                center,
                casterPos,
                casterId,
                zone.Radius,
                playerPos,
                distanceToCenter,
                distanceToCenter <= zone.Radius);
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

    private static SurvivabilityZoneGoalPlan SelectBest(SurvivabilityZoneGoalPlan? current, SurvivabilityZoneGoalPlan candidate)
    {
        return current == null || candidate.DistanceToCenter < current.DistanceToCenter ? candidate : current;
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
        private readonly float radius;
        private readonly bool playerInZone;

        public SurvivabilityZoneGoalPlan(
            string zoneName,
            string casterName,
            Vector2 center,
            Vector2 casterPosition,
            ulong casterId,
            float radius,
            Vector2 playerPosition,
            float distanceToCenter,
            bool playerInZone)
        {
            this.zoneName = zoneName;
            this.casterName = casterName;
            this.center = center;
            this.casterPosition = casterPosition;
            this.casterId = casterId;
            this.radius = radius;
            this.preferredEntryPosition = FindPreferredEntryPosition(center, playerPosition, radius, distanceToCenter, playerInZone);
            this.DistanceToCenter = distanceToCenter;
            this.playerInZone = playerInZone;
        }

        public string ZoneName => this.zoneName;
        public string CasterName => this.casterName;
        public float DistanceToCenter { get; }
        public bool PlayerInZone => this.playerInZone;
        public Vector2 PreferredEntryPosition => this.preferredEntryPosition;

        public Vector3 MovementDestination(float y)
        {
            return new Vector3(this.preferredEntryPosition.X, y, this.preferredEntryPosition.Y);
        }

        public bool SameSource(SurvivabilityZoneGoalPlan other)
        {
            return this.casterId == other.casterId &&
                   string.Equals(this.zoneName, other.zoneName, StringComparison.Ordinal) &&
                   this.playerInZone == other.playerInZone &&
                   Vector2.DistanceSquared(this.center, other.center) <= 0.25f &&
                   Vector2.DistanceSquared(this.preferredEntryPosition, other.preferredEntryPosition) <= 0.25f;
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
            var point = new Vector2(x, z);
            var distance = Vector2.Distance(point, this.center);
            if (distance > this.radius)
            {
                return 0f;
            }

            if (this.playerInZone)
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
    }
}

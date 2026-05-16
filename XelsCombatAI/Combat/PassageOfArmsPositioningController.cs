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

internal sealed record PassageOfArmsPositioningStatus(
    string HookState,
    string LastReason,
    bool Injected,
    string PaladinName,
    float DistanceToPreferred,
    bool PlayerInCone,
    Vector3? PaladinPosition,
    Vector3? PreferredPosition);

internal sealed record PassageOfArmsOverlaySnapshot(
    Vector3 PaladinPosition,
    Vector3 PreferredPosition,
    float Radius,
    float HalfAngle,
    float RotationRadians,
    bool Injected,
    bool PlayerInCone,
    string PaladinName);

internal sealed class PassageOfArmsPositioningController(
    Configuration config,
    DalamudServices services,
    Func<bool> automatedMovementSuppressed)
    : IBossModGoalZoneContributor
{
    private const float ConeRadius = 8f;
    private const float ConeHalfAngle = MathF.PI / 3f;
    private const float PreferredDistance = 3.5f;
    private const float MinimumDistance = 0.8f;
    private const float StrongInsideScore = GoalZoneScorePolicy.NormalPreference;
    private const float PreferredPointScore = GoalZoneScorePolicy.StrongPreference;
    private const uint PaladinJobId = 19;
    private static readonly TimeSpan OverlayRefreshInterval = TimeSpan.FromMilliseconds(250);
    private static readonly BindingFlags InstanceFlags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

    private FieldInfo? goalZonesField;
    private FieldInfo? wposXField;
    private FieldInfo? wposZField;
    private Type? resolvedHintsType;
    private Type? resolvedWPosType;
    private string hookState = "unresolved";
    private string lastReason = "not evaluated";
    private bool lastInjected;
    private string lastPaladinName = "<none>";
    private float lastDistanceToPreferred;
    private bool lastPlayerInCone;
    private Delegate? lastGoalDelegate;
    private PassageGoalPlan? lastPlan;
    private PassageOfArmsOverlaySnapshot? lastOverlay;
    private DateTime nextOverlayRefresh = DateTime.MinValue;

    public PassageOfArmsPositioningStatus Status => new(
        this.hookState,
        this.lastReason,
        this.lastInjected,
        this.lastPaladinName,
        this.lastDistanceToPreferred,
        this.lastPlayerInCone,
        this.lastOverlay?.PaladinPosition,
        this.lastOverlay?.PreferredPosition);

    public PassageOfArmsOverlaySnapshot? Overlay => this.lastOverlay;

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
        this.lastPaladinName = "<none>";
        this.lastDistanceToPreferred = 0f;
        this.lastPlayerInCone = false;
        this.lastGoalDelegate = null;
        this.lastPlan = null;
        this.lastOverlay = null;
        this.nextOverlayRefresh = DateTime.MinValue;
    }

    public void TryInjectGoal(object hints, ICollection<BossModGoalContribution> contributions)
    {
        this.lastInjected = false;
        this.lastOverlay = null;

        if (!config.Enabled || !config.ManagePassageOfArmsPositioning)
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
            this.SetInactive("no active Passage of Arms");
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

        contributions.Add(new(this.lastGoalDelegate, BossModGoalPriority.DefensiveMechanic, "Passage of Arms", plan.PreferredPosition, MechanicWhisperConfidence.Confident));
        this.lastInjected = true;
        this.lastPaladinName = plan.PaladinName;
        this.lastDistanceToPreferred = Vector2.Distance(plan.PlayerPosition, plan.PreferredPosition);
        this.lastPlayerInCone = plan.PlayerInCone;
        this.lastOverlay = plan.CreateOverlay(player.Position.Y, injected: true);
        this.nextOverlayRefresh = DateTime.UtcNow.Add(OverlayRefreshInterval);
        this.lastReason = plan.PlayerInCone ? "holding inside Passage of Arms" : "goal injected";
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

        this.lastPaladinName = plan.PaladinName;
        this.lastDistanceToPreferred = Vector2.Distance(plan.PlayerPosition, plan.PreferredPosition);
        this.lastPlayerInCone = plan.PlayerInCone;
        this.lastOverlay = plan.CreateOverlay(player.Position.Y, this.lastInjected);
    }

    private void SetInactive(string reason)
    {
        this.lastReason = reason;
        this.lastPaladinName = "<none>";
        this.lastDistanceToPreferred = 0f;
        this.lastPlayerInCone = false;
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
            this.lastReason = $"BMR Passage goal reflection members unavailable: {FormatMissing(
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

    private PassageGoalPlan? FindBestPlan(IBattleChara player)
    {
        PassageGoalPlan? best = null;
        foreach (var paladin in this.ActivePassagePaladins(player))
        {
            var plan = PassageGoalPlan.Create(player, paladin);
            if (best == null || plan.DistanceToPreferred < best.DistanceToPreferred)
            {
                best = plan;
            }
        }

        return best;
    }

    private IEnumerable<IBattleChara> ActivePassagePaladins(IBattleChara player)
    {
        foreach (var ally in PartyAllyProvider.EnumerateVisiblePartyAllies(services, player))
        {
            if (ally.ClassJob.RowId == PaladinJobId &&
                ally.StatusList.Any(status => status.StatusId == ActionUse.PassageOfArmsStatusId && status.RemainingTime > 0f))
            {
                yield return ally;
            }
        }
    }

    private sealed class PassageGoalPlan(
        ulong paladinId,
        string paladinName,
        Vector2 paladinPosition,
        Vector2 playerPosition,
        Vector2 behindDirection,
        Vector2 preferredPosition,
        float rotationRadians,
        bool playerInCone)
    {
        private static readonly MethodInfo ScoreFromWPosMethod = typeof(PassageGoalPlan).GetMethod(nameof(ScoreFromWPos), BindingFlags.Instance | BindingFlags.NonPublic)!;

        private ulong PaladinId => paladinId;
        public string PaladinName => paladinName;
        public Vector2 PlayerPosition => playerPosition;
        public Vector2 PreferredPosition => preferredPosition;
        public bool PlayerInCone => playerInCone;
        private float RotationRadians => rotationRadians;
        public float DistanceToPreferred => Vector2.Distance(playerPosition, preferredPosition);

        public static PassageGoalPlan Create(IBattleChara player, IBattleChara paladin)
        {
            var paladinPos = new Vector2(paladin.Position.X, paladin.Position.Z);
            var playerPos = new Vector2(player.Position.X, player.Position.Z);
            var rotation = NormalizeRadians(paladin.Rotation + MathF.PI);
            var behind = Direction2(rotation);
            var preferred = FindPreferredPosition(playerPos, paladinPos, behind);
            return new(
                paladin.GameObjectId,
                paladin.Name.TextValue,
                paladinPos,
                playerPos,
                behind,
                preferred,
                rotation,
                IsInsideCone(playerPos, paladinPos, behind));
        }

        public bool SameSource(PassageGoalPlan other)
        {
            return this.PaladinId == other.PaladinId &&
                   Vector2.DistanceSquared(this.PreferredPosition, other.PreferredPosition) <= 0.25f &&
                   MathF.Abs(NormalizeRadians(this.RotationRadians - other.RotationRadians)) <= 0.1f;
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

        public PassageOfArmsOverlaySnapshot CreateOverlay(float y, bool injected)
        {
            return new(
                new Vector3(paladinPosition.X, y, paladinPosition.Y),
                new Vector3(preferredPosition.X, y, preferredPosition.Y),
                ConeRadius,
                ConeHalfAngle,
                rotationRadians,
                injected,
                playerInCone,
                paladinName);
        }

        private float ScoreFromWPos(float x, float z)
        {
            var point = new Vector2(x, z);
            if (!IsInsideCone(point, paladinPosition, behindDirection))
            {
                return 0f;
            }

            var preferredDistanceSq = Vector2.DistanceSquared(point, preferredPosition);
            if (preferredDistanceSq <= 1f)
            {
                return PreferredPointScore;
            }

            var distance = MathF.Sqrt(preferredDistanceSq);
            return Math.Clamp(MathF.Max(StrongInsideScore, PreferredPointScore - distance * 0.1f), 0f, GoalZoneScorePolicy.MaxRawScore);
        }

        private static Vector2 FindPreferredPosition(Vector2 playerPos, Vector2 paladinPos, Vector2 behind)
        {
            if (IsInsideCone(playerPos, paladinPos, behind))
            {
                return playerPos;
            }

            var best = paladinPos + behind * PreferredDistance;
            var bestDistanceSq = Vector2.DistanceSquared(playerPos, best);
            var distances = new[] { 2f, PreferredDistance, 5f, 7f };
            var angles = new[] { 0f, -ConeHalfAngle * 0.5f, ConeHalfAngle * 0.5f, -ConeHalfAngle * 0.9f, ConeHalfAngle * 0.9f };
            foreach (var distance in distances)
            {
                foreach (var angle in angles)
                {
                    var candidateDirection = Rotate(behind, angle);
                    var candidate = paladinPos + candidateDirection * distance;
                    var candidateDistanceSq = Vector2.DistanceSquared(playerPos, candidate);
                    if (candidateDistanceSq < bestDistanceSq)
                    {
                        best = candidate;
                        bestDistanceSq = candidateDistanceSq;
                    }
                }
            }

            return best;
        }

        private static bool IsInsideCone(Vector2 point, Vector2 origin, Vector2 direction)
        {
            var toPoint = point - origin;
            var distanceSq = toPoint.LengthSquared();
            if (distanceSq < MinimumDistance * MinimumDistance || distanceSq > ConeRadius * ConeRadius)
            {
                return false;
            }

            var distance = MathF.Sqrt(distanceSq);
            var dot = Vector2.Dot(toPoint / distance, direction);
            return dot >= MathF.Cos(ConeHalfAngle);
        }

        private static Vector2 Direction2(float rotation)
        {
            var (sin, cos) = MathF.SinCos(rotation);
            return new Vector2(sin, cos);
        }

        private static Vector2 Rotate(Vector2 direction, float angle)
        {
            var (sin, cos) = MathF.SinCos(angle);
            return new Vector2(direction.X * cos - direction.Y * sin, direction.X * sin + direction.Y * cos);
        }

        private static float NormalizeRadians(float radians)
        {
            while (radians <= -MathF.PI)
            {
                radians += MathF.Tau;
            }

            while (radians > MathF.PI)
            {
                radians -= MathF.Tau;
            }

            return radians;
        }
    }
}

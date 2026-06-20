using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;
using System.Numerics;
using System.Reflection;
using Dalamud.Game.ClientState.Objects.Types;
using FFXIVClientStructs.FFXIV.Client.Game;
using XelsCombatAI.Game;
using XelsCombatAI.Integrations;

namespace XelsCombatAI.Combat;

internal sealed record BlackMageLeyLinesPositioningStatus(
    string HookState,
    string LastReason,
    bool Injected,
    bool PlayerInZone,
    float DistanceToCenter,
    Vector3? ZoneCenter);

internal sealed record BlackMageLeyLinesOverlaySnapshot(
    Vector3 ZoneCenter,
    Vector3 PreferredPosition,
    float Radius,
    bool Injected,
    bool PlayerInZone,
    string Reason);

internal sealed class BlackMageLeyLinesPositioningController(
    Configuration config,
    DalamudServices services,
    RotationSolverActionReflection rotationSolverActions,
    BossModReflectionSafety bossModSafety,
    MobilityDecisionEvaluator mobilityEvaluator,
    Func<bool> automatedMovementSuppressed,
    Func<BossModMechanicPressure> mechanicPressure)
    : IBossModGoalZoneContributor
{
    private const uint BlackMageJobId = 25;
    private const float LeyLinesRadius = 3f;
    private const float PreferredEntryRadius = 0.75f;
    private const float ZoneEntryMargin = 0.35f;
    private const float EstimatedCombatMoveSpeed = 6f;
    private const float WalkArrivalBufferSeconds = 0.2f;
    private const float MaxNarrowSlidecastReturnDistance = 2.1f;
    private const float MinimumBetweenTheLinesDistance = 2.25f;
    private const float MinimumRetraceDistance = 4f;
    private static readonly TimeSpan OverlayRefreshInterval = TimeSpan.FromMilliseconds(250);
    private static readonly TimeSpan ActionAttemptCooldown = TimeSpan.FromMilliseconds(250);
    private static readonly BindingFlags InstanceFlags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

    private FieldInfo? goalZonesField;
    private FieldInfo? wposXField;
    private FieldInfo? wposZField;
    private Type? resolvedHintsType;
    private Type? resolvedWPosType;
    private string hookState = "unresolved";
    private string lastReason = "not evaluated";
    private bool lastInjected;
    private bool lastPlayerInZone;
    private float lastDistanceToCenter;
    private Delegate? lastGoalDelegate;
    private LeyLinesGoalPlan? lastPlan;
    private BlackMageLeyLinesOverlaySnapshot? lastOverlay;
    private DateTime nextOverlayRefresh = DateTime.MinValue;
    private DateTime nextActionAttempt = DateTime.MinValue;

    public BlackMageLeyLinesPositioningStatus Status => new(
        this.hookState,
        this.lastReason,
        this.lastInjected,
        this.lastPlayerInZone,
        this.lastDistanceToCenter,
        this.lastOverlay?.ZoneCenter);

    public BlackMageLeyLinesOverlaySnapshot? Overlay => this.lastOverlay;

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
        this.lastPlayerInZone = false;
        this.lastDistanceToCenter = 0f;
        this.lastGoalDelegate = null;
        this.lastPlan = null;
        this.lastOverlay = null;
        this.nextOverlayRefresh = DateTime.MinValue;
        this.nextActionAttempt = DateTime.MinValue;
    }

    public void TryInjectGoal(object hints, ICollection<BossModGoalContribution> contributions)
    {
        this.lastInjected = false;
        this.lastOverlay = null;

        if (!config.Enabled || !config.ManageLeylines)
        {
            this.SetInactive("disabled");
            return;
        }

        if (!config.ManageMovement)
        {
            this.SetInactive("movement management disabled");
            return;
        }

        if (!config.ReturnToLeylines)
        {
            this.SetInactive("Ley Lines return movement disabled");
            return;
        }

        if (automatedMovementSuppressed())
        {
            this.SetInactive("manual movement suppression active");
            return;
        }

        var player = services.ObjectTable.LocalPlayer;
        if (!IsActiveBlackMage(player))
        {
            this.SetInactive("not active Black Mage");
            return;
        }

        if (!this.EnsureResolved(hints.GetType()))
        {
            return;
        }

        var plan = this.FindPlan(player!);
        if (plan == null)
        {
            this.SetInactive("no active Ley Lines ground");
            return;
        }

        var slidecastWindow = player!.IsCasting && CasterMovementPolicy.IsCasterSlidecastWindow(player);
        if (player.IsCasting &&
            (!slidecastWindow || !ShouldAllowNarrowSlidecastReturn(plan.DistanceToPreferred)))
        {
            this.lastInjected = false;
            this.lastPlayerInZone = plan.PlayerInZone;
            this.lastDistanceToCenter = plan.DistanceToCenter;
            this.lastOverlay = plan.CreateOverlay(player.Position.Y, injected: false, "casting outside Ley Lines slidecast return");
            this.lastReason = "casting outside Ley Lines slidecast return";
            return;
        }

        if (this.goalZonesField!.GetValue(hints) is not IList)
        {
            this.SetInactive("BMR goal zone list unavailable");
            return;
        }

        var previousPlan = this.lastPlan;
        this.lastPlan = plan;
        this.lastPlayerInZone = plan.PlayerInZone;
        this.lastDistanceToCenter = plan.DistanceToCenter;

        if (plan.PlayerInZone)
        {
            this.lastGoalDelegate = null;
            this.lastInjected = false;
            this.lastOverlay = plan.CreateOverlay(player!.Position.Y, injected: false, "holding inside Ley Lines");
            this.nextOverlayRefresh = DateTime.UtcNow.Add(OverlayRefreshInterval);
            this.lastReason = "holding inside Ley Lines";
            return;
        }

        if (this.lastGoalDelegate == null || previousPlan == null || !previousPlan.SameSource(plan))
        {
            this.lastGoalDelegate = plan.CreateGoalDelegate(this.resolvedWPosType!, this.wposXField!, this.wposZField!);
        }

        var reason = plan.DistanceToPreferred <= MaxNarrowSlidecastReturnDistance
            ? "narrow Ley Lines return"
            : "Ley Lines return";
        contributions.Add(new(this.lastGoalDelegate, BossModGoalPriority.Uptime, "Ley Lines", plan.PreferredEntryPosition, MechanicWhisperConfidence.Routine));
        this.lastInjected = true;
        this.lastOverlay = plan.CreateOverlay(player!.Position.Y, injected: true, reason);
        this.nextOverlayRefresh = DateTime.UtcNow.Add(OverlayRefreshInterval);
        this.lastReason = $"goal injected toward Ley Lines ({plan.DistanceToPreferred:0.0}y to edge)";
    }

    public bool TryUseLeyLinesAction()
    {
        var player = services.ObjectTable.LocalPlayer;
        if (!IsActiveBlackMage(player) ||
            !config.Enabled ||
            !config.ManageMovement ||
            !config.ManageLeylines ||
            automatedMovementSuppressed() ||
            DateTime.UtcNow < this.nextActionAttempt)
        {
            return false;
        }

        var plan = this.FindPlan(player!);
        if (plan == null || plan.PlayerInZone)
        {
            return false;
        }

        var pressure = mechanicPressure();
        var canWalkBack = false;
        var walkReason = string.Empty;
        if (config.ReturnToLeylines)
        {
            canWalkBack = this.CanWalkBackWithinGcd(plan.DistanceToPreferred, out walkReason);
        }

        if (ShouldHoldActionForReturnMovement(config.ReturnToLeylines, canWalkBack, plan.DistanceToPreferred))
        {
            this.lastReason = ShouldAllowNarrowSlidecastReturn(plan.DistanceToPreferred)
                ? $"sliding back into Ley Lines ({plan.DistanceToPreferred:0.0}y to edge)"
                : walkReason;
            return false;
        }

        if (ShouldUseBetweenTheLines(
                config.UseBetweenTheLines,
                ActionUse.CanUseAction(ActionUse.BlackMageBetweenTheLinesActionId),
                pressure.BadForOptionalMovement,
                plan.DistanceToPreferred) &&
            this.TryUseBetweenTheLines(player!, plan))
        {
            return true;
        }

        if (ShouldUseRetrace(
                config.UseRetrace,
                ActionUse.CanUseAction(ActionUse.BlackMageRetraceActionId),
                pressure.BadForOptionalMovement,
                plan.DistanceToPreferred))
        {
            return this.TryUseRetrace(player!, plan);
        }

        return false;
    }

    public void RefreshOverlay()
    {
        if (!config.ShowDecisionOverlay)
        {
            this.lastOverlay = null;
            this.nextOverlayRefresh = DateTime.MinValue;
            return;
        }

        if (!config.ManageLeylines ||
            (!config.ReturnToLeylines && !config.UseBetweenTheLines && !config.UseRetrace))
        {
            this.lastOverlay = null;
            this.nextOverlayRefresh = DateTime.MinValue;
            return;
        }

        var player = services.ObjectTable.LocalPlayer;
        if (!IsActiveBlackMage(player))
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
        var plan = this.FindPlan(player!);
        if (plan == null)
        {
            this.lastOverlay = null;
            return;
        }

        this.lastPlayerInZone = plan.PlayerInZone;
        this.lastDistanceToCenter = plan.DistanceToCenter;
        this.lastOverlay = plan.CreateOverlay(player!.Position.Y, this.lastInjected, this.lastReason);
    }

    internal static bool ShouldAllowNarrowSlidecastReturn(float distanceToPreferred)
    {
        return distanceToPreferred > 0f &&
               distanceToPreferred <= MaxNarrowSlidecastReturnDistance;
    }

    internal static bool ShouldHoldActionForReturnMovement(
        bool returnMovementEnabled,
        bool canWalkBack,
        float distanceToPreferred)
    {
        return returnMovementEnabled &&
               (canWalkBack || ShouldAllowNarrowSlidecastReturn(distanceToPreferred));
    }

    internal static bool ShouldUseBetweenTheLines(
        bool enabled,
        bool actionReady,
        bool badForOptionalMovement,
        float distanceToPreferred)
    {
        return enabled &&
               actionReady &&
               !badForOptionalMovement &&
               distanceToPreferred >= MinimumBetweenTheLinesDistance;
    }

    internal static bool ShouldUseRetrace(
        bool enabled,
        bool actionReady,
        bool badForOptionalMovement,
        float distanceToPreferred)
    {
        return enabled &&
               actionReady &&
               !badForOptionalMovement &&
               distanceToPreferred >= MinimumRetraceDistance;
    }

    private bool TryUseBetweenTheLines(IBattleChara player, LeyLinesGoalPlan plan)
    {
        var destination = new Vector3(plan.CenterPosition.X, player.Position.Y, plan.CenterPosition.Y);
        if (!mobilityEvaluator.TryValidateDashDestination(
                player,
                destination,
                services.TargetManager.Target as IBattleChara,
                null,
                MobilityIntent.Uptime,
                "Between the Lines",
                ActionUse.BlackMageBetweenTheLinesActionId,
                MinimumBetweenTheLinesDistance,
                requireSafetyProgress: false,
                requireUptimeProgress: false,
                requireVnavReachable: false,
                out var decision))
        {
            this.lastReason = $"Between the Lines rejected: {decision.RiskReason}";
            return false;
        }

        unsafe
        {
            this.nextActionAttempt = DateTime.UtcNow.Add(ActionAttemptCooldown);
            var location = destination;
            var used = ActionManager.Instance()->UseActionLocation(ActionType.Action, ActionUse.BlackMageBetweenTheLinesActionId, player.GameObjectId, &location);
            mobilityEvaluator.RecordActionResult(decision, used, used ? "action used" : "action failed");
            this.lastReason = used ? "used Between the Lines to Ley Lines" : "failed to use Between the Lines";
            return used;
        }
    }

    private bool TryUseRetrace(IBattleChara player, LeyLinesGoalPlan plan)
    {
        if (!bossModSafety.TryIsPositionSafe(player.Position, out var safe, out var safetyReason) || !safe)
        {
            this.lastReason = $"Retrace held: current position not BMR safe ({safetyReason})";
            return false;
        }

        unsafe
        {
            this.nextActionAttempt = DateTime.UtcNow.Add(ActionAttemptCooldown);
            var used = ActionManager.Instance()->UseAction(ActionType.Action, ActionUse.BlackMageRetraceActionId, player.GameObjectId);
            var decision = new MobilityDecisionDiagnostics(
                DateTime.UtcNow,
                used ? MobilityDecisionState.Used : MobilityDecisionState.Rejected,
                MobilityIntent.Uptime,
                MobilityDecisionEvaluator.FormatIntentLabel(MobilityIntent.Uptime),
                "Retrace",
                ActionUse.BlackMageRetraceActionId,
                player.Position,
                plan.DistanceToPreferred,
                0f,
                "BMR position",
                0f,
                0f,
                safetyReason,
                "Ley Lines moved to player",
                "not checked",
                used ? "action used" : "action failed");
            mobilityEvaluator.RecordActionResult(decision, used, used ? "action used" : "action failed");
            this.lastReason = used ? "used Retrace for Ley Lines" : "failed to use Retrace";
            return used;
        }
    }

    private void SetInactive(string reason)
    {
        this.lastReason = reason;
        this.lastInjected = false;
        this.lastPlayerInZone = false;
        this.lastDistanceToCenter = 0f;
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
            this.lastReason = "BMR Ley Lines goal reflection members unavailable";
            return false;
        }

        this.resolvedHintsType = hintsType;
        this.resolvedWPosType = wposType;
        this.goalZonesField = goalZones;
        this.wposXField = xField;
        this.wposZField = zField;
        return true;
    }

    private LeyLinesGoalPlan? FindPlan(IBattleChara player)
    {
        if (!HasActiveLeyLinesWindow(player))
        {
            return null;
        }

        var zone = this.TryFindLeyLinesObject(player);
        if (zone == null)
        {
            return null;
        }

        var playerPosition = new Vector2(player.Position.X, player.Position.Z);
        var center = new Vector2(zone.Position.X, zone.Position.Z);
        var distanceToCenter = Vector2.Distance(playerPosition, center);
        var playerInZone = HasStatus(player, ActionUse.CircleOfPowerStatusId) ||
                           distanceToCenter <= LeyLinesRadius - 0.05f;
        return new(center, playerPosition, distanceToCenter, playerInZone);
    }

    private IGameObject? TryFindLeyLinesObject(IBattleChara player)
    {
        foreach (var obj in services.ObjectTable)
        {
            if (obj.BaseId == ActionUse.BlackMageLeyLinesObjectDataId &&
                obj.OwnerId == player.GameObjectId)
            {
                return obj;
            }
        }

        return null;
    }

    private bool CanWalkBackWithinGcd(float distance, out string reason)
    {
        reason = string.Empty;
        if (!rotationSolverActions.TryGetUpcomingGcdTiming(out var action, out var timingReason) ||
            !AoeRepositionPolicy.HasReliableGcdTiming(action.GcdRemaining, action.GcdElapsed, action.GcdTotal))
        {
            reason = $"Ley Lines return held: RSR GCD timing unavailable ({timingReason})";
            return true;
        }

        var requiredSeconds = (distance / EstimatedCombatMoveSpeed) + WalkArrivalBufferSeconds;
        if (action.GcdRemaining < requiredSeconds)
        {
            return false;
        }

        reason = $"walking to Ley Lines within GCD ({distance:0.0}y needs {requiredSeconds:0.0}s, {action.GcdRemaining:0.0}s left)";
        return true;
    }

    private static bool IsActiveBlackMage(IBattleChara? player)
    {
        return player != null &&
               player.ClassJob.RowId == BlackMageJobId &&
               !player.IsDead;
    }

    private static bool HasActiveLeyLinesWindow(IBattleChara player)
    {
        return HasStatus(player, ActionUse.LeyLinesStatusId) ||
               HasStatus(player, ActionUse.CircleOfPowerStatusId);
    }

    private static bool HasStatus(IBattleChara player, uint statusId)
        => player.StatusList.Any(status => status.StatusId == statusId && status.RemainingTime > 0f);

    private sealed class LeyLinesGoalPlan
    {
        private static readonly MethodInfo ScoreFromWPosMethod =
            typeof(LeyLinesGoalPlan).GetMethod(nameof(ScoreFromWPos), BindingFlags.Instance | BindingFlags.NonPublic)!;

        private readonly Vector2 center;
        private readonly Vector2 preferredEntryPosition;
        private readonly bool playerInZone;

        public LeyLinesGoalPlan(Vector2 center, Vector2 playerPosition, float distanceToCenter, bool playerInZone)
        {
            this.center = center;
            this.preferredEntryPosition = FindPreferredEntryPosition(center, playerPosition, distanceToCenter, playerInZone);
            this.DistanceToCenter = distanceToCenter;
            this.playerInZone = playerInZone;
        }

        public Vector2 CenterPosition => this.center;
        public Vector2 PreferredEntryPosition => this.preferredEntryPosition;
        public float DistanceToCenter { get; }
        public float DistanceToPreferred => this.playerInZone
            ? 0f
            : MathF.Max(0f, this.DistanceToCenter - (LeyLinesRadius - ZoneEntryMargin));
        public bool PlayerInZone => this.playerInZone;

        public bool SameSource(LeyLinesGoalPlan other)
        {
            return this.playerInZone == other.playerInZone &&
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

        public BlackMageLeyLinesOverlaySnapshot CreateOverlay(float y, bool injected, string reason)
        {
            return new(
                new Vector3(this.center.X, y, this.center.Y),
                new Vector3(this.preferredEntryPosition.X, y, this.preferredEntryPosition.Y),
                LeyLinesRadius,
                injected,
                this.playerInZone,
                reason);
        }

        private float ScoreFromWPos(float x, float z)
        {
            var point = new Vector2(x, z);
            var distance = Vector2.Distance(point, this.center);
            if (distance > LeyLinesRadius)
            {
                return 0f;
            }

            if (this.playerInZone)
            {
                return GoalZoneScorePolicy.NormalPreference;
            }

            var preferredDistance = Vector2.Distance(point, this.preferredEntryPosition);
            return preferredDistance <= PreferredEntryRadius
                ? GoalZoneScorePolicy.StrongPreference
                : GoalZoneScorePolicy.WeakPreference;
        }

        private static Vector2 FindPreferredEntryPosition(Vector2 center, Vector2 playerPosition, float distanceToCenter, bool playerInZone)
        {
            if (playerInZone)
            {
                return playerPosition;
            }

            if (distanceToCenter <= 0.01f)
            {
                return center;
            }

            var directionFromCenter = (playerPosition - center) / distanceToCenter;
            return center + directionFromCenter * MathF.Max(0f, LeyLinesRadius - ZoneEntryMargin);
        }
    }
}

using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;
using System.Numerics;
using System.Reflection;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Game.ClientState.Objects.Types;
using ECommons.Hooks;
using ECommons.Hooks.ActionEffectTypes;
using FFXIVClientStructs.FFXIV.Client.Game;
using XelsCombatAI.Game;
using XelsCombatAI.Integrations;

namespace XelsCombatAI.Combat;

internal sealed record PictomancerStarryMusePositioningStatus(
    string HookState,
    string LastReason,
    bool Injected,
    bool PlayerInZone,
    float DistanceToCenter,
    Vector3? ZoneCenter);

internal sealed record PictomancerStarryMuseOverlaySnapshot(
    Vector3 ZoneCenter,
    Vector3 PreferredPosition,
    float Radius,
    bool Injected,
    bool PlayerInZone);

internal sealed class PictomancerStarryMusePositioningController : IBossModGoalZoneContributor, IDisposable
{
    private const uint PictomancerJobId = 42;
    // The damage buff is 20s; the landscape/Inspiration window is 30s with a 30y effect range.
    private const float StarryMuseRadius = 30f;
    private const float PreferredEntryRadius = 1.25f;
    private const float ZoneEntryMargin = 0.75f;
    private const float EstimatedCombatMoveSpeed = 6f;
    private const float WalkArrivalBufferSeconds = 0.2f;
    private const float MinimumStarryMuseSmudgeDistance = 8f;
    private static readonly TimeSpan StarryMuseLandscapeDuration = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan CachedZoneGrace = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan OverlayRefreshInterval = TimeSpan.FromMilliseconds(250);
    private static readonly TimeSpan SmudgeAttemptCooldown = TimeSpan.FromMilliseconds(250);
    private static readonly BindingFlags InstanceFlags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

    private readonly Configuration config;
    private readonly DalamudServices services;
    private readonly RotationSolverActionReflection rotationSolverActions;
    private readonly MobilityDecisionEvaluator mobilityEvaluator;
    private readonly FacingController facingController;
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
    private bool lastPlayerInZone;
    private float lastDistanceToCenter;
    private Delegate? lastGoalDelegate;
    private StarryMuseGoalPlan? lastPlan;
    private PictomancerStarryMuseOverlaySnapshot? lastOverlay;
    private DateTime nextOverlayRefresh = DateTime.MinValue;
    private DateTime nextSmudgeAttempt = DateTime.MinValue;
    private CachedStarryMuseZone? cachedZone;

    public PictomancerStarryMusePositioningController(
        Configuration config,
        DalamudServices services,
        RotationSolverActionReflection rotationSolverActions,
        MobilityDecisionEvaluator mobilityEvaluator,
        FacingController facingController,
        Func<bool> automatedMovementSuppressed,
        Func<BossModMechanicPressure> mechanicPressure)
    {
        this.config = config;
        this.services = services;
        this.rotationSolverActions = rotationSolverActions;
        this.mobilityEvaluator = mobilityEvaluator;
        this.facingController = facingController;
        this.automatedMovementSuppressed = automatedMovementSuppressed;
        this.mechanicPressure = mechanicPressure;
        ActionEffect.ActionEffectEvent += this.OnActionEffect;
    }

    public PictomancerStarryMusePositioningStatus Status => new(
        this.hookState,
        this.lastReason,
        this.lastInjected,
        this.lastPlayerInZone,
        this.lastDistanceToCenter,
        this.lastOverlay?.ZoneCenter);

    public PictomancerStarryMuseOverlaySnapshot? Overlay => this.lastOverlay;

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
        this.nextSmudgeAttempt = DateTime.MinValue;
        this.cachedZone = null;
    }

    public void Dispose()
    {
        ActionEffect.ActionEffectEvent -= this.OnActionEffect;
    }

    public void TryInjectGoal(object hints, ICollection<BossModGoalContribution> contributions)
    {
        this.lastInjected = false;
        this.lastOverlay = null;

        if (!config.Enabled || !config.ManagePictomancerStarryMuse)
        {
            this.SetInactive("disabled");
            return;
        }

        if (!config.ManageMovement)
        {
            this.SetInactive("movement management disabled");
            return;
        }

        if (automatedMovementSuppressed())
        {
            this.SetInactive("automated movement suppressed");
            return;
        }

        var player = services.ObjectTable.LocalPlayer;
        if (!IsActivePictomancer(player))
        {
            this.SetInactive("not active Pictomancer");
            return;
        }

        if (CasterMovementPolicy.ShouldSuppressAdvisoryMovement(player))
        {
            this.SetInactive("casting outside slidecast window");
            return;
        }

        if (!this.EnsureResolved(hints.GetType()))
        {
            return;
        }

        var plan = this.FindPlan(player!);
        if (plan == null)
        {
            this.SetInactive("no active Starry Muse ground");
            return;
        }

        var goalZones = this.goalZonesField!.GetValue(hints) as IList;
        if (goalZones == null)
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
            this.lastOverlay = plan.CreateOverlay(player!.Position.Y, injected: false);
            this.nextOverlayRefresh = DateTime.UtcNow.Add(OverlayRefreshInterval);
            this.lastReason = "holding inside Starry Muse";
            return;
        }

        if (this.lastGoalDelegate == null || previousPlan == null || !previousPlan.SameSource(plan))
        {
            this.lastGoalDelegate = plan.CreateGoalDelegate(this.resolvedWPosType!, this.wposXField!, this.wposZField!);
        }

        contributions.Add(new(this.lastGoalDelegate, BossModGoalPriority.Uptime, "Starry Muse", plan.PreferredEntryPosition, MechanicWhisperConfidence.Routine));
        this.lastInjected = true;
        this.lastOverlay = plan.CreateOverlay(player!.Position.Y, injected: true);
        this.nextOverlayRefresh = DateTime.UtcNow.Add(OverlayRefreshInterval);
        this.lastReason = "goal injected toward Starry Muse";
    }

    public bool TryUseStarryMuseSmudge()
    {
        var player = services.ObjectTable.LocalPlayer;
        if (!IsActivePictomancer(player) ||
            !config.Enabled ||
            !config.ManageMovement ||
            !config.ManagePictomancerStarryMuse ||
            !config.UsePictomancerStarryMuseSmudge ||
            !config.UseGapCloser ||
            !config.GapCloserPCT)
        {
            return false;
        }

        if (DateTime.UtcNow < this.nextSmudgeAttempt ||
            automatedMovementSuppressed() ||
            mechanicPressure().BadForOptionalMovement)
        {
            return false;
        }

        if (!ActionUse.CanUseAction(ActionUse.PictomancerSmudgeActionId))
        {
            return false;
        }

        var plan = this.FindPlan(player!);
        if (plan == null || plan.PlayerInZone)
        {
            return false;
        }

        if (this.CanWalkBackWithinGcd(plan.DistanceToPreferred, out var walkReason))
        {
            this.lastReason = walkReason;
            return false;
        }

        if (!TryCalculateSmudgeLanding(player!.Position, plan.CenterPosition, out var destination))
        {
            this.lastReason = "Starry Muse Smudge rejected: no 15y landing inside circle";
            return false;
        }

        if (!mobilityEvaluator.TryValidateFixedDashDestination(
            player,
            destination,
            services.TargetManager.Target as IBattleChara,
            null,
            MobilityIntent.PathRecovery,
            "Smudge",
            ActionUse.PictomancerSmudgeActionId,
            MinimumStarryMuseSmudgeDistance,
            requireSafetyProgress: false,
            requireUptimeProgress: false,
            requireVnavReachable: true,
            fixedDashRange: CombatConstants.FixedForwardGapCloserRange,
            fixedDashBackwards: false,
            out var decision))
        {
            this.lastReason = $"Starry Muse Smudge rejected: {decision.RiskReason}";
            return false;
        }

        this.nextSmudgeAttempt = DateTime.UtcNow.Add(SmudgeAttemptCooldown);
        var direction = destination - player.Position;
        direction.Y = 0f;
        var desiredRotation = Geometry.DirectionToRotation(direction);
        if (Geometry.AbsAngleDelta(player.Rotation, desiredRotation) > FacingController.DirectionalDashToleranceRadians)
        {
            facingController.RequestFacing(FacingController.CreateDirectionalDashRequest(desiredRotation, destination, "turn for Smudge to Starry Muse", FacingBossModPolicy.AssistValidatedDash));
            this.lastInjected = false;
            this.lastOverlay = plan.CreateOverlay(player.Position.Y, injected: true);
            this.lastReason = "turning for Smudge to Starry Muse";
            return true;
        }

        unsafe
        {
            var used = ActionManager.Instance()->UseAction(ActionType.Action, ActionUse.PictomancerSmudgeActionId, player.GameObjectId);
            mobilityEvaluator.RecordActionResult(decision, used, used ? "action used" : "action failed");
            this.lastReason = used ? "used Smudge to Starry Muse" : "failed to use Smudge to Starry Muse";
            return used;
        }
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
        if (!IsActivePictomancer(player))
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
        this.lastOverlay = plan.CreateOverlay(player!.Position.Y, this.lastInjected);
    }

    private void OnActionEffect(ActionEffectSet set)
    {
        try
        {
            if (set.Header.ActionID != ActionUse.PictomancerStarryMuseActionId)
            {
                return;
            }

            var player = services.ObjectTable.LocalPlayer;
            var source = set.Source;
            if (player == null ||
                source == null ||
                (source.GameObjectId != player.GameObjectId && source.EntityId != player.EntityId))
            {
                return;
            }

            var position = set.Position == default ? source.Position : set.Position;
            var now = DateTime.UtcNow;
            this.cachedZone = new CachedStarryMuseZone(position, now, now.Add(StarryMuseLandscapeDuration).Add(CachedZoneGrace));
        }
        catch (Exception ex)
        {
            services.Log.Verbose($"Pictomancer Starry Muse action-effect tracking failed: {ex.Message}");
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
            this.lastReason = "BMR Starry Muse goal reflection members unavailable";
            return false;
        }

        this.resolvedHintsType = hintsType;
        this.resolvedWPosType = wposType;
        this.goalZonesField = goalZones;
        this.wposXField = xField;
        this.wposZField = zField;
        return true;
    }

    private StarryMuseGoalPlan? FindPlan(IBattleChara player)
    {
        var cached = this.cachedZone;
        if (!cached.HasValue || cached.Value.ExpiresAtUtc <= DateTime.UtcNow)
        {
            this.cachedZone = null;
            return null;
        }

        if (!HasActiveStarryMuseWindow(player))
        {
            return null;
        }

        var playerPosition = new Vector2(player.Position.X, player.Position.Z);
        var center = new Vector2(cached.Value.Center.X, cached.Value.Center.Z);
        var distanceToCenter = Vector2.Distance(playerPosition, center);
        return new(center, playerPosition, distanceToCenter, distanceToCenter <= StarryMuseRadius);
    }

    private bool CanWalkBackWithinGcd(float distance, out string reason)
    {
        reason = string.Empty;
        if (!rotationSolverActions.TryGetUpcomingGcdTiming(out var action, out var timingReason) ||
            !AoeRepositionPolicy.HasReliableGcdTiming(action.GcdRemaining, action.GcdElapsed, action.GcdTotal))
        {
            reason = $"Starry Muse Smudge held: RSR GCD timing unavailable ({timingReason})";
            return true;
        }

        var requiredSeconds = (distance / EstimatedCombatMoveSpeed) + WalkArrivalBufferSeconds;
        if (action.GcdRemaining < requiredSeconds)
        {
            return false;
        }

        reason = $"walking to Starry Muse within GCD ({distance:0.0}y needs {requiredSeconds:0.0}s, {action.GcdRemaining:0.0}s left)";
        return true;
    }

    private static bool TryCalculateSmudgeLanding(Vector3 playerPosition, Vector2 center, out Vector3 destination)
    {
        var player = new Vector2(playerPosition.X, playerPosition.Z);
        var toCenter = center - player;
        var distanceToCenter = toCenter.Length();
        var dashRange = CombatConstants.FixedForwardGapCloserRange;
        if (distanceToCenter <= 0.01f ||
            distanceToCenter > dashRange + StarryMuseRadius ||
            distanceToCenter < MathF.Max(0f, dashRange - StarryMuseRadius))
        {
            destination = default;
            return false;
        }

        var direction = toCenter / distanceToCenter;
        destination = playerPosition + new Vector3(direction.X, 0f, direction.Y) * dashRange;
        return true;
    }

    private static bool IsActivePictomancer(IBattleChara? player)
    {
        return player != null &&
               player.ClassJob.RowId == PictomancerJobId &&
               !player.IsDead;
    }

    private static bool HasActiveStarryMuseWindow(IBattleChara player)
    {
        return player.StatusList.Any(status =>
            status.RemainingTime > 0f &&
            status.StatusId is ActionUse.PictomancerStarryMuseStatusId or ActionUse.PictomancerHyperphantasiaStatusId or ActionUse.PictomancerInspirationStatusId);
    }

    private readonly record struct CachedStarryMuseZone(Vector3 Center, DateTime CreatedAtUtc, DateTime ExpiresAtUtc);

    private sealed class StarryMuseGoalPlan
    {
        private static readonly MethodInfo ScoreFromWPosMethod =
            typeof(StarryMuseGoalPlan).GetMethod(nameof(ScoreFromWPos), BindingFlags.Instance | BindingFlags.NonPublic)!;

        private readonly Vector2 center;
        private readonly Vector2 preferredEntryPosition;
        private readonly bool playerInZone;

        public StarryMuseGoalPlan(Vector2 center, Vector2 playerPosition, float distanceToCenter, bool playerInZone)
        {
            this.center = center;
            this.preferredEntryPosition = FindPreferredEntryPosition(center, playerPosition, distanceToCenter, playerInZone);
            this.DistanceToCenter = distanceToCenter;
            this.playerInZone = playerInZone;
        }

        public Vector2 CenterPosition => this.center;
        public Vector2 PreferredEntryPosition => this.preferredEntryPosition;
        public float DistanceToCenter { get; }
        public float DistanceToPreferred => Vector2.Distance(this.preferredEntryPosition, this.center) <= 0.01f
            ? this.DistanceToCenter
            : MathF.Max(0f, this.DistanceToCenter - (StarryMuseRadius - ZoneEntryMargin));
        public bool PlayerInZone => this.playerInZone;

        public bool SameSource(StarryMuseGoalPlan other)
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

        public PictomancerStarryMuseOverlaySnapshot CreateOverlay(float y, bool injected)
        {
            return new(
                new Vector3(this.center.X, y, this.center.Y),
                new Vector3(this.preferredEntryPosition.X, y, this.preferredEntryPosition.Y),
                StarryMuseRadius,
                injected,
                this.playerInZone);
        }

        private float ScoreFromWPos(float x, float z)
        {
            var point = new Vector2(x, z);
            var distance = Vector2.Distance(point, this.center);
            if (distance > StarryMuseRadius)
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

            var directionFromCenter = Vector2.Normalize(playerPosition - center);
            return center + directionFromCenter * MathF.Max(0f, StarryMuseRadius - ZoneEntryMargin);
        }
    }
}

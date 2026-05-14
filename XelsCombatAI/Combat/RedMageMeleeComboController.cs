using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Game.ClientState.Objects.Enums;
using Dalamud.Game.ClientState.Objects.Types;
using ECommons.Hooks;
using ECommons.Hooks.ActionEffectTypes;
using FFXIVClientStructs.FFXIV.Client.Game;
using XelsCombatAI.Game;

namespace XelsCombatAI.Combat;

internal sealed record RedMageMeleeComboStatus(
    bool Enabled,
    string Mode,
    string LastReason,
    byte WhiteMana,
    byte BlackMana,
    byte ManaStacks,
    string NextActionName,
    uint NextActionId,
    int AffectedTargets,
    Vector3? CandidateDestination,
    Vector3? LastJumpLanding);

internal sealed class RedMageMeleeComboController : IMovementCandidateSource, IDisposable
{
    internal const string CandidateSource = "Red Mage melee combo";

    private const uint RedMageJobId = 35;
    private const float SingleTargetMeleeSurfaceRange = 2.6f;
    private const float MoulinetConeSurfaceRange = 4.6f;
    private const float RangedSurfaceRange = 25f;
    private const float MoveInAcceptanceRadius = 1.25f;
    private const float AoEMoveInAcceptanceRadius = 1.6f;
    private const float CorpsACorpsMaxRange = 25f;
    private const float DisplacementTargetRange = 5f;
    private const float DisplacementDistance = 15f;
    private const float ExitAttemptMaxSurfaceRange = 10f;
    private const float StayCloseMaxSurfaceRange = 10f;
    private const float MoveInDashSlack = 1.5f;
    private const byte StarterManaThreshold = 50;
    private const byte ContinuationManaThreshold = 15;
    private static readonly TimeSpan ExitWindow = TimeSpan.FromSeconds(4);
    private static readonly TimeSpan ComboTrackWindow = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan JumpAttemptInterval = TimeSpan.FromMilliseconds(250);

    private readonly Configuration config;
    private readonly DalamudServices services;
    private readonly RotationSolverActionReflection rotationSolverActions;
    private readonly BossModReflectionSafety bossModSafety;
    private readonly MobilityDecisionEvaluator mobilityEvaluator;
    private readonly FacingController facingController;
    private readonly Func<bool> currentTargetHasBossModule;

    private DateTime nextJumpAttempt = DateTime.MinValue;
    private DateTime exitUntilUtc = DateTime.MinValue;
    private ulong exitTargetObjectId;
    private ulong stayCloseTargetObjectId;
    private bool stayCloseAfterUnsafeExit;
    private RedMageComboTrack activeComboTrack = RedMageComboTrack.None;
    private DateTime activeComboTrackUntilUtc = DateTime.MinValue;
    private Vector3? lastCandidateDestination;
    private Vector3? lastJumpLanding;
    private RedMageMeleeComboStatus status = new(
        false,
        "inactive",
        "not checked",
        0,
        0,
        0,
        "<none>",
        0,
        0,
        null,
        null);

    public RedMageMeleeComboController(
        Configuration config,
        DalamudServices services,
        RotationSolverActionReflection rotationSolverActions,
        BossModReflectionSafety bossModSafety,
        MobilityDecisionEvaluator mobilityEvaluator,
        FacingController facingController,
        Func<bool> currentTargetHasBossModule)
    {
        this.config = config;
        this.services = services;
        this.rotationSolverActions = rotationSolverActions;
        this.bossModSafety = bossModSafety;
        this.mobilityEvaluator = mobilityEvaluator;
        this.facingController = facingController;
        this.currentTargetHasBossModule = currentTargetHasBossModule;
        ActionEffect.ActionEffectEvent += this.OnActionEffect;
    }

    public RedMageMeleeComboStatus Status => this.status;

    public void Tick()
    {
        _ = this.TryEvaluate(out _);
    }

    public void Reset()
    {
        this.nextJumpAttempt = DateTime.MinValue;
        this.exitUntilUtc = DateTime.MinValue;
        this.exitTargetObjectId = 0;
        this.stayCloseTargetObjectId = 0;
        this.stayCloseAfterUnsafeExit = false;
        this.activeComboTrack = RedMageComboTrack.None;
        this.activeComboTrackUntilUtc = DateTime.MinValue;
        this.lastCandidateDestination = null;
        this.lastJumpLanding = null;
        this.status = this.status with
        {
            Enabled = false,
            Mode = "inactive",
            LastReason = "reset",
            CandidateDestination = null,
            LastJumpLanding = null
        };
    }

    public void Dispose()
    {
        ActionEffect.ActionEffectEvent -= this.OnActionEffect;
    }

    public float? GetTargetUptimeRangeOverride()
    {
        if (!this.TryEvaluate(out var decision))
        {
            return null;
        }

        return decision.Mode switch
        {
            RedMageComboMode.MoveInSingleTarget => SingleTargetMeleeSurfaceRange,
            RedMageComboMode.MoveInAoeCone => MoulinetConeSurfaceRange,
            RedMageComboMode.ExitWithDisplacement => Math.Clamp(decision.CurrentSurfaceDistance, SingleTargetMeleeSurfaceRange, StayCloseMaxSurfaceRange),
            RedMageComboMode.StayCloseAfterExit => Math.Clamp(decision.CurrentSurfaceDistance, SingleTargetMeleeSurfaceRange, StayCloseMaxSurfaceRange),
            _ => null
        };
    }

    public void AddMovementCandidates(MovementPlannerContext context, ICollection<MovementCandidate> candidates)
    {
        if (!this.TryEvaluate(context.Player, context.Target, out var decision) ||
            decision.Mode is not (RedMageComboMode.MoveInSingleTarget or RedMageComboMode.MoveInAoeCone) ||
            decision.Target == null)
        {
            return;
        }

        if (decision.CurrentSurfaceDistance <= decision.DesiredSurfaceDistance + decision.AcceptanceRadius)
        {
            this.lastCandidateDestination = null;
            this.UpdateStatus(decision, "already in combo range");
            return;
        }

        if (this.ShouldHoldMovementForCorpsACorps(decision))
        {
            this.lastCandidateDestination = null;
            this.UpdateStatus(decision, "waiting for Corps-a-corps");
            return;
        }

        var first = true;
        foreach (var destination in this.EnumerateTargetRingDestinations(context, decision.Target, decision.DesiredSurfaceDistance))
        {
            if (first)
            {
                this.lastCandidateDestination = destination;
                first = false;
            }

            var rangeGain = Math.Clamp((decision.CurrentSurfaceDistance - decision.DesiredSurfaceDistance) / 10f, 0.45f, 1f);
            candidates.Add(new(
                CandidateSource,
                decision.Reason,
                destination,
                decision.AcceptanceRadius,
                decision.Mode == RedMageComboMode.MoveInAoeCone ? MovementCandidatePriority.ActiveAoe : MovementCandidatePriority.TargetRange,
                1f,
                rangeGain,
                decision.Mode == RedMageComboMode.MoveInAoeCone ? 1f : 0f,
                0.75f));
        }

        this.UpdateStatus(decision, decision.Reason);
    }

    public unsafe bool TryUseComboJump()
    {
        if (DateTime.UtcNow < this.nextJumpAttempt)
        {
            return false;
        }

        this.nextJumpAttempt = DateTime.UtcNow.Add(JumpAttemptInterval);
        if (!this.TryEvaluate(out var decision))
        {
            return false;
        }

        if (decision.Mode == RedMageComboMode.ExitWithDisplacement)
        {
            return this.TryUseDisplacementExit(decision);
        }

        if (decision.Mode is RedMageComboMode.MoveInSingleTarget or RedMageComboMode.MoveInAoeCone)
        {
            return this.TryUseCorpsACorpsMoveIn(decision);
        }

        return false;
    }

    private unsafe bool TryUseCorpsACorpsMoveIn(RedMageComboDecision decision)
    {
        var player = decision.Player;
        var target = decision.Target;
        if (target == null)
        {
            this.UpdateStatus(decision, "jump rejected: missing target");
            return false;
        }

        if (player.IsCasting)
        {
            this.UpdateStatus(decision, "jump rejected: player casting");
            return false;
        }

        if (ActionUse.HasAnimationLock())
        {
            this.UpdateStatus(decision, "jump rejected: animation lock");
            return false;
        }

        if (decision.CurrentSurfaceDistance <= decision.DesiredSurfaceDistance + MoveInDashSlack)
        {
            this.UpdateStatus(decision, "already in combo range");
            return false;
        }

        if (decision.CurrentSurfaceDistance > CorpsACorpsMaxRange)
        {
            this.UpdateStatus(decision, "jump rejected: target too far");
            return false;
        }

        if (!ActionUse.CanUseAction(ActionUse.RedMageCorpsACorpsActionId))
        {
            this.UpdateStatus(decision, "jump rejected: Corps-a-corps unavailable");
            return false;
        }

        if (!bossModSafety.TryCanAttemptDashNow(out var dashReason))
        {
            this.UpdateStatus(decision, $"jump rejected: {dashReason}");
            return false;
        }

        if (!Geometry.TryCalculateTargetDashDestination(player.Position, target.Position, decision.CurrentSurfaceDistance, out var destination))
        {
            this.UpdateStatus(decision, "jump rejected: could not calculate Corps-a-corps landing");
            return false;
        }

        if (!this.TryValidateStrictSafeDash(
                player,
                target,
                destination,
                MobilityIntent.Uptime,
                "Corps-a-corps",
                ActionUse.RedMageCorpsACorpsActionId,
                requireUptimeProgress: true,
                out var validation))
        {
            this.UpdateStatus(decision, $"jump rejected: {validation.RiskReason}");
            return false;
        }

        var used = ActionManager.Instance()->UseAction(ActionType.Action, ActionUse.RedMageCorpsACorpsActionId, target.GameObjectId);
        mobilityEvaluator.RecordActionResult(validation, used, used ? "action used" : "action failed");
        if (used)
        {
            this.lastJumpLanding = destination;
            this.UpdateStatus(decision, "Corps-a-corps in");
            return true;
        }

        this.UpdateStatus(decision, "jump rejected: Corps-a-corps failed");
        return false;
    }

    private unsafe bool TryUseDisplacementExit(RedMageComboDecision decision)
    {
        var player = decision.Player;
        var target = decision.Target;
        if (target == null)
        {
            this.EnterStayCloseAfterExit(0, "staying after unsafe exit: missing target");
            this.UpdateStatus(decision, "staying after unsafe exit: missing target");
            return false;
        }

        if (player.IsCasting)
        {
            this.UpdateStatus(decision, "jump rejected: player casting");
            return false;
        }

        if (ActionUse.HasAnimationLock())
        {
            this.UpdateStatus(decision, "jump rejected: animation lock");
            return false;
        }

        if (decision.CurrentSurfaceDistance > DisplacementTargetRange)
        {
            this.EnterStayCloseAfterExit(target.GameObjectId, "staying after unsafe exit: target outside Displacement range");
            this.UpdateStatus(decision, "staying after unsafe exit: target outside Displacement range");
            return false;
        }

        if (!ActionUse.CanUseAction(ActionUse.RedMageDisplacementActionId))
        {
            this.EnterStayCloseAfterExit(target.GameObjectId, "staying after unsafe exit: Displacement unavailable");
            this.UpdateStatus(decision, "staying after unsafe exit: Displacement unavailable");
            return false;
        }

        if (!bossModSafety.TryCanAttemptDashNow(out var dashReason))
        {
            this.EnterStayCloseAfterExit(target.GameObjectId, $"staying after unsafe exit: {dashReason}");
            this.UpdateStatus(decision, $"staying after unsafe exit: {dashReason}");
            return false;
        }

        if (!TryCalculateTargetBackstepDestination(player, target, DisplacementDistance, out var destination))
        {
            this.EnterStayCloseAfterExit(target.GameObjectId, "staying after unsafe exit: could not calculate Displacement landing");
            this.UpdateStatus(decision, "staying after unsafe exit: could not calculate Displacement landing");
            return false;
        }

        if (!this.TryValidateStrictSafeDash(
                player,
                target,
                destination,
                MobilityIntent.Safety,
                "Displacement",
                ActionUse.RedMageDisplacementActionId,
                requireUptimeProgress: false,
                out var validation))
        {
            if (IsTransientNavigationValidationFailure(validation.RiskReason))
            {
                this.UpdateStatus(decision, $"jump rejected: {validation.RiskReason}");
                return false;
            }

            this.EnterStayCloseAfterExit(target.GameObjectId, $"staying after unsafe exit: {validation.RiskReason}");
            this.UpdateStatus(decision, $"staying after unsafe exit: {validation.RiskReason}");
            return false;
        }

        var desiredRotation = Geometry.DirectionToRotation(target.Position - player.Position);
        if (Geometry.AbsAngleDelta(player.Rotation, desiredRotation) > FacingController.DirectionalDashToleranceRadians)
        {
            if (this.ShouldStopWaitingForDisplacementFacing())
            {
                this.EnterStayCloseAfterExit(target.GameObjectId, "staying after unsafe exit: facing setup blocked");
                this.UpdateStatus(decision, "staying after unsafe exit: facing setup blocked");
                return false;
            }

            facingController.RequestFacing(FacingController.CreateDirectionalDashRequest(desiredRotation, destination, "turn for Displacement", FacingBossModPolicy.AssistValidatedDash));
            this.lastJumpLanding = destination;
            this.UpdateStatus(decision, $"turning for Displacement ({validation.IntentLabel})");
            return false;
        }

        var used = ActionManager.Instance()->UseAction(ActionType.Action, ActionUse.RedMageDisplacementActionId, target.GameObjectId);
        mobilityEvaluator.RecordActionResult(validation, used, used ? "action used" : "action failed");
        if (used)
        {
            this.exitUntilUtc = DateTime.MinValue;
            this.exitTargetObjectId = 0;
            this.stayCloseAfterUnsafeExit = false;
            this.stayCloseTargetObjectId = 0;
            this.lastJumpLanding = destination;
            this.UpdateStatus(decision, "Displacement out");
            return true;
        }

        this.EnterStayCloseAfterExit(target.GameObjectId, "staying after unsafe exit: Displacement failed");
        this.UpdateStatus(decision, "staying after unsafe exit: Displacement failed");
        return false;
    }

    private bool TryValidateStrictSafeDash(
        IBattleChara player,
        IBattleChara target,
        Vector3 destination,
        MobilityIntent intent,
        string actionName,
        uint actionId,
        bool requireUptimeProgress,
        out MobilityDecisionDiagnostics decision)
    {
        var safeMovementDestination = bossModSafety.TryGetSafeMovementIntent(player.Position, out var safeDestination, out _)
            ? safeDestination
            : (Vector3?)null;
        if (!mobilityEvaluator.TryValidateDashDestination(
                player,
                destination,
                target,
                safeMovementDestination,
                intent,
                actionName,
                actionId,
                0f,
                requireSafetyProgress: false,
                requireUptimeProgress: requireUptimeProgress,
                requireVnavReachable: false,
                out decision))
        {
            return false;
        }

        if (mobilityEvaluator.TryValidateStrictLandingSupport(player.Position, destination, out var supportReason))
        {
            return true;
        }

        decision = mobilityEvaluator.RecordActionResult(decision, false, supportReason);
        return false;
    }

    private bool ShouldHoldMovementForCorpsACorps(RedMageComboDecision decision)
    {
        if (decision.Target == null ||
            decision.CurrentSurfaceDistance <= decision.DesiredSurfaceDistance + MoveInDashSlack ||
            decision.CurrentSurfaceDistance > CorpsACorpsMaxRange)
        {
            return false;
        }

        unsafe
        {
            return ActionUse.CanUseAction(ActionUse.RedMageCorpsACorpsActionId);
        }
    }

    private static bool IsTransientNavigationValidationFailure(string reason)
    {
        return reason.Contains("pending", StringComparison.OrdinalIgnoreCase) ||
               reason.Contains("queued", StringComparison.OrdinalIgnoreCase) ||
               reason.Contains("in progress", StringComparison.OrdinalIgnoreCase);
    }

    private bool ShouldStopWaitingForDisplacementFacing()
    {
        var facing = facingController.Status;
        return facing.Source == FacingRequestSource.DirectionalDash &&
               string.Equals(facing.Reason, "turn for Displacement", StringComparison.Ordinal) &&
               !facing.Applied &&
               !string.IsNullOrWhiteSpace(facing.RejectionReason) &&
               !string.Equals(facing.RejectionReason, "within tolerance", StringComparison.Ordinal);
    }

    private bool TryEvaluate(out RedMageComboDecision decision)
    {
        return this.TryEvaluate(services.ObjectTable.LocalPlayer, services.TargetManager.Target as IBattleChara, out decision);
    }

    private unsafe bool TryEvaluate(IBattleChara? player, IBattleChara? target, out RedMageComboDecision decision)
    {
        decision = default;
        this.lastCandidateDestination = null;

        if (!this.IsFeatureEnabled(player, out var disabledReason))
        {
            this.ClearTransientStates();
            this.UpdateStatus(false, "inactive", disabledReason, 0, 0, 0, "<none>", 0, 0);
            return false;
        }

        if (target == null || target.GameObjectId == 0 || target.IsDead || target.CurrentHp <= 0)
        {
            this.ClearTransientStates();
            this.UpdateStatus(true, "ranged", "missing target", 0, 0, 0, "<none>", 0, 0);
            return false;
        }

        if (target is IBattleNpc npc && npc.BattleNpcKind != BattleNpcSubKind.Combatant)
        {
            this.ClearTransientStates();
            this.UpdateStatus(true, "ranged", "target is not attackable", 0, 0, 0, "<none>", 0, 0);
            return false;
        }

        if (!this.TryGetRedMageGauge(out var whiteMana, out var blackMana, out var manaStacks))
        {
            this.ClearTransientStates();
            this.UpdateStatus(true, "ranged", "RDM gauge unavailable", 0, 0, 0, "<none>", 0, 0);
            return false;
        }

        var now = DateTime.UtcNow;
        var currentSurface = Geometry.DistanceToHitbox(player!.Position, player.HitboxRadius, target.Position, target.HitboxRadius);
        var exitPending = this.exitTargetObjectId == target.GameObjectId && now <= this.exitUntilUtc;
        if (this.exitTargetObjectId != 0 && (this.exitTargetObjectId != target.GameObjectId || now > this.exitUntilUtc))
        {
            if (this.exitTargetObjectId == target.GameObjectId && currentSurface <= StayCloseMaxSurfaceRange)
            {
                this.EnterStayCloseAfterExit(target.GameObjectId, "staying after unsafe exit: exit window expired");
            }
            else
            {
                this.exitTargetObjectId = 0;
                this.exitUntilUtc = DateTime.MinValue;
            }
        }

        if (manaStacks >= 3 || (manaStacks == 0 && now > this.activeComboTrackUntilUtc))
        {
            this.activeComboTrack = RedMageComboTrack.None;
            this.activeComboTrackUntilUtc = DateTime.MinValue;
        }

        var hasRsrAction = rotationSolverActions.TryGetUpcomingGcd(requirePreview: false, out var nextAction, out var rsrReason);
        if (!hasRsrAction && !string.Equals(rotationSolverActions.Status, "available", StringComparison.Ordinal))
        {
            if (this.TryBuildStayCloseDecision(player, target, currentSurface, whiteMana, blackMana, manaStacks, "<none>", 0, 0, $"RSR unavailable: {rsrReason}", out decision))
            {
                return true;
            }

            this.UpdateStatus(true, "ranged", $"RSR unavailable: {rsrReason}", whiteMana, blackMana, manaStacks, "<none>", 0, 0);
            return false;
        }

        var actionId = hasRsrAction ? ResolveActionId(nextAction!) : 0;
        var actionName = hasRsrAction ? nextAction!.ActionName : "<none>";
        var affectedTargets = hasRsrAction ? nextAction!.AffectedTargetCount : 0;
        if (exitPending && currentSurface <= ExitAttemptMaxSurfaceRange)
        {
            decision = new(
                RedMageComboMode.ExitWithDisplacement,
                player,
                target,
                currentSurface,
                currentSurface,
                MoveInAcceptanceRadius,
                whiteMana,
                blackMana,
                manaStacks,
                actionName,
                actionId,
                affectedTargets,
                "Displacement out");
            this.UpdateStatus(decision, "Displacement out");
            return true;
        }

        var hasStarterMana = whiteMana >= StarterManaThreshold && blackMana >= StarterManaThreshold;
        var hasContinuationMana = whiteMana >= ContinuationManaThreshold && blackMana >= ContinuationManaThreshold;
        var hasManaficationRange = HasStatus(player, ActionUse.RedMageManaficationStatusId);
        var hasMagickedSwordplay = HasStatus(player, ActionUse.RedMageMagickedSwordplayStatusId);
        var hasContinuationResources = hasContinuationMana || hasMagickedSwordplay;
        var hasPendingInstantSpell = HasPendingInstantSpell(player);
        var knownBoss = this.currentTargetHasBossModule();
        var aoeTrash = !knownBoss && affectedTargets >= 3;
        var continuationTrack = this.ResolveContinuationTrack(actionId, aoeTrash, manaStacks);

        if (continuationTrack == RedMageComboTrack.AoE && hasContinuationResources)
        {
            var continuationActionId = IsMoulinetComboAction(actionId)
                ? actionId
                : GetExpectedMoulinetComboActionId(manaStacks);
            decision = new(
                RedMageComboMode.MoveInAoeCone,
                player,
                target,
                currentSurface,
                MoulinetConeSurfaceRange,
                AoEMoveInAcceptanceRadius,
                whiteMana,
                blackMana,
                manaStacks,
                IsMoulinetComboAction(actionId) ? actionName : GetRedMageActionName(continuationActionId),
                continuationActionId,
                affectedTargets,
                "holding Moulinet cone");
            this.UpdateStatus(decision, decision.Reason);
            return true;
        }

        if (aoeTrash && (hasStarterMana || hasMagickedSwordplay))
        {
            if (hasPendingInstantSpell)
            {
                this.ClearStayCloseIfRanged(target.GameObjectId, currentSurface);
                this.UpdateStatus(true, "ranged", "waiting for instant spell before Moulinet", whiteMana, blackMana, manaStacks, actionName, actionId, affectedTargets);
                return false;
            }

            decision = new(
                RedMageComboMode.MoveInAoeCone,
                player,
                target,
                currentSurface,
                MoulinetConeSurfaceRange,
                AoEMoveInAcceptanceRadius,
                whiteMana,
                blackMana,
                manaStacks,
                IsMoulinetStart(actionId) ? actionName : "Enchanted Moulinet (inferred)",
                IsMoulinetStart(actionId) ? actionId : ActionUse.RedMageEnchantedMoulinetActionId,
                affectedTargets,
                IsMoulinetStart(actionId) ? "Moulinet cone" : "pending Moulinet");
            this.UpdateStatus(decision, decision.Reason);
            return true;
        }

        if (aoeTrash && !hasStarterMana && !hasMagickedSwordplay)
        {
            this.ClearStayCloseIfRanged(target.GameObjectId, currentSurface);
            this.UpdateStatus(true, "ranged", "AoE trash: waiting for 50/50 or Magicked Swordplay", whiteMana, blackMana, manaStacks, actionName, actionId, affectedTargets);
            return false;
        }

        if (hasManaficationRange || IsManaficationSingleTargetComboAction(actionId))
        {
            this.ClearStayCloseIfRanged(target.GameObjectId, currentSurface);
            this.UpdateStatus(true, "ranged", "Manafication range buff active", whiteMana, blackMana, manaStacks, actionName, actionId, affectedTargets);
            return false;
        }

        if (continuationTrack == RedMageComboTrack.SingleTarget && hasContinuationResources)
        {
            var continuationActionId = IsSingleTargetMeleeComboAction(actionId)
                ? actionId
                : GetExpectedSingleTargetComboActionId(manaStacks);
            decision = new(
                RedMageComboMode.MoveInSingleTarget,
                player,
                target,
                currentSurface,
                SingleTargetMeleeSurfaceRange,
                MoveInAcceptanceRadius,
                whiteMana,
                blackMana,
                manaStacks,
                IsSingleTargetMeleeComboAction(actionId) ? actionName : GetRedMageActionName(continuationActionId),
                continuationActionId,
                affectedTargets,
                "holding melee combo");
            this.UpdateStatus(decision, decision.Reason);
            return true;
        }

        if (hasStarterMana)
        {
            if (hasPendingInstantSpell)
            {
                this.ClearStayCloseIfRanged(target.GameObjectId, currentSurface);
                this.UpdateStatus(true, "ranged", "waiting for instant spell before Riposte", whiteMana, blackMana, manaStacks, actionName, actionId, affectedTargets);
                return false;
            }

            decision = new(
                RedMageComboMode.MoveInSingleTarget,
                player,
                target,
                currentSurface,
                SingleTargetMeleeSurfaceRange,
                MoveInAcceptanceRadius,
                whiteMana,
                blackMana,
                manaStacks,
                IsRiposte(actionId) ? actionName : "Enchanted Riposte (inferred)",
                IsRiposte(actionId) ? actionId : ActionUse.RedMageEnchantedRiposteActionId,
                affectedTargets,
                IsRiposte(actionId) ? "pending Riposte" : "pending Riposte from mana");
            this.UpdateStatus(decision, decision.Reason);
            return true;
        }

        if (!hasStarterMana)
        {
            if (this.TryBuildStayCloseDecision(player, target, currentSurface, whiteMana, blackMana, manaStacks, actionName, actionId, affectedTargets, "staying after unsafe exit", out decision))
            {
                return true;
            }

            this.UpdateStatus(true, "ranged", "waiting for 50/50 mana", whiteMana, blackMana, manaStacks, actionName, actionId, affectedTargets);
            return false;
        }

        this.UpdateStatus(true, "ranged", "RDM state does not need melee combo range", whiteMana, blackMana, manaStacks, actionName, actionId, affectedTargets);
        return false;
    }

    private bool TryBuildStayCloseDecision(
        IBattleChara player,
        IBattleChara target,
        float currentSurface,
        byte whiteMana,
        byte blackMana,
        byte manaStacks,
        string actionName,
        uint actionId,
        int affectedTargets,
        string reason,
        out RedMageComboDecision decision)
    {
        if (!this.stayCloseAfterUnsafeExit || this.stayCloseTargetObjectId != target.GameObjectId || currentSurface > StayCloseMaxSurfaceRange)
        {
            decision = default;
            if (currentSurface > RangedSurfaceRange - 1f)
            {
                this.ClearTransientStates();
            }

            return false;
        }

        decision = new(
            RedMageComboMode.StayCloseAfterExit,
            player,
            target,
            currentSurface,
            Math.Clamp(currentSurface, SingleTargetMeleeSurfaceRange, StayCloseMaxSurfaceRange),
            MoveInAcceptanceRadius,
            whiteMana,
            blackMana,
            manaStacks,
            actionName,
            actionId,
            affectedTargets,
            reason);
        this.UpdateStatus(decision, reason);
        return true;
    }

    private void EnterStayCloseAfterExit(ulong targetObjectId, string reason)
    {
        this.exitUntilUtc = DateTime.MinValue;
        this.exitTargetObjectId = 0;
        this.stayCloseAfterUnsafeExit = true;
        this.stayCloseTargetObjectId = targetObjectId;
        this.status = this.status with
        {
            Mode = "staying after unsafe exit",
            LastReason = reason
        };
    }

    private void ClearTransientStates()
    {
        this.exitUntilUtc = DateTime.MinValue;
        this.exitTargetObjectId = 0;
        this.stayCloseAfterUnsafeExit = false;
        this.stayCloseTargetObjectId = 0;
        this.activeComboTrack = RedMageComboTrack.None;
        this.activeComboTrackUntilUtc = DateTime.MinValue;
    }

    private void ClearStayCloseIfRanged(ulong targetObjectId, float currentSurface)
    {
        if (this.stayCloseAfterUnsafeExit && this.stayCloseTargetObjectId == targetObjectId && currentSurface >= RangedSurfaceRange - 1f)
        {
            this.ClearTransientStates();
        }
    }

    private bool IsFeatureEnabled(IBattleChara? player, out string reason)
    {
        if (!config.Enabled)
        {
            reason = "disabled";
            return false;
        }

        if (!config.ManageMovement)
        {
            reason = "movement management disabled";
            return false;
        }

        if (!config.UseRedMageMeleeComboMovement)
        {
            reason = "RDM melee combo movement disabled";
            return false;
        }

        if (!CombatEngagementDetector.IsEffectivelyInCombat(services))
        {
            reason = "not in combat";
            return false;
        }

        if (services.Condition[ConditionFlag.Unconscious] || player == null || player.IsDead || player.CurrentHp <= 0)
        {
            reason = "player unavailable";
            return false;
        }

        if (player.ClassJob.RowId != RedMageJobId)
        {
            reason = "not Red Mage";
            return false;
        }

        reason = "enabled";
        return true;
    }

    private IEnumerable<Vector3> EnumerateTargetRingDestinations(MovementPlannerContext context, IBattleChara target, float desiredSurfaceDistance)
    {
        var centerDistance = context.PlayerHitboxRadius + target.HitboxRadius + desiredSurfaceDistance;
        var target2 = new Vector2(target.Position.X, target.Position.Z);
        var player2 = new Vector2(context.PlayerPosition.X, context.PlayerPosition.Z);
        var fromTargetToPlayer = player2 - target2;
        if (fromTargetToPlayer.LengthSquared() <= 0.01f)
        {
            fromTargetToPlayer = new Vector2(0f, -1f);
        }

        fromTargetToPlayer = Vector2.Normalize(fromTargetToPlayer);
        foreach (var direction in EnumerateHumanLikeTargetDirections(target, fromTargetToPlayer))
        {
            var destination2 = target2 + direction * centerDistance;
            yield return new Vector3(destination2.X, target.Position.Y, destination2.Y);
        }
    }

    private static IEnumerable<Vector2> EnumerateHumanLikeTargetDirections(IBattleChara target, Vector2 fromTargetToPlayer)
    {
        yield return fromTargetToPlayer;

        var targetForward = new Vector2(MathF.Sin(target.Rotation), MathF.Cos(target.Rotation));
        var targetBack = -targetForward;
        var targetLeft = new Vector2(-targetForward.Y, targetForward.X);
        var targetRight = -targetLeft;
        yield return targetBack;
        yield return Vector2.Normalize(targetBack + targetLeft);
        yield return Vector2.Normalize(targetBack + targetRight);
        yield return targetLeft;
        yield return targetRight;
    }

    private RedMageComboTrack ResolveContinuationTrack(uint actionId, bool aoeTrash, byte manaStacks)
    {
        if (manaStacks is not (1 or 2))
        {
            return RedMageComboTrack.None;
        }

        var actionTrack = GetActionComboTrack(actionId);
        if (actionTrack != RedMageComboTrack.None)
        {
            return actionTrack;
        }

        if (this.activeComboTrack != RedMageComboTrack.None)
        {
            return this.activeComboTrack;
        }

        return aoeTrash ? RedMageComboTrack.AoE : RedMageComboTrack.SingleTarget;
    }

    private void OnActionEffect(ActionEffectSet set)
    {
        try
        {
            var actionId = set.Header.ActionID;
            var comboTrack = GetActionComboTrack(actionId);
            if (comboTrack == RedMageComboTrack.None)
            {
                return;
            }

            var player = services.ObjectTable.LocalPlayer;
            if (player == null ||
                !this.IsFeatureEnabled(player, out _) ||
                set.Source?.GameObjectId != player.GameObjectId ||
                services.TargetManager.Target is not IBattleChara target ||
                target.GameObjectId == 0)
            {
                return;
            }

            this.activeComboTrack = IsComboFinisher(actionId) ? RedMageComboTrack.None : comboTrack;
            this.activeComboTrackUntilUtc = IsComboFinisher(actionId) ? DateTime.MinValue : DateTime.UtcNow.Add(ComboTrackWindow);
            if (!IsComboFinisher(actionId))
            {
                this.status = this.status with
                {
                    Enabled = true,
                    Mode = comboTrack == RedMageComboTrack.AoE ? "Moulinet cone" : "holding melee combo",
                    LastReason = $"{GetRedMageActionName(actionId)} landed"
                };
                return;
            }

            var currentSurface = Geometry.DistanceToHitbox(player.Position, player.HitboxRadius, target.Position, target.HitboxRadius);
            if (currentSurface > ExitAttemptMaxSurfaceRange)
            {
                return;
            }

            this.exitTargetObjectId = target.GameObjectId;
            this.exitUntilUtc = DateTime.UtcNow.Add(ExitWindow);
            this.stayCloseAfterUnsafeExit = false;
            this.stayCloseTargetObjectId = 0;
            this.status = this.status with
            {
                Enabled = true,
                Mode = "Displacement out",
                LastReason = $"{GetRedMageActionName(actionId)} landed"
            };
        }
        catch (Exception ex)
        {
            services.Log.Verbose($"RDM melee combo action-effect tracking failed: {ex.Message}");
        }
    }

    private unsafe bool TryGetRedMageGauge(out byte whiteMana, out byte blackMana, out byte manaStacks)
    {
        var gaugeManager = JobGaugeManager.Instance();
        if (gaugeManager == null)
        {
            whiteMana = 0;
            blackMana = 0;
            manaStacks = 0;
            return false;
        }

        var gauge = gaugeManager->RedMage;
        whiteMana = gauge.WhiteMana;
        blackMana = gauge.BlackMana;
        manaStacks = gauge.ManaStacks;
        return true;
    }

    private static bool HasStatus(IBattleChara player, uint statusId)
    {
        return player.StatusList.Any(status => status.StatusId == statusId && status.RemainingTime > 0f);
    }

    private static bool HasAnyStatus(IBattleChara player, params uint[] statusIds)
    {
        return player.StatusList.Any(status => status.RemainingTime > 0f && statusIds.Contains(status.StatusId));
    }

    private static bool HasPendingInstantSpell(IBattleChara player)
    {
        return HasAnyStatus(
            player,
            ActionUse.RedMageDualcastStatusId,
            ActionUse.RedMageAlternateDualcastStatusId,
            ActionUse.RedMageAccelerationStatusId,
            ActionUse.RedMageGrandImpactReadyStatusId,
            ActionUse.SwiftcastStatusId);
    }

    private static uint ResolveActionId(RsrAoeActionSnapshot action)
    {
        return action.AdjustedActionId != 0 ? action.AdjustedActionId : action.ActionId;
    }

    private static bool IsRiposte(uint actionId)
    {
        return actionId == ActionUse.RedMageEnchantedRiposteActionId;
    }

    private static uint GetExpectedSingleTargetComboActionId(byte manaStacks)
    {
        return manaStacks switch
        {
            1 => ActionUse.RedMageEnchantedZwerchhauActionId,
            2 => ActionUse.RedMageEnchantedRedoublementActionId,
            _ => ActionUse.RedMageEnchantedRiposteActionId
        };
    }

    private static uint GetExpectedMoulinetComboActionId(byte manaStacks)
    {
        return manaStacks switch
        {
            1 => ActionUse.RedMageEnchantedMoulinetDeuxActionId,
            2 => ActionUse.RedMageEnchantedMoulinetTroisActionId,
            _ => ActionUse.RedMageEnchantedMoulinetActionId
        };
    }

    private static string GetRedMageActionName(uint actionId)
    {
        return actionId switch
        {
            ActionUse.RedMageEnchantedRiposteActionId => "Enchanted Riposte",
            ActionUse.RedMageEnchantedZwerchhauActionId => "Enchanted Zwerchhau",
            ActionUse.RedMageEnchantedRedoublementActionId => "Enchanted Redoublement",
            ActionUse.RedMageEnchantedMoulinetActionId => "Enchanted Moulinet",
            ActionUse.RedMageEnchantedMoulinetDeuxActionId => "Enchanted Moulinet Deux",
            ActionUse.RedMageEnchantedMoulinetTroisActionId => "Enchanted Moulinet Trois",
            ActionUse.RedMageManaficationRiposteActionId => "Enchanted Riposte",
            ActionUse.RedMageManaficationZwerchhauActionId => "Enchanted Zwerchhau",
            ActionUse.RedMageManaficationRedoublementActionId => "Enchanted Redoublement",
            _ => actionId.ToString(System.Globalization.CultureInfo.InvariantCulture)
        };
    }

    private static bool IsSingleTargetMeleeComboAction(uint actionId)
    {
        return actionId is
            ActionUse.RedMageEnchantedRiposteActionId or
            ActionUse.RedMageEnchantedZwerchhauActionId or
            ActionUse.RedMageEnchantedRedoublementActionId;
    }

    private static RedMageComboTrack GetActionComboTrack(uint actionId)
    {
        if (IsSingleTargetMeleeComboAction(actionId) || IsManaficationSingleTargetComboAction(actionId))
        {
            return RedMageComboTrack.SingleTarget;
        }

        return IsMoulinetComboAction(actionId) ? RedMageComboTrack.AoE : RedMageComboTrack.None;
    }

    private static bool IsComboFinisher(uint actionId)
    {
        return actionId is
            ActionUse.RedMageEnchantedRedoublementActionId or
            ActionUse.RedMageManaficationRedoublementActionId or
            ActionUse.RedMageEnchantedMoulinetTroisActionId;
    }

    private static bool IsManaficationSingleTargetComboAction(uint actionId)
    {
        return actionId is
            ActionUse.RedMageManaficationRiposteActionId or
            ActionUse.RedMageManaficationZwerchhauActionId or
            ActionUse.RedMageManaficationRedoublementActionId;
    }

    private static bool IsMoulinetComboAction(uint actionId)
    {
        return actionId is
            ActionUse.RedMageEnchantedMoulinetActionId or
            ActionUse.RedMageEnchantedMoulinetDeuxActionId or
            ActionUse.RedMageEnchantedMoulinetTroisActionId;
    }

    private static bool IsMoulinetStart(uint actionId)
    {
        return actionId == ActionUse.RedMageEnchantedMoulinetActionId;
    }

    private static bool IsMoulinetContinuation(uint actionId)
    {
        return actionId is ActionUse.RedMageEnchantedMoulinetDeuxActionId or ActionUse.RedMageEnchantedMoulinetTroisActionId;
    }

    private static bool TryCalculateTargetBackstepDestination(IBattleChara player, IBattleChara target, float backstepDistance, out Vector3 destination)
    {
        var awayFromTarget = player.Position - target.Position;
        awayFromTarget.Y = 0f;
        if (awayFromTarget.LengthSquared() <= 0.0001f)
        {
            destination = default;
            return false;
        }

        awayFromTarget = Vector3.Normalize(awayFromTarget);
        destination = player.Position + awayFromTarget * backstepDistance;
        return true;
    }

    private void UpdateStatus(RedMageComboDecision decision, string reason)
    {
        this.UpdateStatus(
            true,
            decision.Mode switch
            {
                RedMageComboMode.MoveInSingleTarget => decision.Reason,
                RedMageComboMode.MoveInAoeCone => "Moulinet cone",
                RedMageComboMode.ExitWithDisplacement => "Displacement out",
                RedMageComboMode.StayCloseAfterExit => "staying after unsafe exit",
                _ => "ranged"
            },
            reason,
            decision.WhiteMana,
            decision.BlackMana,
            decision.ManaStacks,
            decision.NextActionName,
            decision.NextActionId,
            decision.AffectedTargets);
    }

    private void UpdateStatus(
        bool enabled,
        string mode,
        string reason,
        byte whiteMana,
        byte blackMana,
        byte manaStacks,
        string nextActionName,
        uint nextActionId,
        int affectedTargets)
    {
        this.status = new(
            enabled,
            mode,
            reason,
            whiteMana,
            blackMana,
            manaStacks,
            nextActionName,
            nextActionId,
            affectedTargets,
            this.lastCandidateDestination,
            this.lastJumpLanding);
    }

    private enum RedMageComboMode
    {
        None,
        MoveInSingleTarget,
        MoveInAoeCone,
        ExitWithDisplacement,
        StayCloseAfterExit
    }

    private enum RedMageComboTrack
    {
        None,
        SingleTarget,
        AoE
    }

    private readonly record struct RedMageComboDecision(
        RedMageComboMode Mode,
        IBattleChara Player,
        IBattleChara? Target,
        float CurrentSurfaceDistance,
        float DesiredSurfaceDistance,
        float AcceptanceRadius,
        byte WhiteMana,
        byte BlackMana,
        byte ManaStacks,
        string NextActionName,
        uint NextActionId,
        int AffectedTargets,
        string Reason);
}

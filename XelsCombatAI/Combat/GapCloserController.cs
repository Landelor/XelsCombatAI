using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Dalamud.Game.ClientState.Objects.Enums;
using Dalamud.Game.ClientState.Objects.Types;
using FFXIVClientStructs.FFXIV.Client.Game;
using XelsCombatAI.Game;

namespace XelsCombatAI.Combat;

internal sealed class GapCloserController(
    Configuration config,
    DalamudServices services,
    BossModIpc bossMod,
    BossModReflectionSafety bossModSafety,
    JobRangeProvider jobRangeProvider,
    MobilityDecisionEvaluator mobilityEvaluator,
    DashStyleController dashStyleController,
    FacingController facingController,
    RotationSolverActionReflection rotationSolverActions,
    Func<bool> rsrHenchedActive,
    Func<TrashPullDiagnostics> trashPullDiagnostics)
{
    private const float BossLikeHitboxRadius = 4f;
    private const float DirectionalDashBetterLandingThreshold = 0.75f;
    private const float ConservativeTrashGapCloserRangeReserve = 2.5f;
    private const float ConservativeTrashMinimumConfidence = 0.55f;
    private const int ConservativeTrashMinimumTargets = 3;
    private const float FriendlyAnchorMinimumMoveDistance = 6f;
    private const float FriendlyAnchorMinimumUptimeGain = 4f;
    private const float HostileRelayMinimumTargetGain = 4f;
    private const float HostileRelayMinimumDirectionDot = 0.45f;
    private readonly record struct DirectionalDashFacingCandidate(float Rotation, Vector3 Destination);

    private DateTime nextGapCloserAttempt = DateTime.MinValue;
    private string lastGapCloserSafety = "not checked";
    private Vector3? lastSafeLandingPosition;

    public string LastGapCloserSafety => this.lastGapCloserSafety;
    public Vector3? LastSafeLandingPosition => this.lastSafeLandingPosition;

    public void Reset()
    {
        this.nextGapCloserAttempt = DateTime.MinValue;
        this.lastGapCloserSafety = "not checked";
        this.lastSafeLandingPosition = null;
    }

    public unsafe bool TryUseReengageGapCloser()
    {
        if (DateTime.UtcNow < this.nextGapCloserAttempt)
        {
            return false;
        }

        this.nextGapCloserAttempt = DateTime.UtcNow.AddMilliseconds(250);

        var player = services.ObjectTable.LocalPlayer;
        if (player == null)
        {
            this.lastGapCloserSafety = "missing player";
            return false;
        }

        var target = services.TargetManager.Target;
        if (this.TryResolveRsrAlignedReengageTarget(out var rsrTarget, out var rsrTargetReason))
        {
            target = rsrTarget;
        }
        else if (rsrTargetReason.Length > 0)
        {
            this.lastGapCloserSafety = rsrTargetReason;
            this.lastSafeLandingPosition = null;
            mobilityEvaluator.RecordIdle(MobilityIntent.Uptime, "Gap closer", rsrTargetReason);
            return false;
        }

        if (player.IsCasting)
        {
            this.lastGapCloserSafety = "player casting";
            return false;
        }

        if (ActionManager.Instance()->AnimationLock > 0)
        {
            this.lastGapCloserSafety = "animation lock";
            return false;
        }

        if (IsNinjaMudraWindow(player))
        {
            this.lastGapCloserSafety = "NIN mudra active";
            this.lastSafeLandingPosition = null;
            mobilityEvaluator.RecordIdle(MobilityIntent.Uptime, "Gap closer", this.lastGapCloserSafety);
            return false;
        }

        if (this.TryUsePairedReturn(player, target as IBattleNpc))
        {
            return true;
        }

        if (target == null)
        {
            this.lastGapCloserSafety = "missing target";
            return false;
        }

        if (target is not IBattleNpc battleNpc || battleNpc.BattleNpcKind != BattleNpcSubKind.Combatant)
        {
            this.lastGapCloserSafety = "target is not attackable";
            return false;
        }

        if (config.GuardUnknownBossNavigationWithVnavmesh && this.IsUnknownBossLikeTarget(target))
        {
            this.lastGapCloserSafety = "unknown boss module; gap closer disabled";
            this.lastSafeLandingPosition = null;
            return false;
        }

        var intendedTarget = battleNpc;
        var distanceToHitbox = Geometry.DistanceToHitbox(player.Position, player.HitboxRadius, target.Position, target.HitboxRadius);
        var safeMovementDestination = bossModSafety.TryGetSafeMovementIntent(player.Position, out var safeDestination, out _)
            ? safeDestination
            : (Vector3?)null;

        var reengageRange = MathF.Max(CombatConstants.MeleeActionRange, jobRangeProvider.EngagementRange);
        var originalTargetObjectId = target.GameObjectId;
        var classJobId = player.ClassJob.RowId;
        IBattleNpc? relayReturnTarget = null;
        if (this.ShouldTryHostileRelay(classJobId, distanceToHitbox) &&
            this.TryFindHostileRelayGapCloserTarget(player, intendedTarget, distanceToHitbox, classJobId, out var relayTarget))
        {
            relayReturnTarget = intendedTarget;
            target = relayTarget;
            distanceToHitbox = Geometry.DistanceToHitbox(player.Position, player.HitboxRadius, target.Position, target.HitboxRadius);
        }

        var primaryActionId = this.GetPrimaryReengageActionId(classJobId);
        var styleOpportunity = dashStyleController.EvaluateReengageOpportunity(player, target, distanceToHitbox, primaryActionId, config.MinimumGapCloserDistance);
        if (styleOpportunity.Active &&
            originalTargetObjectId != target.GameObjectId &&
            target is IBattleNpc surfTarget &&
            surfTarget.BattleNpcKind == BattleNpcSubKind.Combatant)
        {
            styleOpportunity = styleOpportunity with { Reason = "pack surf" };
        }

        var friendlyReengageTarget = relayReturnTarget ?? target as IBattleChara;
        var hasFriendlyReengageOption = classJobId is 2 or 20 or 25 or 40 or 41;
        if (relayReturnTarget == null &&
            (distanceToHitbox <= reengageRange ||
            (distanceToHitbox > CombatConstants.GapCloserMaxRange && !hasFriendlyReengageOption)))
        {
            this.lastGapCloserSafety = distanceToHitbox <= reengageRange
                ? $"target within {reengageRange:0.#}y engagement range"
                : "target not in gap closer range";
            this.lastSafeLandingPosition = null;
            return false;
        }

        var minimumDashDistance = styleOpportunity.AllowsShortDash ? 4f : config.MinimumGapCloserDistance;
        if (distanceToHitbox < minimumDashDistance)
        {
            this.lastGapCloserSafety = $"target under {minimumDashDistance:0}y";
            return false;
        }

        if (this.ShouldConserveTrashPullGapCloser(player, target, distanceToHitbox, classJobId, out var trashConserveReason))
        {
            this.lastGapCloserSafety = trashConserveReason;
            this.lastSafeLandingPosition = null;
            mobilityEvaluator.RecordIdle(MobilityIntent.Uptime, "Gap closer", trashConserveReason);
            return false;
        }

        return classJobId switch
        {
            1 or 19 when config.GapCloserPLD => this.TryUseTargetGapCloser(ActionUse.PaladinInterveneActionId, "Intervene", distanceToHitbox, target, safeMovementDestination, styleOpportunity, relayReturnTarget),
            3 or 21 when config.GapCloserWAR => this.TryUseTargetGapCloser(ActionUse.WarriorOnslaughtActionId, "Onslaught", distanceToHitbox, target, safeMovementDestination, styleOpportunity, relayReturnTarget),
            32 when config.GapCloserDRK => this.TryUseTargetGapCloser(ActionUse.DarkKnightShadowstrideActionId, "Shadowstride", distanceToHitbox, target, safeMovementDestination, styleOpportunity, relayReturnTarget),
            37 when config.GapCloserGNB => this.TryUseTargetGapCloser(ActionUse.GunbreakerTrajectoryActionId, "Trajectory", distanceToHitbox, target, safeMovementDestination, styleOpportunity, relayReturnTarget),
            2 or 20 when config.GapCloserMNK => (distanceToHitbox <= CombatConstants.GapCloserMaxRange && this.TryUseTargetGapCloser(ActionUse.MonkThunderclapActionId, "Thunderclap", distanceToHitbox, target, safeMovementDestination, styleOpportunity, relayReturnTarget)) ||
                                               this.TryUseFriendlyReengageGapCloser(ActionUse.MonkThunderclapActionId, "Thunderclap", CombatConstants.GapCloserMaxRange, friendlyReengageTarget, safeMovementDestination, styleOpportunity),
            4 or 22 when config.GapCloserDRG => this.TryUseTargetGapCloser(ActionUse.DragoonWingedGlideActionId, "Winged Glide", distanceToHitbox, target, safeMovementDestination, styleOpportunity, relayReturnTarget),
            29 or 30 when config.GapCloserNIN => this.TryUseNinjaShukuchi(target, safeMovementDestination, styleOpportunity),
            34 when config.GapCloserSAM => this.TryUseTargetGapCloser(ActionUse.SamuraiGyotenActionId, "Gyoten", distanceToHitbox, target, safeMovementDestination, styleOpportunity, relayReturnTarget),
            38 when config.GapCloserDNC => this.TryUseForwardGapCloser(ActionUse.DancerEnAvantActionId, "En Avant", distanceToHitbox, reengageRange, target, safeMovementDestination, styleOpportunity),
            39 when config.GapCloserRPR => this.TryUseReaperRegress(ref this.lastGapCloserSafety, distanceToHitbox, safeMovementDestination, styleOpportunity, target as IBattleChara) || this.TryUseForwardGapCloser(ActionUse.ReaperHellsIngressActionId, "Hell's Ingress", distanceToHitbox, MathF.Max(reengageRange, CombatConstants.MeleeActionRange + 1f), target, safeMovementDestination, styleOpportunity),
            41 when config.GapCloserVPR => (distanceToHitbox <= CombatConstants.GapCloserMaxRange && this.TryUseTargetGapCloser(ActionUse.ViperSlitherActionId, "Slither", distanceToHitbox, target, safeMovementDestination, styleOpportunity, relayReturnTarget)) ||
                                          this.TryUseFriendlyReengageGapCloser(ActionUse.ViperSlitherActionId, "Slither", CombatConstants.GapCloserMaxRange, friendlyReengageTarget, safeMovementDestination, styleOpportunity),
            24 when config.GapCloserWHM => this.TryUseForwardGapCloser(ActionUse.WhiteMageAetherialShiftActionId, "Aetherial Shift", distanceToHitbox, reengageRange, target, safeMovementDestination, styleOpportunity),
            25 when config.GapCloserBLM => this.TryUseFriendlyReengageGapCloser(ActionUse.BlackMageAetherialManipulationActionId, "Aetherial Manipulation", 25f, friendlyReengageTarget, safeMovementDestination, styleOpportunity),
            35 when config.GapCloserRDM => this.TryUseTargetGapCloser(ActionUse.RedMageCorpsACorpsActionId, "Corps-a-corps", distanceToHitbox, target, safeMovementDestination, styleOpportunity, relayReturnTarget),
            40 when config.GapCloserSGE => (distanceToHitbox <= 25f && this.TryUseTargetGapCloser(ActionUse.SageIcarusActionId, "Icarus", distanceToHitbox, target, safeMovementDestination, styleOpportunity, relayReturnTarget)) ||
                                           this.TryUseFriendlyReengageGapCloser(ActionUse.SageIcarusActionId, "Icarus", 25f, friendlyReengageTarget, safeMovementDestination, styleOpportunity),
            42 when config.GapCloserPCT => this.TryUseForwardGapCloser(ActionUse.PictomancerSmudgeActionId, "Smudge", distanceToHitbox, reengageRange, target, safeMovementDestination, styleOpportunity),
            _ => false
        };
    }

    private bool TryResolveRsrAlignedReengageTarget(out IBattleNpc? target, out string reason)
    {
        target = null;
        reason = string.Empty;
        if (!rotationSolverActions.TryGetUpcomingGcd(requirePreview: false, out var action, out _))
        {
            return false;
        }

        if (!ShouldUseRsrActionTarget(
                rsrHenchedActive(),
                actionAvailable: true,
                action.IsFriendly,
                action.PrimaryTargetId,
                CurrentTargetMatchesRsrTarget(services.TargetManager.Target as IBattleChara, action.PrimaryTargetId)))
        {
            return false;
        }

        target = this.FindRsrActionTarget(action);
        if (IsUsableGapCloserTarget(target))
        {
            reason = "RSR Auto action target";
            return true;
        }

        reason = "RSR Auto target mismatch; reflected target unavailable";
        return false;
    }

    internal static bool ShouldUseRsrActionTarget(
        bool rsrHenchedActive,
        bool actionAvailable,
        bool actionIsFriendly,
        ulong actionPrimaryTargetId,
        bool currentTargetMatchesAction)
    {
        return !rsrHenchedActive &&
               actionAvailable &&
               !actionIsFriendly &&
               actionPrimaryTargetId != 0 &&
               !currentTargetMatchesAction;
    }

    private static bool CurrentTargetMatchesRsrTarget(IBattleChara? currentTarget, ulong actionPrimaryTargetId)
    {
        return currentTarget != null &&
               actionPrimaryTargetId != 0 &&
               (currentTarget.GameObjectId == actionPrimaryTargetId ||
                currentTarget.EntityId == actionPrimaryTargetId);
    }

    private IBattleNpc? FindRsrActionTarget(RsrAoeActionSnapshot action)
    {
        if (action.PrimaryTargetId != 0)
        {
            if (services.ObjectTable.SearchById(action.PrimaryTargetId) is IBattleNpc byGameObjectId)
            {
                return byGameObjectId;
            }

            foreach (var npc in services.ObjectTable.OfType<IBattleNpc>())
            {
                if (npc.EntityId == action.PrimaryTargetId)
                {
                    return npc;
                }
            }
        }

        if (action.PrimaryTargetPosition == default)
        {
            return null;
        }

        IBattleNpc? best = null;
        var bestDistanceSq = 4f;
        foreach (var npc in services.ObjectTable.OfType<IBattleNpc>())
        {
            if (!IsUsableGapCloserTarget(npc))
            {
                continue;
            }

            var distanceSq = Geometry.Distance2D(npc.Position, action.PrimaryTargetPosition);
            distanceSq *= distanceSq;
            if (distanceSq < bestDistanceSq)
            {
                best = npc;
                bestDistanceSq = distanceSq;
            }
        }

        return best;
    }

    private static bool IsUsableGapCloserTarget(IBattleNpc? target)
    {
        return target != null &&
               target.BattleNpcKind == BattleNpcSubKind.Combatant &&
               target.GameObjectId != 0 &&
               target.StatusFlags.HasFlag(StatusFlags.Hostile) &&
               !target.IsDead &&
               target.CurrentHp > 0;
    }

    private bool TryCommitGapCloserTarget(IGameObject target, out string reason)
    {
        reason = string.Empty;
        if (target is not IBattleNpc battleNpc ||
            battleNpc.BattleNpcKind != BattleNpcSubKind.Combatant ||
            battleNpc.GameObjectId == 0 ||
            battleNpc.IsDead ||
            battleNpc.CurrentHp <= 0)
        {
            reason = "gap closer target is no longer valid";
            return false;
        }

        if (services.TargetManager.Target?.GameObjectId != battleNpc.GameObjectId)
        {
            services.TargetManager.Target = battleNpc;
        }

        if (services.TargetManager.Target?.GameObjectId == battleNpc.GameObjectId)
        {
            return true;
        }

        reason = "could not select gap closer target";
        return false;
    }

    private unsafe bool TryUsePairedReturn(IBattleChara player, IBattleNpc? currentTarget)
    {
        if (!dashStyleController.TryGetPairedReturn(out var request, out _))
        {
            return false;
        }

        if (!bossModSafety.TryIsPositionSafe(player.Position, out var currentSafe, out var currentReason))
        {
            this.lastGapCloserSafety = currentReason;
            return false;
        }

        if (!currentSafe)
        {
            this.lastGapCloserSafety = "paired return blocked: current position unsafe";
            return false;
        }

        if (!bossModSafety.TryCanAttemptDashNow(out var dashReason))
        {
            this.lastGapCloserSafety = dashReason;
            return false;
        }

        if (!ActionUse.CanUseAction(request.ActionId))
        {
            this.lastGapCloserSafety = $"{request.ActionName} unavailable";
            dashStyleController.ClearPairedReturn("paired return unavailable");
            return false;
        }

        var safeMovementDestination = bossModSafety.TryGetSafeMovementIntent(player.Position, out var safeDestination, out _)
            ? safeDestination
            : (Vector3?)null;

        if (request.Kind == DashReturnKind.Regress)
        {
            var requiredDistance = currentTarget != null
                ? Geometry.DistanceToHitbox(player.Position, player.HitboxRadius, currentTarget.Position, currentTarget.HitboxRadius)
                : 0f;
            var styleOpportunity = new DashStyleReengageOpportunity(true, "paired return", false, false);
            if (this.TryUseReaperRegress(ref this.lastGapCloserSafety, requiredDistance, safeMovementDestination, styleOpportunity))
            {
                dashStyleController.ClearPairedReturn("paired return");
                this.lastGapCloserSafety = "used Regress (paired return)";
                return true;
            }

            if (this.lastGapCloserSafety.Contains("no portal", StringComparison.OrdinalIgnoreCase))
            {
                dashStyleController.ClearPairedReturn("paired return portal missing");
            }

            return false;
        }

        if (currentTarget == null ||
            currentTarget.BattleNpcKind != BattleNpcSubKind.Combatant ||
            currentTarget.GameObjectId == 0 ||
            currentTarget.IsDead ||
            currentTarget.CurrentHp <= 0)
        {
            this.lastGapCloserSafety = "paired return needs current target";
            return false;
        }

        if (config.GuardUnknownBossNavigationWithVnavmesh && this.IsUnknownBossLikeTarget(currentTarget))
        {
            this.lastGapCloserSafety = "unknown boss module; paired return disabled";
            return false;
        }

        var distanceToHitbox = Geometry.DistanceToHitbox(player.Position, player.HitboxRadius, currentTarget.Position, currentTarget.HitboxRadius);
        if (distanceToHitbox <= CombatConstants.MeleeActionRange || distanceToHitbox > CombatConstants.GapCloserMaxRange)
        {
            this.lastGapCloserSafety = "paired return target not in dash range";
            return false;
        }

        if (!Geometry.TryCalculateTargetDashDestination(player.Position, currentTarget.Position, distanceToHitbox, out var destination))
        {
            this.lastGapCloserSafety = "could not calculate paired return landing";
            return false;
        }

        if (!mobilityEvaluator.TryValidateDashDestination(
            player,
            destination,
            currentTarget,
            safeMovementDestination,
            MobilityIntent.Uptime,
            request.ActionName,
            request.ActionId,
            0f,
            requireSafetyProgress: false,
            requireUptimeProgress: true,
            requireVnavReachable: false,
            out var decision))
        {
            this.lastGapCloserSafety = decision.RiskReason;
            return false;
        }

        this.lastSafeLandingPosition = destination;
        var used = ActionManager.Instance()->UseAction(ActionType.Action, request.ActionId, currentTarget.GameObjectId);
        mobilityEvaluator.RecordActionResult(decision, used, used ? "action used" : "action failed");
        if (used)
        {
            dashStyleController.ClearPairedReturn("paired return");
            this.lastGapCloserSafety = $"used {request.ActionName} ({decision.IntentLabel}, paired return)";
            return true;
        }

        dashStyleController.ClearPairedReturn("paired return failed");
        this.lastGapCloserSafety = $"failed to use {request.ActionName} ({decision.IntentLabel}, paired return)";
        return false;
    }

    public unsafe bool TryUseReaperRegress(ref string lastSafety, float distanceToHitboxRequired = 0f, Vector3? safeMovementDestination = null, DashStyleReengageOpportunity? styleOpportunity = null, IBattleChara? targetOverride = null)
    {
        var player = services.ObjectTable.LocalPlayer;
        if (player == null)
        {
            return false;
        }

        if (!ActionUse.CanUseAction(ActionUse.ReaperRegressActionId))
        {
            lastSafety = "Regress unavailable";
            return false;
        }

        var portal = this.TryFindReaperPortal(player);
        if (portal == null)
        {
            lastSafety = "Regress: no portal found";
            return false;
        }

        var portalPosition = portal.Position;

        if (distanceToHitboxRequired > 0f)
        {
            var target = targetOverride ?? services.TargetManager.Target;
            if (target == null)
            {
                lastSafety = "Regress: no target";
                return false;
            }

            var regressDistanceToHitbox = Geometry.DistanceToHitbox(portalPosition, player.HitboxRadius, target.Position, target.HitboxRadius);
            if (regressDistanceToHitbox >= distanceToHitboxRequired || regressDistanceToHitbox > CombatConstants.MeleeActionRange + 1f)
            {
                lastSafety = "Regress: portal would not re-engage";
                mobilityEvaluator.RecordIdle(MobilityIntent.Uptime, "Regress", "portal would not re-engage");
                return false;
            }
        }

        if (safeMovementDestination.HasValue &&
            !mobilityEvaluator.TryValidateDashDestination(
                player,
                portalPosition,
                targetOverride ?? services.TargetManager.Target as IBattleChara,
                safeMovementDestination.Value,
                distanceToHitboxRequired > 0f ? MobilityIntent.Uptime : MobilityIntent.Safety,
                "Regress",
                ActionUse.ReaperRegressActionId,
                distanceToHitboxRequired > 0f ? 0f : config.MinimumGapCloserDistance,
                requireSafetyProgress: distanceToHitboxRequired <= 0f,
                requireUptimeProgress: distanceToHitboxRequired > 0f,
                requireVnavReachable: config.GuardUnknownBossNavigationWithVnavmesh,
                out var regressSafetyDecision))
        {
            lastSafety = $"Regress: {regressSafetyDecision.RiskReason}";
            return false;
        }

        if (!safeMovementDestination.HasValue &&
            !mobilityEvaluator.TryValidateDashDestination(
                player,
                portalPosition,
                targetOverride ?? services.TargetManager.Target as IBattleChara,
                null,
                MobilityIntent.Uptime,
                "Regress",
                ActionUse.ReaperRegressActionId,
                0f,
                requireSafetyProgress: false,
                requireUptimeProgress: distanceToHitboxRequired > 0f,
                requireVnavReachable: false,
                out var regressUptimeDecision))
        {
            lastSafety = $"Regress: {regressUptimeDecision.RiskReason}";
            return false;
        }

        var location = portalPosition;
        var decision = mobilityEvaluator.LastDecision;
        if (!this.AcceptStyleOpportunity(styleOpportunity, decision, ref lastSafety))
        {
            return false;
        }

        var used = ActionManager.Instance()->UseActionLocation(ActionType.Action, ActionUse.ReaperRegressActionId, player.GameObjectId, &location);
        mobilityEvaluator.RecordActionResult(decision, used, used ? "action used" : "action failed");
        if (used && styleOpportunity is { Active: true } activeStyle)
        {
            dashStyleController.RecordStyleUse(activeStyle.Reason);
        }

        lastSafety = used && styleOpportunity is { Active: true } usedStyle
            ? $"used Regress ({decision.IntentLabel}, {usedStyle.Reason})"
            : used ? $"used Regress ({decision.IntentLabel})" : $"failed to use Regress ({decision.IntentLabel})";
        return used;
    }

    private unsafe bool TryUseFriendlyReengageGapCloser(uint actionId, string actionName, float maxRange, IBattleChara? currentTarget, Vector3? safeMovementDestination, DashStyleReengageOpportunity styleOpportunity)
    {
        var player = services.ObjectTable.LocalPlayer;
        if (player == null)
        {
            return false;
        }

        if (currentTarget == null)
        {
            this.lastGapCloserSafety = "friendly reengage needs target";
            return false;
        }

        if (!ActionUse.CanUseAction(actionId))
        {
            this.lastGapCloserSafety = "action unavailable";
            return false;
        }

        if (dashStyleController.ReengageStyleActive)
        {
            var styleCandidates = new List<DashStyleCandidate<IBattleChara>>();
            foreach (var ally in this.EnumerateFriendlyReengageTargets(player, currentTarget, maxRange))
            {
                if (!mobilityEvaluator.TryValidateDashDestination(
                    player,
                    ally.Position,
                    currentTarget,
                    safeMovementDestination,
                    MobilityIntent.Uptime,
                    actionName,
                    actionId,
                    0f,
                    requireSafetyProgress: false,
                    requireUptimeProgress: true,
                    requireVnavReachable: false,
                    out var decision))
                {
                    this.lastGapCloserSafety = decision.RiskReason;
                    continue;
                }

                if (!ShouldUseFriendlyAnchorDash(decision.MoveDistance, decision.SafetyGain, decision.UptimeGain, decision.PathGain, out var anchorReason))
                {
                    this.lastGapCloserSafety = anchorReason;
                    mobilityEvaluator.RecordIdle(MobilityIntent.Uptime, actionName, anchorReason);
                    continue;
                }

                var reason = styleOpportunity.Reason is "knockback recovery" or "capped charge" or "pack surf"
                    ? styleOpportunity.Reason
                    : "ally anchor";
                styleCandidates.Add(dashStyleController.ScoreCandidate(
                    ally,
                    player,
                    ally.Position,
                    currentTarget,
                    safeMovementDestination,
                    decision,
                    reason));
            }

            if (dashStyleController.TrySelectBest(styleCandidates, out var selected))
            {
                if (!this.AcceptStyleOpportunity(styleOpportunity, selected.Decision, ref this.lastGapCloserSafety))
                {
                    this.lastSafeLandingPosition = null;
                    return false;
                }

                if (!this.TryCommitGapCloserTarget(currentTarget, out var targetCommitReason))
                {
                    this.lastGapCloserSafety = targetCommitReason;
                    this.lastSafeLandingPosition = null;
                    mobilityEvaluator.RecordIdle(MobilityIntent.Uptime, actionName, targetCommitReason);
                    return false;
                }

                this.lastSafeLandingPosition = selected.Destination;
                var used = ActionManager.Instance()->UseAction(ActionType.Action, actionId, selected.Source.GameObjectId);
                mobilityEvaluator.RecordActionResult(selected.Decision, used, used ? "action used" : "action failed");
                if (used)
                {
                    dashStyleController.RecordStyleUse(selected.Reason);
                }

                this.lastGapCloserSafety = used
                    ? $"used {actionName} on ally ({selected.Decision.IntentLabel}, {selected.Reason})"
                    : $"failed to use {actionName} on ally ({selected.Decision.IntentLabel}, {selected.Reason})";
                return used;
            }

            if (string.IsNullOrEmpty(this.lastGapCloserSafety) || this.lastGapCloserSafety == "current position safe")
            {
                this.lastGapCloserSafety = "no safe ally anchor";
            }

            return false;
        }

        foreach (var ally in this.EnumerateFriendlyReengageTargets(player, currentTarget, maxRange))
        {
            if (!mobilityEvaluator.TryValidateDashDestination(
                player,
                ally.Position,
                currentTarget,
                safeMovementDestination,
                MobilityIntent.Uptime,
                actionName,
                actionId,
                0f,
                requireSafetyProgress: false,
                requireUptimeProgress: true,
                requireVnavReachable: false,
                out var decision))
            {
                this.lastGapCloserSafety = decision.RiskReason;
                continue;
            }

            if (!ShouldUseFriendlyAnchorDash(decision.MoveDistance, decision.SafetyGain, decision.UptimeGain, decision.PathGain, out var anchorReason))
            {
                this.lastGapCloserSafety = anchorReason;
                mobilityEvaluator.RecordIdle(MobilityIntent.Uptime, actionName, anchorReason);
                continue;
            }

            if (!this.TryCommitGapCloserTarget(currentTarget, out var targetCommitReason))
            {
                this.lastGapCloserSafety = targetCommitReason;
                this.lastSafeLandingPosition = null;
                mobilityEvaluator.RecordIdle(MobilityIntent.Uptime, actionName, targetCommitReason);
                return false;
            }

            this.lastSafeLandingPosition = ally.Position;
            var used = ActionManager.Instance()->UseAction(ActionType.Action, actionId, ally.GameObjectId);
            mobilityEvaluator.RecordActionResult(decision, used, used ? "action used" : "action failed");
            this.lastGapCloserSafety = used ? $"used {actionName} on ally ({decision.IntentLabel})" : $"failed to use {actionName} on ally ({decision.IntentLabel})";
            return used;
        }

        if (string.IsNullOrEmpty(this.lastGapCloserSafety) || this.lastGapCloserSafety == "current position safe")
        {
            this.lastGapCloserSafety = "no safe ally anchor";
        }

        return false;
    }

    internal static bool ShouldUseFriendlyAnchorDash(
        float moveDistance,
        float safetyGain,
        float uptimeGain,
        float pathGain,
        out string reason)
    {
        reason = string.Empty;
        if (moveDistance < FriendlyAnchorMinimumMoveDistance)
        {
            reason = $"ally anchor too close: {moveDistance:0.0}y";
            return false;
        }

        if (safetyGain > 0.1f ||
            pathGain > 0.1f)
        {
            return true;
        }

        if (uptimeGain < FriendlyAnchorMinimumUptimeGain)
        {
            reason = $"ally anchor low uptime gain: {uptimeGain:0.0}y";
            return false;
        }

        return true;
    }

    private unsafe bool TryUseTargetGapCloser(uint actionId, string actionName, float distanceToHitbox, IGameObject target, Vector3? safeMovementDestination, DashStyleReengageOpportunity styleOpportunity, IBattleNpc? restoreTargetAfterUse = null)
    {
        var player = services.ObjectTable.LocalPlayer;
        if (player == null)
        {
            return false;
        }

        if (!ActionUse.CanUseAction(actionId))
        {
            this.lastGapCloserSafety = "action unavailable";
            return false;
        }

        if (!Geometry.TryCalculateTargetDashDestination(player.Position, target.Position, distanceToHitbox, out var destination))
        {
            this.lastGapCloserSafety = "could not calculate dash destination";
            this.lastSafeLandingPosition = null;
            return false;
        }

        if (!mobilityEvaluator.TryValidateDashDestination(
            player,
            destination,
            target as IBattleChara,
            safeMovementDestination,
            MobilityIntent.Uptime,
            actionName,
            actionId,
            0f,
            requireSafetyProgress: false,
            requireUptimeProgress: true,
            requireVnavReachable: false,
            out var decision))
        {
            this.lastGapCloserSafety = decision.RiskReason;
            this.lastSafeLandingPosition = null;
            return false;
        }

        if (!this.AcceptStyleOpportunity(styleOpportunity, decision, ref this.lastGapCloserSafety))
        {
            this.lastSafeLandingPosition = null;
            return false;
        }

        if (!this.TryCommitGapCloserTarget(target, out var targetCommitReason))
        {
            this.lastGapCloserSafety = targetCommitReason;
            this.lastSafeLandingPosition = null;
            mobilityEvaluator.RecordIdle(MobilityIntent.Uptime, actionName, targetCommitReason);
            return false;
        }

        this.lastSafeLandingPosition = destination;
        var used = ActionManager.Instance()->UseAction(ActionType.Action, actionId, target.GameObjectId);
        mobilityEvaluator.RecordActionResult(decision, used, used ? "action used" : "action failed");
        if (restoreTargetAfterUse != null)
        {
            this.TryRestoreTargetAfterRelay(restoreTargetAfterUse);
        }

        if (used && styleOpportunity.Active)
        {
            dashStyleController.RecordStyleUse(styleOpportunity.Reason);
        }

        this.lastGapCloserSafety = used && styleOpportunity.Active
            ? $"used {actionName} ({decision.IntentLabel}, {styleOpportunity.Reason})"
            : used ? $"used {actionName} ({decision.IntentLabel})" : $"failed to use {actionName} ({decision.IntentLabel})";
        return used;
    }

    private void TryRestoreTargetAfterRelay(IBattleNpc target)
    {
        if (!IsUsableGapCloserTarget(target))
        {
            return;
        }

        services.TargetManager.Target = target;
    }

    private unsafe bool TryUseForwardGapCloser(uint actionId, string actionName, float distanceToHitbox, float requiredLandingRange, IGameObject target, Vector3? safeMovementDestination, DashStyleReengageOpportunity styleOpportunity)
    {
        var player = services.ObjectTable.LocalPlayer;
        if (player == null)
        {
            return false;
        }

        if (!ActionUse.CanUseAction(actionId))
        {
            this.lastGapCloserSafety = "action unavailable";
            return false;
        }

        var forward = Geometry.RotationToDirection(player.Rotation);
        var destination = player.Position + forward * CombatConstants.FixedForwardGapCloserRange;
        var destinationDistanceToHitbox = Geometry.DistanceToHitbox(destination, player.HitboxRadius, target.Position, target.HitboxRadius);
        if (destinationDistanceToHitbox >= distanceToHitbox || destinationDistanceToHitbox > requiredLandingRange)
        {
            if (this.TryRequestForwardDashFacing(player, target as IBattleChara, actionId, actionName, CombatConstants.FixedForwardGapCloserRange, requiredLandingRange, distanceToHitbox, safeMovementDestination, null, null))
            {
                return false;
            }

            this.lastGapCloserSafety = "fixed dash would not re-engage";
            return false;
        }

        if (!mobilityEvaluator.TryValidateDashDestination(
            player,
            destination,
            target as IBattleChara,
            safeMovementDestination,
            MobilityIntent.Uptime,
            actionName,
            actionId,
            0f,
            requireSafetyProgress: false,
            requireUptimeProgress: true,
            requireVnavReachable: false,
            out var decision))
        {
            if (this.TryRequestForwardDashFacing(player, target as IBattleChara, actionId, actionName, CombatConstants.FixedForwardGapCloserRange, requiredLandingRange, distanceToHitbox, safeMovementDestination, null, null))
            {
                return false;
            }

            this.lastGapCloserSafety = decision.RiskReason;
            this.lastSafeLandingPosition = null;
            return false;
        }

        if (this.TryRequestForwardDashFacing(player, target as IBattleChara, actionId, actionName, CombatConstants.FixedForwardGapCloserRange, requiredLandingRange, distanceToHitbox, safeMovementDestination, destination, decision))
        {
            return false;
        }

        if (!this.AcceptStyleOpportunity(styleOpportunity, decision, ref this.lastGapCloserSafety))
        {
            this.lastSafeLandingPosition = null;
            return false;
        }

        this.lastSafeLandingPosition = destination;
        var used = ActionManager.Instance()->UseAction(ActionType.Action, actionId, player.GameObjectId);
        mobilityEvaluator.RecordActionResult(decision, used, used ? "action used" : "action failed");
        if (used && styleOpportunity.Active)
        {
            dashStyleController.RecordStyleUse(styleOpportunity.Reason);
        }

        this.lastGapCloserSafety = used && styleOpportunity.Active
            ? $"used {actionName} ({decision.IntentLabel}, {styleOpportunity.Reason})"
            : used ? $"used {actionName} ({decision.IntentLabel})" : $"failed to use {actionName} ({decision.IntentLabel})";
        return used;
    }

    private unsafe bool TryUseNinjaShukuchi(IGameObject target, Vector3? safeMovementDestination, DashStyleReengageOpportunity styleOpportunity)
    {
        var player = services.ObjectTable.LocalPlayer;
        if (player == null)
        {
            return false;
        }

        if (!ActionUse.CanUseAction(ActionUse.NinjaShukuchiActionId))
        {
            this.lastGapCloserSafety = "action unavailable";
            return false;
        }

        if (!this.TryFindSafeShukuchiDestination(player, target as IBattleChara, target.Position, target.HitboxRadius, safeMovementDestination, styleOpportunity, out var destination, out var decision, out var styleReason))
        {
            return false;
        }

        if (!this.AcceptStyleOpportunity(styleOpportunity, decision, ref this.lastGapCloserSafety))
        {
            return false;
        }

        if (!this.TryCommitGapCloserTarget(target, out var targetCommitReason))
        {
            this.lastGapCloserSafety = targetCommitReason;
            mobilityEvaluator.RecordIdle(MobilityIntent.Uptime, "Shukuchi", targetCommitReason);
            return false;
        }

        var location = destination;
        var used = ActionManager.Instance()->UseActionLocation(ActionType.Action, ActionUse.NinjaShukuchiActionId, player.GameObjectId, &location);
        mobilityEvaluator.RecordActionResult(decision, used, used ? "action used" : "action failed");
        if (used && styleReason.Length > 0)
        {
            dashStyleController.RecordStyleUse(styleReason);
        }

        this.lastGapCloserSafety = used && styleReason.Length > 0
            ? $"used Shukuchi ({decision.IntentLabel}, {styleReason})"
            : used ? $"used Shukuchi ({decision.IntentLabel})" : $"failed to use Shukuchi ({decision.IntentLabel})";
        return used;
    }

    private bool TryRequestForwardDashFacing(
        IBattleChara player,
        IBattleChara? target,
        uint actionId,
        string actionName,
        float dashDistance,
        float requiredLandingRange,
        float currentDistanceToHitbox,
        Vector3? safeMovementDestination,
        Vector3? currentDestination,
        MobilityDecisionDiagnostics? currentDecision)
    {
        if (target == null)
        {
            return false;
        }

        if (!dashStyleController.ReengageStyleActive)
        {
            return currentDecision == null &&
                   this.TryRequestDirectForwardDashFacing(
                       player,
                       target,
                       actionId,
                       actionName,
                       dashDistance,
                       requiredLandingRange,
                       currentDistanceToHitbox,
                       safeMovementDestination);
        }

        var currentMaxMeleeError = currentDestination.HasValue
            ? this.CalculateMaxMeleeLandingError(player, currentDestination.Value, target)
            : float.PositiveInfinity;
        var candidates = new List<DashStyleCandidate<float>>();
        foreach (var candidate in this.EnumerateForwardDashFacingCandidates(player, target, dashDistance))
        {
            var destinationDistanceToHitbox = Geometry.DistanceToHitbox(candidate.Destination, player.HitboxRadius, target.Position, target.HitboxRadius);
            if (destinationDistanceToHitbox >= currentDistanceToHitbox || destinationDistanceToHitbox > requiredLandingRange)
            {
                continue;
            }

            if (!mobilityEvaluator.TryValidateDashDestination(
                player,
                candidate.Destination,
                target,
                safeMovementDestination,
                MobilityIntent.Uptime,
                actionName,
                actionId,
                0f,
                requireSafetyProgress: false,
                requireUptimeProgress: true,
                requireVnavReachable: false,
                out var decision))
            {
                this.lastGapCloserSafety = decision.RiskReason;
                continue;
            }

            if (currentDecision != null)
            {
                var candidateError = this.CalculateMaxMeleeLandingError(player, candidate.Destination, target);
                if (candidateError + DirectionalDashBetterLandingThreshold >= currentMaxMeleeError)
                {
                    continue;
                }
            }

            candidates.Add(dashStyleController.ScoreCandidate(
                candidate.Rotation,
                player,
                candidate.Destination,
                target,
                safeMovementDestination,
                decision,
                "directional dash"));
        }

        if (!dashStyleController.TrySelectBest(candidates, out var selected))
        {
            return false;
        }

        if (Geometry.AbsAngleDelta(player.Rotation, selected.Source) <= FacingController.DirectionalDashToleranceRadians)
        {
            return false;
        }

        facingController.RequestFacing(FacingController.CreateDirectionalDashRequest(selected.Source, selected.Destination, $"turn for {actionName}", FacingBossModPolicy.AssistValidatedDash));
        this.lastSafeLandingPosition = selected.Destination;
        this.lastGapCloserSafety = $"turning for {actionName} ({selected.Decision.IntentLabel}, {selected.Reason})";
        return true;
    }

    private bool TryRequestDirectForwardDashFacing(
        IBattleChara player,
        IBattleChara target,
        uint actionId,
        string actionName,
        float dashDistance,
        float requiredLandingRange,
        float currentDistanceToHitbox,
        Vector3? safeMovementDestination)
    {
        var toTarget = target.Position - player.Position;
        toTarget.Y = 0f;
        if (toTarget.LengthSquared() <= 0.0001f)
        {
            return false;
        }

        var direction = Vector3.Normalize(toTarget);
        var destination = player.Position + direction * dashDistance;
        var destinationDistanceToHitbox = Geometry.DistanceToHitbox(destination, player.HitboxRadius, target.Position, target.HitboxRadius);
        if (destinationDistanceToHitbox >= currentDistanceToHitbox || destinationDistanceToHitbox > requiredLandingRange)
        {
            return false;
        }

        if (!mobilityEvaluator.TryValidateDashDestination(
            player,
            destination,
            target,
            safeMovementDestination,
            MobilityIntent.Uptime,
            actionName,
            actionId,
            0f,
            requireSafetyProgress: false,
            requireUptimeProgress: true,
            requireVnavReachable: false,
            out var decision))
        {
            this.lastGapCloserSafety = decision.RiskReason;
            return false;
        }

        var desiredRotation = Geometry.DirectionToRotation(direction);
        if (Geometry.AbsAngleDelta(player.Rotation, desiredRotation) <= FacingController.DirectionalDashToleranceRadians)
        {
            return false;
        }

        facingController.RequestFacing(FacingController.CreateDirectionalDashRequest(desiredRotation, destination, $"turn for {actionName}", FacingBossModPolicy.AssistValidatedDash));
        this.lastSafeLandingPosition = destination;
        this.lastGapCloserSafety = $"turning for {actionName} ({decision.IntentLabel}, direct dash)";
        return true;
    }

    private bool TryFindSafeShukuchiDestination(
        IBattleChara player,
        IBattleChara? target,
        Vector3 targetPosition,
        float targetHitboxRadius,
        Vector3? safeMovementDestination,
        DashStyleReengageOpportunity styleOpportunity,
        out Vector3 destination,
        out MobilityDecisionDiagnostics selectedDecision,
        out string styleReason)
    {
        if (dashStyleController.ReengageStyleActive)
        {
            var candidates = new List<DashStyleCandidate<Vector3>>();
            foreach (var candidate in this.EnumerateShukuchiCandidates(player.Position, targetPosition, targetHitboxRadius))
            {
                if (Vector3.Distance(player.Position, candidate) > CombatConstants.GapCloserMaxRange)
                {
                    continue;
                }

                if (mobilityEvaluator.TryValidateDashDestination(
                    player,
                    candidate,
                    target,
                    safeMovementDestination,
                    MobilityIntent.Uptime,
                    "Shukuchi",
                    ActionUse.NinjaShukuchiActionId,
                    0f,
                    requireSafetyProgress: false,
                    requireUptimeProgress: true,
                    requireVnavReachable: false,
                    out var decision))
                {
                    var reason = styleOpportunity.Reason is "knockback recovery" or "capped charge" or "pack surf"
                        ? styleOpportunity.Reason
                        : "precision Shukuchi";
                    candidates.Add(dashStyleController.ScoreCandidate(
                        candidate,
                        player,
                        candidate,
                        target,
                        safeMovementDestination,
                        decision,
                        reason));
                    continue;
                }

                this.lastGapCloserSafety = decision.RiskReason;
            }

            if (dashStyleController.TrySelectBest(candidates, out var selected))
            {
                destination = selected.Destination;
                selectedDecision = selected.Decision;
                styleReason = selected.Reason;
                this.lastSafeLandingPosition = destination;
                this.lastGapCloserSafety = $"Shukuchi ready ({selected.Decision.IntentLabel}, {selected.Reason})";
                return true;
            }

            destination = default;
            selectedDecision = MobilityDecisionDiagnostics.Empty;
            styleReason = string.Empty;
            if (string.IsNullOrEmpty(this.lastGapCloserSafety) || this.lastGapCloserSafety == "safe Shukuchi destination found")
            {
                this.lastGapCloserSafety = "no safe Shukuchi destination";
            }

            return false;
        }

        foreach (var candidate in this.EnumerateShukuchiCandidates(player.Position, targetPosition, targetHitboxRadius))
        {
            if (Vector3.Distance(player.Position, candidate) > CombatConstants.GapCloserMaxRange)
            {
                continue;
            }

            if (mobilityEvaluator.TryValidateDashDestination(
                player,
                candidate,
                target,
                safeMovementDestination,
                MobilityIntent.Uptime,
                "Shukuchi",
                ActionUse.NinjaShukuchiActionId,
                0f,
                requireSafetyProgress: false,
                requireUptimeProgress: true,
                requireVnavReachable: false,
                out var decision))
            {
                destination = candidate;
                selectedDecision = decision;
                styleReason = string.Empty;
                this.lastGapCloserSafety = $"Shukuchi ready ({decision.IntentLabel})";
                return true;
            }

            this.lastGapCloserSafety = decision.RiskReason;
        }

        destination = default;
        selectedDecision = MobilityDecisionDiagnostics.Empty;
        styleReason = string.Empty;
        if (string.IsNullOrEmpty(this.lastGapCloserSafety) || this.lastGapCloserSafety == "safe Shukuchi destination found")
        {
            this.lastGapCloserSafety = "no safe Shukuchi destination";
        }

        return false;
    }

    private bool AcceptStyleOpportunity(DashStyleReengageOpportunity? styleOpportunity, MobilityDecisionDiagnostics decision, ref string lastSafety)
    {
        if (styleOpportunity is not { Active: true } activeStyle || !activeStyle.RequiresStrongGain)
        {
            return true;
        }

        if (dashStyleController.MeetsStrongStyleGain(decision))
        {
            return true;
        }

        lastSafety = $"{activeStyle.Reason}: style gain under 3y";
        return false;
    }

    private bool ShouldConserveTrashPullGapCloser(IBattleChara player, IGameObject target, float distanceToHitbox, uint classJobId, out string reason)
    {
        reason = string.Empty;

        var trash = trashPullDiagnostics();
        if (!IsConservativeTrashPullContext(trash))
        {
            return false;
        }

        if (target is IBattleNpc battleNpc && bossMod.HasModuleByDataId(battleNpc.BaseId))
        {
            return false;
        }

        var maxRange = ResolveConservativeTrashGapCloserRange(classJobId);
        var useThreshold = MathF.Max(0f, maxRange - ConservativeTrashGapCloserRangeReserve);
        var anchor = ResolveTrashPullGapCloserAnchor(trash, target);
        var anchorDistance = Geometry.Distance2D(player.Position, anchor);
        var triggerDistance = MathF.Max(distanceToHitbox, anchorDistance);
        if (triggerDistance >= useThreshold)
        {
            return false;
        }

        reason = $"trash pull conserving gap closer: {triggerDistance:0.#}y / {useThreshold:0.#}y";
        return true;
    }

    private bool ShouldTryHostileRelay(uint classJobId, float intendedDistanceToHitbox)
    {
        return CanUseHostileRelayGapCloser(classJobId) &&
               intendedDistanceToHitbox > ResolveHostileRelayGapCloserRange(classJobId);
    }

    private bool TryFindHostileRelayGapCloserTarget(
        IBattleChara player,
        IBattleNpc intendedTarget,
        float intendedDistanceToHitbox,
        uint classJobId,
        out IBattleNpc relayTarget)
    {
        relayTarget = null!;
        var maxRange = ResolveHostileRelayGapCloserRange(classJobId);
        var bestScore = float.NegativeInfinity;

        foreach (var candidate in services.ObjectTable.OfType<IBattleNpc>())
        {
            if (candidate.GameObjectId == intendedTarget.GameObjectId ||
                !IsUsableGapCloserTarget(candidate) ||
                !candidate.StatusFlags.HasFlag(StatusFlags.InCombat))
            {
                continue;
            }

            if (config.GuardUnknownBossNavigationWithVnavmesh && this.IsUnknownBossLikeTarget(candidate))
            {
                continue;
            }

            var candidateDistance = Geometry.DistanceToHitbox(player.Position, player.HitboxRadius, candidate.Position, candidate.HitboxRadius);
            if (candidateDistance <= CombatConstants.MeleeActionRange || candidateDistance > maxRange)
            {
                continue;
            }

            if (!Geometry.TryCalculateTargetDashDestination(player.Position, candidate.Position, candidateDistance, out var landing))
            {
                continue;
            }

            if (!TryEvaluateHostileRelayDash(
                    player.Position,
                    player.HitboxRadius,
                    landing,
                    intendedTarget.Position,
                    intendedTarget.HitboxRadius,
                    out var gain,
                    out var dot,
                    out _))
            {
                continue;
            }

            var score = gain + (dot * 2f) - (candidateDistance * 0.025f);
            if (score > bestScore)
            {
                relayTarget = candidate;
                bestScore = score;
            }
        }

        return bestScore > float.NegativeInfinity &&
               Geometry.DistanceToHitbox(player.Position, player.HitboxRadius, relayTarget.Position, relayTarget.HitboxRadius) < intendedDistanceToHitbox;
    }

    internal static bool ShouldUseHostileRelayDash(
        Vector3 playerPosition,
        float playerRadius,
        Vector3 landingPosition,
        Vector3 intendedTargetPosition,
        float intendedTargetRadius,
        out string reason)
    {
        return TryEvaluateHostileRelayDash(
            playerPosition,
            playerRadius,
            landingPosition,
            intendedTargetPosition,
            intendedTargetRadius,
            out _,
            out _,
            out reason);
    }

    private static bool TryEvaluateHostileRelayDash(
        Vector3 playerPosition,
        float playerRadius,
        Vector3 landingPosition,
        Vector3 intendedTargetPosition,
        float intendedTargetRadius,
        out float gain,
        out float directionDot,
        out string reason)
    {
        var currentDistance = Geometry.DistanceToHitbox(playerPosition, playerRadius, intendedTargetPosition, intendedTargetRadius);
        var landingDistance = Geometry.DistanceToHitbox(landingPosition, playerRadius, intendedTargetPosition, intendedTargetRadius);
        gain = currentDistance - landingDistance;
        if (gain < HostileRelayMinimumTargetGain)
        {
            directionDot = 0f;
            reason = $"relay low target gain: {gain:0.0}y";
            return false;
        }

        if (!TryDirectionDot(playerPosition, landingPosition, intendedTargetPosition, out directionDot))
        {
            reason = "relay direction unknown";
            return false;
        }

        if (directionDot < HostileRelayMinimumDirectionDot)
        {
            reason = $"relay wrong direction: {directionDot:0.00}";
            return false;
        }

        reason = $"relay gains {gain:0.0}y; direction {directionDot:0.00}";
        return true;
    }

    private static bool TryDirectionDot(Vector3 from, Vector3 to, Vector3 toward, out float dot)
    {
        var movement = to - from;
        var target = toward - from;
        movement.Y = 0f;
        target.Y = 0f;
        if (movement.LengthSquared() <= 0.0001f || target.LengthSquared() <= 0.0001f)
        {
            dot = 0f;
            return false;
        }

        dot = Vector3.Dot(Vector3.Normalize(movement), Vector3.Normalize(target));
        return true;
    }

    private static bool CanUseHostileRelayGapCloser(uint classJobId)
    {
        return classJobId is
            1 or 19 or
            3 or 21 or
            32 or
            37 or
            2 or 20 or
            4 or 22 or
            34 or
            41 or
            35 or
            40;
    }

    private static float ResolveHostileRelayGapCloserRange(uint classJobId)
    {
        return classJobId == 40
            ? 25f
            : CombatConstants.GapCloserMaxRange;
    }

    private static bool IsConservativeTrashPullContext(TrashPullDiagnostics trash)
    {
        return (trash.Phase is TrashPullPhase.Gathering or TrashPullPhase.Stabilizing or TrashPullPhase.Burning) &&
               trash.Confidence >= ConservativeTrashMinimumConfidence &&
               trash.DominantTargetCount >= ConservativeTrashMinimumTargets;
    }

    private static Vector3 ResolveTrashPullGapCloserAnchor(TrashPullDiagnostics trash, IGameObject target)
    {
        if (trash.LeadCandidateActive && trash.LeadDestination.HasValue)
        {
            return trash.LeadDestination.Value;
        }

        if (trash.Phase == TrashPullPhase.Gathering && trash.ProjectedTankPosition.HasValue)
        {
            return trash.ProjectedTankPosition.Value;
        }

        return trash.PackCentroid ?? target.Position;
    }

    private static float ResolveConservativeTrashGapCloserRange(uint classJobId)
    {
        return classJobId switch
        {
            25 or 40 => 25f,
            24 or 38 or 39 or 42 => CombatConstants.FixedForwardGapCloserRange,
            _ => CombatConstants.GapCloserMaxRange
        };
    }

    private static bool IsNinjaMudraWindow(IBattleChara player)
    {
        return player.ClassJob.RowId is 29 or 30 &&
               HasAnyStatus(
                   player,
                   ActionUse.NinjaMudraStatusId,
                   ActionUse.NinjaTenChiJinStatusId,
                   ActionUse.NinjaThreeMudraStatusId);
    }

    private static bool HasAnyStatus(IBattleChara player, params uint[] statusIds)
    {
        return player.StatusList.Any(status => status.RemainingTime > 0f && statusIds.Contains(status.StatusId));
    }

    private IEnumerable<DirectionalDashFacingCandidate> EnumerateForwardDashFacingCandidates(IBattleChara player, IBattleChara target, float dashDistance)
    {
        var targetSurface = this.CalculateTargetMaxMeleeSurface();
        var ringRadius = target.HitboxRadius + player.HitboxRadius + targetSurface;
        var toTarget = target.Position - player.Position;
        toTarget.Y = 0f;
        if (toTarget.LengthSquared() > 0.0001f)
        {
            var direction = Vector3.Normalize(toTarget);
            var idealLanding = target.Position - direction * ringRadius;
            if (TryCreateDirectionalDashFacingCandidate(player.Position, idealLanding, dashDistance, out var candidate))
            {
                yield return candidate;
            }
        }

        for (var i = 0; i < 24; i++)
        {
            var angle = i * (MathF.Tau / 24f);
            var idealLanding = new Vector3(
                target.Position.X + MathF.Sin(angle) * ringRadius,
                player.Position.Y,
                target.Position.Z + MathF.Cos(angle) * ringRadius);
            if (TryCreateDirectionalDashFacingCandidate(player.Position, idealLanding, dashDistance, out var candidate))
            {
                yield return candidate;
            }
        }
    }

    private float CalculateMaxMeleeLandingError(IBattleChara player, Vector3 destination, IBattleChara target)
    {
        var surfaceDistance = Geometry.DistanceToHitbox(destination, player.HitboxRadius, target.Position, target.HitboxRadius);
        return MathF.Abs(surfaceDistance - this.CalculateTargetMaxMeleeSurface());
    }

    private float CalculateTargetMaxMeleeSurface()
    {
        return MathF.Min(CombatConstants.GapCloserDestinationMeleeRange, MathF.Max(CombatConstants.MeleeActionRange, jobRangeProvider.EngagementRange));
    }

    private static bool TryCreateDirectionalDashFacingCandidate(Vector3 playerPosition, Vector3 idealLanding, float dashDistance, out DirectionalDashFacingCandidate candidate)
    {
        var direction = idealLanding - playerPosition;
        direction.Y = 0f;
        if (direction.LengthSquared() <= 0.0001f)
        {
            candidate = default;
            return false;
        }

        direction = Vector3.Normalize(direction);
        var destination = playerPosition + direction * dashDistance;
        candidate = new(Geometry.DirectionToRotation(direction), destination);
        return true;
    }

    private uint? GetPrimaryReengageActionId(uint classJobId)
    {
        return classJobId switch
        {
            1 or 19 when config.GapCloserPLD => ActionUse.PaladinInterveneActionId,
            3 or 21 when config.GapCloserWAR => ActionUse.WarriorOnslaughtActionId,
            32 when config.GapCloserDRK => ActionUse.DarkKnightShadowstrideActionId,
            37 when config.GapCloserGNB => ActionUse.GunbreakerTrajectoryActionId,
            2 or 20 when config.GapCloserMNK => ActionUse.MonkThunderclapActionId,
            4 or 22 when config.GapCloserDRG => ActionUse.DragoonWingedGlideActionId,
            29 or 30 when config.GapCloserNIN => ActionUse.NinjaShukuchiActionId,
            34 when config.GapCloserSAM => ActionUse.SamuraiGyotenActionId,
            38 when config.GapCloserDNC => ActionUse.DancerEnAvantActionId,
            39 when config.GapCloserRPR => ActionUse.ReaperHellsIngressActionId,
            41 when config.GapCloserVPR => ActionUse.ViperSlitherActionId,
            24 when config.GapCloserWHM => ActionUse.WhiteMageAetherialShiftActionId,
            25 when config.GapCloserBLM => ActionUse.BlackMageAetherialManipulationActionId,
            35 when config.GapCloserRDM => ActionUse.RedMageCorpsACorpsActionId,
            40 when config.GapCloserSGE => ActionUse.SageIcarusActionId,
            42 when config.GapCloserPCT => ActionUse.PictomancerSmudgeActionId,
            _ => null
        };
    }

    internal bool IsUnknownBossLikeTarget(IGameObject target)
    {
        return target is IBattleNpc battleNpc &&
               battleNpc.BattleNpcKind == BattleNpcSubKind.Combatant &&
               target.HitboxRadius >= BossLikeHitboxRadius &&
               !bossMod.HasModuleByDataId(battleNpc.BaseId);
    }

    internal bool HasNearbyUnknownBossLikeThreat(Vector3 playerPosition)
    {
        foreach (var obj in services.ObjectTable.OfType<IBattleNpc>())
        {
            if (obj.BattleNpcKind != BattleNpcSubKind.Combatant)
            {
                continue;
            }

            if (obj.HitboxRadius < BossLikeHitboxRadius || bossMod.HasModuleByDataId(obj.BaseId))
            {
                continue;
            }

            if (Vector3.Distance(playerPosition, obj.Position) <= 60f)
            {
                return true;
            }
        }

        return false;
    }

    private IEnumerable<Vector3> EnumerateShukuchiCandidates(Vector3 playerPosition, Vector3 targetPosition, float targetHitboxRadius)
    {
        var radius = targetHitboxRadius + CombatConstants.GapCloserDestinationMeleeRange;
        var toTarget = targetPosition - playerPosition;
        toTarget.Y = 0;
        if (toTarget.LengthSquared() > 0.0001f)
        {
            var direction = Vector3.Normalize(toTarget);
            yield return new Vector3(targetPosition.X - (direction.X * radius), playerPosition.Y, targetPosition.Z - (direction.Z * radius));
        }

        for (var i = 0; i < 16; i++)
        {
            var angle = i * (MathF.Tau / 16f);
            yield return new Vector3(
                targetPosition.X + MathF.Cos(angle) * radius,
                playerPosition.Y,
                targetPosition.Z + MathF.Sin(angle) * radius);
        }
    }

    private IEnumerable<IBattleChara> EnumerateFriendlyReengageTargets(IBattleChara player, IBattleChara currentTarget, float maxRange)
    {
        return PartyAllyProvider.EnumerateVisiblePartyAllies(services, player)
            .Where(ally =>
                ally.GameObjectId != player.GameObjectId &&
                ally.GameObjectId != 0 &&
                !ally.IsDead &&
                ally.CurrentHp > 0 &&
                Vector3.Distance(player.Position, ally.Position) <= maxRange)
            .OrderBy(ally => Geometry.DistanceToHitbox(ally.Position, player.HitboxRadius, currentTarget.Position, currentTarget.HitboxRadius))
            .ThenByDescending(ally => Vector3.Distance(player.Position, ally.Position));
    }

    private IGameObject? TryFindReaperPortal(IGameObject player)
    {
        foreach (var obj in services.ObjectTable)
        {
            if (obj.BaseId == ActionUse.ReaperHellsgatePortalDataId && obj.OwnerId == player.GameObjectId)
            {
                return obj;
            }
        }

        return null;
    }
}

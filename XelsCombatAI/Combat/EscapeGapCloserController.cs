using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Dalamud.Game.ClientState.Objects.Enums;
using Dalamud.Game.ClientState.Objects.Types;
using FFXIVClientStructs.FFXIV.Client.Game;

namespace XelsCombatAI.Combat;

internal sealed class EscapeGapCloserController(
    Configuration config,
    DalamudServices services,
    BossModReflectionSafety bossModSafety,
    MobilityDecisionEvaluator mobilityEvaluator,
    GapCloserController gapCloserController,
    DashStyleController dashStyleController,
    FacingController facingController,
    Func<Positional> positionalIntent,
    Func<BossModMovementDiagnostics> bossModMovementDiagnostics,
    Func<BossModMechanicPressure> mechanicPressure)
{
    private const float GreedGcdAssistUrgencySeconds = 2.5f;
    private const float GreedLastMomentAssistUrgencySeconds = 1.0f;
    private const float FarSafeAssistMinimumDistance = 12f;
    private const float FarSafeAssistBufferSeconds = 0.75f;
    private const float WalkSpeedYalmsPerSecond = 6f;
    private DateTime nextEscapeGapCloserAttempt = DateTime.MinValue;
    private DateTime escapeDangerDetectedAt = DateTime.MinValue;
    private string lastEscapeGapCloserSafety = "not checked";
    private Vector3? lastSafeEscapeDestination;

    public string LastEscapeGapCloserSafety => this.lastEscapeGapCloserSafety;
    public Vector3? LastSafeEscapeDestination => this.lastSafeEscapeDestination;

    public void Reset()
    {
        this.nextEscapeGapCloserAttempt = DateTime.MinValue;
        this.escapeDangerDetectedAt = DateTime.MinValue;
        this.lastEscapeGapCloserSafety = "not checked";
        this.lastSafeEscapeDestination = null;
    }

    public unsafe bool TryUseEscapeGapCloser()
    {
        if (DateTime.UtcNow < this.nextEscapeGapCloserAttempt)
        {
            return false;
        }

        this.nextEscapeGapCloserAttempt = DateTime.UtcNow.AddMilliseconds(250);

        var player = services.ObjectTable.LocalPlayer;
        if (player == null)
        {
            this.lastEscapeGapCloserSafety = "missing player";
            return false;
        }

        if (player.IsCasting)
        {
            this.lastEscapeGapCloserSafety = "player casting";
            return false;
        }

        if (ActionManager.Instance()->AnimationLock > 0)
        {
            this.lastEscapeGapCloserSafety = "animation lock";
            return false;
        }

        var classJobId = player.ClassJob.RowId;
        if (IsNinjaMudraWindow(player))
        {
            this.lastEscapeGapCloserSafety = "NIN mudra active";
            this.lastSafeEscapeDestination = null;
            mobilityEvaluator.RecordIdle(MobilityIntent.Safety, "Gap closer", this.lastEscapeGapCloserSafety);
            return false;
        }

        if (config.CombatStyle != CombatStyle.Normal && classJobId == 25 && this.HasActiveCircleOfPower())
        {
            this.lastEscapeGapCloserSafety = "disabled in Greed mode while in Ley Lines";
            return false;
        }

        if (!bossModSafety.TryIsPositionSafe(player.Position, out var currentSafe, out var currentReason))
        {
            this.lastEscapeGapCloserSafety = currentReason;
            return false;
        }

        var hasSafeMovementIntent = bossModSafety.TryGetSafeMovementIntent(player.Position, out var safeMovementDestination, out var intentReason);
        if (currentSafe && !hasSafeMovementIntent)
        {
            this.escapeDangerDetectedAt = DateTime.MinValue;
            this.lastEscapeGapCloserSafety = "current position safe";
            this.lastSafeEscapeDestination = null;
            return false;
        }

        if (!currentSafe && !hasSafeMovementIntent)
        {
            this.lastEscapeGapCloserSafety = intentReason;
            return false;
        }

        var safeMovementDistance = Geometry.Distance2D(player.Position, safeMovementDestination);
        var safeAssistReason = string.Empty;
        var canAssistSafeMovement = !currentSafe || this.ShouldAssistSafeBossModMovement(safeMovementDistance, out safeAssistReason);
        if (!canAssistSafeMovement)
        {
            this.escapeDangerDetectedAt = DateTime.MinValue;
            this.lastEscapeGapCloserSafety = safeAssistReason;
            this.lastSafeEscapeDestination = null;
            mobilityEvaluator.RecordIdle(MobilityIntent.Safety, "Gap closer", safeAssistReason);
            return false;
        }

        var now = DateTime.UtcNow;
        if (this.escapeDangerDetectedAt == DateTime.MinValue)
        {
            this.escapeDangerDetectedAt = now;
        }

        if (ShouldSuppressLateEscapeGapCloser(
                currentSafe,
                safeMovementDistance,
                config.MinimumGapCloserDistance,
                canAssistSafeMovement,
                (now - this.escapeDangerDetectedAt).TotalMilliseconds))
        {
            this.lastEscapeGapCloserSafety = "already walking to safety";
            return false;
        }

        if (safeMovementDistance < config.MinimumGapCloserDistance)
        {
            this.lastEscapeGapCloserSafety = $"safe movement under {config.MinimumGapCloserDistance:0}y";
            return false;
        }

        if (this.TryUsePhantomEscapeGapCloser(classJobId, safeMovementDestination))
        {
            return true;
        }

        return classJobId switch
        {
            2 or 20 when config.GapCloserMNK => this.TryUseFriendlyEscapeGapCloser(ActionUse.MonkThunderclapActionId, "Thunderclap", CombatConstants.GapCloserMaxRange, safeMovementDestination) || this.TryUseGreedyTargetEscapeGapCloser(ActionUse.MonkThunderclapActionId, "Thunderclap", safeMovementDestination),
            4 or 22 when config.GapCloserDRG => this.TryUseBackstepEscapeGapCloser(ActionUse.DragoonElusiveJumpActionId, CombatConstants.FixedForwardGapCloserRange, "Elusive Jump", safeMovementDestination) || this.TryUseGreedyTargetEscapeGapCloser(ActionUse.DragoonWingedGlideActionId, "Winged Glide", safeMovementDestination),
            5 or 23 when config.GapCloserBRD => this.TryUseTargetBackstepEscapeGapCloser(ActionUse.BardRepellingShotActionId, "Repelling Shot", 15f, 10f, safeMovementDestination),
            25 when config.GapCloserBLM => this.TryUseFriendlyEscapeGapCloser(ActionUse.BlackMageAetherialManipulationActionId, "Aetherial Manipulation", 25f, safeMovementDestination),
            29 or 30 when config.GapCloserNIN => this.TryUseLocationEscapeGapCloser(ActionUse.NinjaShukuchiActionId, CombatConstants.GapCloserMaxRange, "Shukuchi", safeMovementDestination),
            34 when config.GapCloserSAM => this.TryUseTargetBackstepEscapeGapCloser(ActionUse.SamuraiYatenActionId, "Yaten", 5f, 10f, safeMovementDestination) || this.TryUseGreedyTargetEscapeGapCloser(ActionUse.SamuraiGyotenActionId, "Gyoten", safeMovementDestination),
            39 when config.GapCloserRPR => gapCloserController.TryUseReaperRegress(ref this.lastEscapeGapCloserSafety, safeMovementDestination: safeMovementDestination) || this.TryUseBackstepEscapeGapCloser(ActionUse.ReaperHellsEgressActionId, CombatConstants.FixedForwardGapCloserRange, "Hell's Egress", safeMovementDestination) || this.TryUseForwardEscapeGapCloser(ActionUse.ReaperHellsIngressActionId, "Hell's Ingress", safeMovementDestination),
            24 when config.GapCloserWHM => this.TryUseForwardEscapeGapCloser(ActionUse.WhiteMageAetherialShiftActionId, "Aetherial Shift", safeMovementDestination),
            35 when config.GapCloserRDM => this.TryUseTargetBackstepEscapeGapCloser(ActionUse.RedMageDisplacementActionId, "Displacement", 5f, CombatConstants.FixedForwardGapCloserRange, safeMovementDestination),
            40 when config.GapCloserSGE => this.TryUseFriendlyEscapeGapCloser(ActionUse.SageIcarusActionId, "Icarus", 25f, safeMovementDestination),
            41 when config.GapCloserVPR => this.TryUseFriendlyEscapeGapCloser(ActionUse.ViperSlitherActionId, "Slither", CombatConstants.GapCloserMaxRange, safeMovementDestination) || this.TryUseGreedyTargetEscapeGapCloser(ActionUse.ViperSlitherActionId, "Slither", safeMovementDestination),
            38 when config.GapCloserDNC => this.TryUseForwardEscapeGapCloser(ActionUse.DancerEnAvantActionId, "En Avant", safeMovementDestination),
            42 when config.GapCloserPCT => this.TryUseForwardEscapeGapCloser(ActionUse.PictomancerSmudgeActionId, "Smudge", safeMovementDestination),
            _ => false
        };
    }

    private bool TryUsePhantomEscapeGapCloser(uint classJobId, Vector3 safeMovementDestination)
    {
        return (ShouldAllowPhantomTargetEscapeGapCloser(classJobId, config.UsePhantomGapClosers, config.IsPhantomGapCloserJobEnabled(classJobId)) &&
                this.TryUseGreedyTargetEscapeGapCloser(
                    ActionUse.PhantomKickActionId,
                    "Phantom Kick",
                    safeMovementDestination,
                    CombatConstants.PhantomKickMaxRange)) ||
               (ShouldAllowPhantomForwardEscapeGapCloser(classJobId, config.UsePhantomGapClosers, config.IsPhantomGapCloserJobEnabled(classJobId)) &&
                this.TryUseForwardEscapeGapCloser(ActionUse.OccultFeatherfootActionId, "Occult Featherfoot", safeMovementDestination));
    }

    internal static bool ShouldAllowPhantomTargetEscapeGapCloser(uint classJobId, bool phantomGapClosersEnabled, bool currentJobGapCloserEnabled)
    {
        return phantomGapClosersEnabled &&
               currentJobGapCloserEnabled;
    }

    internal static bool ShouldAllowPhantomForwardEscapeGapCloser(uint classJobId, bool phantomGapClosersEnabled, bool currentJobGapCloserEnabled)
    {
        return phantomGapClosersEnabled &&
               currentJobGapCloserEnabled &&
               !JobRoles.IsTankJob(classJobId);
    }

    internal static bool ShouldSuppressLateEscapeGapCloser(bool currentSafe, float safeMovementDistance, float minimumGapCloserDistance, bool canAssistSafeMovement, double dangerElapsedMilliseconds)
    {
        return dangerElapsedMilliseconds > CombatConstants.EscapeGapCloserDangerWindowMilliseconds &&
               ((currentSafe && !canAssistSafeMovement) || safeMovementDistance < minimumGapCloserDistance);
    }

    private unsafe bool TryUseFriendlyEscapeGapCloser(uint actionId, string actionName, float maxRange, Vector3 safeMovementDestination)
    {
        var player = services.ObjectTable.LocalPlayer;
        if (player == null)
        {
            return false;
        }

        if (!ActionUse.CanUseAction(actionId))
        {
            this.lastEscapeGapCloserSafety = "action unavailable";
            return false;
        }

        if (dashStyleController.EscapeStyleActive)
        {
            var styleCandidates = new List<DashStyleCandidate<IBattleChara>>();
            foreach (var ally in this.EnumerateFriendlyEscapeTargets(player, maxRange))
            {
                if (!mobilityEvaluator.TryValidateDashDestination(
                    player,
                    ally.Position,
                    services.TargetManager.Target as IBattleChara,
                    safeMovementDestination,
                    MobilityIntent.Safety,
                    actionName,
                    actionId,
                    config.MinimumGapCloserDistance,
                    requireSafetyProgress: true,
                    requireUptimeProgress: false,
                    requireVnavReachable: true,
                    out var decision))
                {
                    this.lastEscapeGapCloserSafety = decision.RiskReason;
                    continue;
                }

                styleCandidates.Add(dashStyleController.ScoreCandidate(
                    ally,
                    player,
                    ally.Position,
                    services.TargetManager.Target as IBattleChara,
                    safeMovementDestination,
                    decision,
                    "ally anchor"));
            }

            if (dashStyleController.TrySelectBest(styleCandidates, out var selected))
            {
                this.lastSafeEscapeDestination = selected.Destination;
                var used = ActionManager.Instance()->UseAction(ActionType.Action, actionId, selected.Source.GameObjectId);
                mobilityEvaluator.RecordActionResult(selected.Decision, used, used ? "action used" : "action failed");
                if (used)
                {
                    dashStyleController.RecordStyleUse(selected.Reason);
                    this.lastEscapeGapCloserSafety = $"used {actionName} on ally ({selected.Decision.IntentLabel}, {selected.Reason})";
                    return true;
                }

                this.lastEscapeGapCloserSafety = $"failed to use {actionName} on ally ({selected.Decision.IntentLabel}, {selected.Reason})";
                return false;
            }

            if (string.IsNullOrEmpty(this.lastEscapeGapCloserSafety) || this.lastEscapeGapCloserSafety == "current position safe")
            {
                this.lastEscapeGapCloserSafety = "no safe ally found";
            }

            return false;
        }

        foreach (var ally in this.EnumerateFriendlyEscapeTargets(player, maxRange))
        {
            if (!mobilityEvaluator.TryValidateDashDestination(
                player,
                ally.Position,
                services.TargetManager.Target as IBattleChara,
                safeMovementDestination,
                MobilityIntent.Safety,
                actionName,
                actionId,
                config.MinimumGapCloserDistance,
                requireSafetyProgress: true,
                requireUptimeProgress: false,
                requireVnavReachable: true,
                out var decision))
            {
                this.lastEscapeGapCloserSafety = decision.RiskReason;
                continue;
            }

            this.lastSafeEscapeDestination = ally.Position;
            var used = ActionManager.Instance()->UseAction(ActionType.Action, actionId, ally.GameObjectId);
            mobilityEvaluator.RecordActionResult(decision, used, used ? "action used" : "action failed");
            if (used)
            {
                this.lastEscapeGapCloserSafety = $"used {actionName} on ally ({decision.IntentLabel})";
                return true;
            }

            this.lastEscapeGapCloserSafety = $"failed to use {actionName} on ally ({decision.IntentLabel})";
        }

        if (string.IsNullOrEmpty(this.lastEscapeGapCloserSafety) || this.lastEscapeGapCloserSafety == "current position safe")
        {
            this.lastEscapeGapCloserSafety = "no safe ally found";
        }

        return false;
    }

    private unsafe bool TryUseLocationEscapeGapCloser(uint actionId, float maxRange, string actionName, Vector3 safeMovementDestination)
    {
        var player = services.ObjectTable.LocalPlayer;
        if (player == null)
        {
            return false;
        }

        if (!ActionUse.CanUseAction(actionId))
        {
            this.lastEscapeGapCloserSafety = "action unavailable";
            return false;
        }

        var target = services.TargetManager.Target as IBattleChara;
        var desiredPositional = this.ResolveDesiredDashPositional(player, target);
        if (dashStyleController.EscapeStyleActive)
        {
            var styleCandidates = new List<DashStyleCandidate<Vector3>>();
            foreach (var candidate in this.EnumerateGreedyEscapeLocationCandidates(player.Position, safeMovementDestination, maxRange, target, desiredPositional))
            {
                if (!mobilityEvaluator.TryValidateDashDestination(
                    player,
                    candidate,
                    target,
                    safeMovementDestination,
                    MobilityIntent.Safety,
                    actionName,
                    actionId,
                    config.MinimumGapCloserDistance,
                    requireSafetyProgress: true,
                    requireUptimeProgress: false,
                    requireVnavReachable: true,
                    out var decision))
                {
                    this.lastEscapeGapCloserSafety = decision.RiskReason;
                    continue;
                }

                styleCandidates.Add(dashStyleController.ScoreCandidate(
                    candidate,
                    player,
                    candidate,
                    target,
                    safeMovementDestination,
                    decision,
                    PositionalDashPolicy.IsActive(desiredPositional) &&
                    target != null &&
                    PositionalDashPolicy.IsSatisfied(desiredPositional, candidate, target.Position, target.Rotation)
                        ? "positional Shukuchi"
                        : "precision Shukuchi"));
            }

            if (dashStyleController.TrySelectBest(styleCandidates, out var selected))
            {
                this.lastSafeEscapeDestination = selected.Destination;
                var location = selected.Destination;
                var used = ActionManager.Instance()->UseActionLocation(ActionType.Action, actionId, player.GameObjectId, &location);
                mobilityEvaluator.RecordActionResult(selected.Decision, used, used ? "action used" : "action failed");
                if (used)
                {
                    dashStyleController.RecordStyleUse(selected.Reason);
                }

                this.lastEscapeGapCloserSafety = used ? $"used {actionName} ({selected.Decision.IntentLabel}, {selected.Reason})" : $"failed to use {actionName} ({selected.Decision.IntentLabel}, {selected.Reason})";
                return used;
            }

            if (string.IsNullOrEmpty(this.lastEscapeGapCloserSafety) || this.lastEscapeGapCloserSafety == "current position safe")
            {
                this.lastEscapeGapCloserSafety = $"no safe {actionName} escape destination";
            }

            return false;
        }

        var positionalCandidates = PositionalDashPolicy.IsActive(desiredPositional)
            ? new List<DashStyleCandidate<Vector3>>()
            : null;
        MobilityDecisionDiagnostics? firstFallbackDecision = null;
        var firstFallbackDestination = default(Vector3);

        foreach (var candidate in this.EnumerateEscapeLocationCandidates(player.Position, maxRange, target, desiredPositional))
        {
            if (!mobilityEvaluator.TryValidateDashDestination(
                player,
                candidate,
                target,
                safeMovementDestination,
                MobilityIntent.Safety,
                actionName,
                actionId,
                config.MinimumGapCloserDistance,
                requireSafetyProgress: true,
                requireUptimeProgress: false,
                requireVnavReachable: true,
                out var decision))
            {
                this.lastEscapeGapCloserSafety = decision.RiskReason;
                continue;
            }

            if (PositionalDashPolicy.IsActive(desiredPositional) &&
                target != null &&
                PositionalDashPolicy.IsSatisfied(desiredPositional, candidate, target.Position, target.Rotation))
            {
                positionalCandidates!.Add(dashStyleController.ScoreCandidate(
                    candidate,
                    player,
                    candidate,
                    target,
                    safeMovementDestination,
                    decision,
                    "positional Shukuchi"));
                continue;
            }

            if (positionalCandidates != null)
            {
                firstFallbackDecision ??= decision;
                firstFallbackDestination = candidate;
                continue;
            }

            this.lastSafeEscapeDestination = candidate;
            var location = candidate;
            var used = ActionManager.Instance()->UseActionLocation(ActionType.Action, actionId, player.GameObjectId, &location);
            mobilityEvaluator.RecordActionResult(decision, used, used ? "action used" : "action failed");
            this.lastEscapeGapCloserSafety = used ? $"used {actionName} ({decision.IntentLabel})" : $"failed to use {actionName} ({decision.IntentLabel})";
            return used;
        }

        if (positionalCandidates != null && dashStyleController.TrySelectBest(positionalCandidates, out var selectedPositional))
        {
            this.lastSafeEscapeDestination = selectedPositional.Destination;
            var location = selectedPositional.Destination;
            var used = ActionManager.Instance()->UseActionLocation(ActionType.Action, actionId, player.GameObjectId, &location);
            mobilityEvaluator.RecordActionResult(selectedPositional.Decision, used, used ? "action used" : "action failed");
            this.lastEscapeGapCloserSafety = used ? $"used {actionName} ({selectedPositional.Decision.IntentLabel}, {selectedPositional.Reason})" : $"failed to use {actionName} ({selectedPositional.Decision.IntentLabel}, {selectedPositional.Reason})";
            return used;
        }

        if (firstFallbackDecision != null)
        {
            this.lastSafeEscapeDestination = firstFallbackDestination;
            var location = firstFallbackDestination;
            var used = ActionManager.Instance()->UseActionLocation(ActionType.Action, actionId, player.GameObjectId, &location);
            mobilityEvaluator.RecordActionResult(firstFallbackDecision, used, used ? "action used" : "action failed");
            this.lastEscapeGapCloserSafety = used ? $"used {actionName} ({firstFallbackDecision.IntentLabel})" : $"failed to use {actionName} ({firstFallbackDecision.IntentLabel})";
            return used;
        }

        if (string.IsNullOrEmpty(this.lastEscapeGapCloserSafety) || this.lastEscapeGapCloserSafety == "current position safe")
        {
            this.lastEscapeGapCloserSafety = $"no safe {actionName} escape destination";
        }

        return false;
    }

    private unsafe bool TryUseForwardEscapeGapCloser(uint actionId, string actionName, Vector3 safeMovementDestination)
    {
        var player = services.ObjectTable.LocalPlayer;
        if (player == null)
        {
            return false;
        }

        if (!ActionUse.CanUseAction(actionId))
        {
            this.lastEscapeGapCloserSafety = "action unavailable";
            return false;
        }

        var destination = player.Position + Geometry.RotationToDirection(player.Rotation) * CombatConstants.FixedForwardGapCloserRange;
        if (!mobilityEvaluator.TryValidateDashDestination(
            player,
            destination,
            services.TargetManager.Target as IBattleChara,
            safeMovementDestination,
            MobilityIntent.Safety,
            actionName,
            actionId,
            config.MinimumGapCloserDistance,
            requireSafetyProgress: true,
            requireUptimeProgress: false,
            requireVnavReachable: true,
            out var decision))
        {
            if (this.TryRequestFixedEscapeDashFacing(player, actionId, actionName, CombatConstants.FixedForwardGapCloserRange, safeMovementDestination, backward: false))
            {
                return true;
            }

            this.lastEscapeGapCloserSafety = decision.RiskReason;
            return false;
        }

        this.lastSafeEscapeDestination = destination;
        var used = ActionManager.Instance()->UseAction(ActionType.Action, actionId, player.GameObjectId);
        mobilityEvaluator.RecordActionResult(decision, used, used ? "action used" : "action failed");
        if (used)
        {
            this.RecordEscapeActionUsed(actionId, actionName, dashStyleController.EscapeStyleActive ? "fixed escape" : null);
        }

        this.lastEscapeGapCloserSafety = used ? $"used {actionName} ({decision.IntentLabel})" : $"failed to use {actionName} ({decision.IntentLabel})";
        return used;
    }

    private unsafe bool TryUseBackstepEscapeGapCloser(uint actionId, float backstepDistance, string actionName, Vector3 safeMovementDestination)
    {
        var player = services.ObjectTable.LocalPlayer;
        if (player == null)
        {
            return false;
        }

        if (!ActionUse.CanUseAction(actionId))
        {
            this.lastEscapeGapCloserSafety = $"{actionName} unavailable";
            return false;
        }

        var destination = player.Position - Geometry.RotationToDirection(player.Rotation) * backstepDistance;
        if (!mobilityEvaluator.TryValidateDashDestination(
            player,
            destination,
            services.TargetManager.Target as IBattleChara,
            safeMovementDestination,
            MobilityIntent.Safety,
            actionName,
            actionId,
            config.MinimumGapCloserDistance,
            requireSafetyProgress: true,
            requireUptimeProgress: false,
            requireVnavReachable: true,
            out var decision))
        {
            if (this.TryRequestFixedEscapeDashFacing(player, actionId, actionName, backstepDistance, safeMovementDestination, backward: true))
            {
                return true;
            }

            this.lastEscapeGapCloserSafety = decision.RiskReason;
            return false;
        }

        this.lastSafeEscapeDestination = destination;
        var used = ActionManager.Instance()->UseAction(ActionType.Action, actionId, player.GameObjectId);
        mobilityEvaluator.RecordActionResult(decision, used, used ? "action used" : "action failed");
        if (used)
        {
            this.RecordEscapeActionUsed(actionId, actionName, dashStyleController.EscapeStyleActive ? "backstep escape" : null);
        }

        this.lastEscapeGapCloserSafety = used ? $"used {actionName} ({decision.IntentLabel})" : $"failed to use {actionName} ({decision.IntentLabel})";
        return used;
    }

    private unsafe bool TryUseTargetBackstepEscapeGapCloser(uint actionId, string actionName, float maxTargetRange, float backstepDistance, Vector3 safeMovementDestination)
    {
        var player = services.ObjectTable.LocalPlayer;
        if (player == null)
        {
            return false;
        }

        if (!ActionUse.CanUseAction(actionId))
        {
            this.lastEscapeGapCloserSafety = $"{actionName} unavailable";
            return false;
        }

        if (dashStyleController.EscapeStyleActive)
        {
            var styleCandidates = new List<DashStyleCandidate<IBattleNpc>>();
            foreach (var enemy in this.EnumerateBackstepTargets(player, maxTargetRange))
            {
                if (!TryCalculateTargetBackstepDestination(player, enemy, backstepDistance, out var destination))
                {
                    this.lastEscapeGapCloserSafety = $"could not calculate {actionName} landing";
                    continue;
                }

                if (!mobilityEvaluator.TryValidateDashDestination(
                    player,
                    destination,
                    services.TargetManager.Target as IBattleChara,
                    safeMovementDestination,
                    MobilityIntent.Safety,
                    actionName,
                    actionId,
                    config.MinimumGapCloserDistance,
                    requireSafetyProgress: true,
                    requireUptimeProgress: false,
                    requireVnavReachable: true,
                    out var decision))
                {
                    this.lastEscapeGapCloserSafety = decision.RiskReason;
                    continue;
                }

                styleCandidates.Add(dashStyleController.ScoreCandidate(
                    enemy,
                    player,
                    destination,
                    services.TargetManager.Target as IBattleChara,
                    safeMovementDestination,
                    decision,
                    "safe backstep"));
            }

            if (dashStyleController.TrySelectBest(styleCandidates, out var selected))
            {
                this.lastSafeEscapeDestination = selected.Destination;
                var used = ActionManager.Instance()->UseAction(ActionType.Action, actionId, selected.Source.GameObjectId);
                mobilityEvaluator.RecordActionResult(selected.Decision, used, used ? "action used" : "action failed");
                if (used)
                {
                    this.RecordEscapeActionUsed(actionId, actionName, selected.Reason);
                }

                this.lastEscapeGapCloserSafety = used ? $"used {actionName} ({selected.Decision.IntentLabel}, {selected.Reason})" : $"failed to use {actionName} ({selected.Decision.IntentLabel}, {selected.Reason})";
                return used;
            }

            if (string.IsNullOrEmpty(this.lastEscapeGapCloserSafety) || this.lastEscapeGapCloserSafety == "current position safe")
            {
                this.lastEscapeGapCloserSafety = $"no safe {actionName} target";
            }

            return false;
        }

        foreach (var enemy in this.EnumerateBackstepTargets(player, maxTargetRange))
        {
            if (!TryCalculateTargetBackstepDestination(player, enemy, backstepDistance, out var destination))
            {
                this.lastEscapeGapCloserSafety = $"could not calculate {actionName} landing";
                continue;
            }

            if (!mobilityEvaluator.TryValidateDashDestination(
                player,
                destination,
                services.TargetManager.Target as IBattleChara,
                safeMovementDestination,
                MobilityIntent.Safety,
                actionName,
                actionId,
                config.MinimumGapCloserDistance,
                requireSafetyProgress: true,
                requireUptimeProgress: false,
                requireVnavReachable: true,
                out var decision))
            {
                this.lastEscapeGapCloserSafety = decision.RiskReason;
                continue;
            }

            this.lastSafeEscapeDestination = destination;
            var used = ActionManager.Instance()->UseAction(ActionType.Action, actionId, enemy.GameObjectId);
            mobilityEvaluator.RecordActionResult(decision, used, used ? "action used" : "action failed");
            if (used)
            {
                this.RecordEscapeActionUsed(actionId, actionName, null);
            }

            this.lastEscapeGapCloserSafety = used ? $"used {actionName} ({decision.IntentLabel})" : $"failed to use {actionName} ({decision.IntentLabel})";
            return used;
        }

        if (string.IsNullOrEmpty(this.lastEscapeGapCloserSafety) || this.lastEscapeGapCloserSafety == "current position safe")
        {
            this.lastEscapeGapCloserSafety = $"no safe {actionName} target";
        }

        return false;
    }

    private bool TryRequestFixedEscapeDashFacing(IBattleChara player, uint actionId, string actionName, float dashDistance, Vector3 safeMovementDestination, bool backward)
    {
        var movementDirection = safeMovementDestination - player.Position;
        movementDirection.Y = 0f;
        if (movementDirection.LengthSquared() <= 0.0001f)
        {
            return false;
        }

        movementDirection = Vector3.Normalize(movementDirection);
        var desiredForward = backward ? -movementDirection : movementDirection;
        var desiredRotation = Geometry.DirectionToRotation(desiredForward);
        if (Geometry.AbsAngleDelta(player.Rotation, desiredRotation) <= FacingController.DirectionalDashToleranceRadians)
        {
            return false;
        }

        var destination = player.Position + movementDirection * dashDistance;
        if (!mobilityEvaluator.TryValidateDashDestination(
            player,
            destination,
            services.TargetManager.Target as IBattleChara,
            safeMovementDestination,
            MobilityIntent.Safety,
            actionName,
            actionId,
            config.MinimumGapCloserDistance,
            requireSafetyProgress: true,
            requireUptimeProgress: false,
            requireVnavReachable: true,
            out var decision))
        {
            this.lastEscapeGapCloserSafety = decision.RiskReason;
            return false;
        }

        facingController.RequestFacing(FacingController.CreateDirectionalDashRequest(desiredRotation, destination, $"turn for {actionName}", FacingBossModPolicy.AssistBmrMovementDash, safeMovementDestination));
        this.lastSafeEscapeDestination = destination;
        this.lastEscapeGapCloserSafety = $"turning for {actionName} ({decision.IntentLabel}, directional dash)";
        return true;
    }

    private unsafe bool TryUseGreedyTargetEscapeGapCloser(
        uint actionId,
        string actionName,
        Vector3 safeMovementDestination,
        float maxRange = CombatConstants.GapCloserMaxRange)
    {
        if (!this.GreedyUnsafeEscapeDashesEnabled())
        {
            return false;
        }

        var player = services.ObjectTable.LocalPlayer;
        if (player == null)
        {
            return false;
        }

        if (!ActionUse.CanUseAction(actionId))
        {
            this.lastEscapeGapCloserSafety = $"{actionName} unavailable";
            return false;
        }

        if (services.TargetManager.Target is not IBattleNpc target ||
            target.BattleNpcKind != BattleNpcSubKind.Combatant ||
            target.GameObjectId == 0 ||
            target.IsDead ||
            target.CurrentHp <= 0)
        {
            this.lastEscapeGapCloserSafety = "no emergency dash target";
            return false;
        }

        var distanceToHitbox = Geometry.DistanceToHitbox(player.Position, player.HitboxRadius, target.Position, target.HitboxRadius);
        if (distanceToHitbox > maxRange)
        {
            this.lastEscapeGapCloserSafety = "target not in emergency dash range";
            return false;
        }

        if (!Geometry.TryCalculateTargetDashDestination(player.Position, target.Position, distanceToHitbox, out var destination))
        {
            this.lastEscapeGapCloserSafety = $"could not calculate {actionName} landing";
            return false;
        }

        if (mobilityEvaluator.TryValidateDashDestination(
            player,
            destination,
            target,
            safeMovementDestination,
            MobilityIntent.Safety,
            actionName,
            actionId,
            config.MinimumGapCloserDistance,
            requireSafetyProgress: true,
            requireUptimeProgress: false,
            requireVnavReachable: true,
            out var safeDecision))
        {
            return this.TryUseTargetEscapeAction(actionId, actionName, target, destination, safeDecision);
        }

        var pressure = mechanicPressure();
        if (pressure.BadForGreedyDash && !pressure.KnockbackRecoveryActive)
        {
            this.lastEscapeGapCloserSafety = pressure.FormatOptionalMovementHoldReason();
            mobilityEvaluator.RecordIdle(MobilityIntent.Safety, actionName, this.lastEscapeGapCloserSafety);
            return false;
        }

        if (!mobilityEvaluator.TryValidateGreedyUnsafeEscapeDashDestination(
            player,
            destination,
            target,
            safeMovementDestination,
            actionName,
            actionId,
            config.MinimumGapCloserDistance,
            out var decision))
        {
            this.lastEscapeGapCloserSafety = decision.RiskReason == "landing is safe; normal escape validation required"
                ? safeDecision.RiskReason
                : decision.RiskReason;
            return false;
        }

        return this.TryUseTargetEscapeAction(actionId, actionName, target, destination, decision);
    }

    private unsafe bool TryUseTargetEscapeAction(uint actionId, string actionName, IBattleNpc target, Vector3 destination, MobilityDecisionDiagnostics decision)
    {
        this.lastSafeEscapeDestination = destination;
        var used = ActionManager.Instance()->UseAction(ActionType.Action, actionId, target.GameObjectId);
        mobilityEvaluator.RecordActionResult(decision, used, used ? "action used" : "action failed");
        this.lastEscapeGapCloserSafety = used ? $"used {actionName} ({decision.IntentLabel})" : $"failed to use {actionName} ({decision.IntentLabel})";
        return used;
    }

    private IEnumerable<IBattleChara> EnumerateFriendlyEscapeTargets(IBattleChara player, float maxRange)
    {
        return services.ObjectTable
            .OfType<IBattleChara>()
            .Where(ally =>
                ally.ObjectKind == ObjectKind.Pc &&
                ally.GameObjectId != player.GameObjectId &&
                ally.GameObjectId != 0 &&
                !ally.IsDead &&
                ally.CurrentHp > 0 &&
                Vector3.Distance(player.Position, ally.Position) <= maxRange)
            .OrderByDescending(ally => Vector3.Distance(player.Position, ally.Position));
    }

    private IEnumerable<IBattleNpc> EnumerateBackstepTargets(IBattleChara player, float maxTargetRange)
    {
        return services.ObjectTable
            .OfType<IBattleNpc>()
            .Where(enemy =>
                enemy.BattleNpcKind == BattleNpcSubKind.Combatant &&
                enemy.GameObjectId != 0 &&
                !enemy.IsDead &&
                enemy.CurrentHp > 0 &&
                Geometry.DistanceToHitbox(player.Position, player.HitboxRadius, enemy.Position, enemy.HitboxRadius) <= maxTargetRange)
            .OrderByDescending(enemy => enemy.GameObjectId == services.TargetManager.Target?.GameObjectId)
            .ThenBy(enemy => Geometry.Distance2D(player.Position, enemy.Position));
    }

    private IEnumerable<Vector3> EnumerateEscapeLocationCandidates(Vector3 playerPosition, float maxRange, IBattleChara? target, Positional desiredPositional)
    {
        if (target != null)
        {
            var ringRadius = target.HitboxRadius + CombatConstants.GapCloserDestinationMeleeRange;
            foreach (var candidate in PositionalDashPolicy.EnumeratePreferredLandings(playerPosition, target.Position, target.Rotation, ringRadius, desiredPositional))
            {
                if (Geometry.Distance2D(playerPosition, candidate) <= maxRange)
                {
                    yield return candidate;
                }
            }
        }

        foreach (var radius in CombatConstants.EscapeLocationRadii)
        {
            if (radius > maxRange)
            {
                continue;
            }

            for (var i = 0; i < 16; i++)
            {
                var angle = i * (MathF.Tau / 16f);
                yield return new Vector3(
                    playerPosition.X + MathF.Cos(angle) * radius,
                    playerPosition.Y,
                    playerPosition.Z + MathF.Sin(angle) * radius);
            }
        }
    }

    private IEnumerable<Vector3> EnumerateGreedyEscapeLocationCandidates(Vector3 playerPosition, Vector3 safeMovementDestination, float maxRange, IBattleChara? target, Positional desiredPositional)
    {
        if (target != null)
        {
            var ringRadius = target.HitboxRadius + CombatConstants.GapCloserDestinationMeleeRange;
            foreach (var candidate in PositionalDashPolicy.EnumeratePreferredLandings(playerPosition, target.Position, target.Rotation, ringRadius, desiredPositional))
            {
                if (Geometry.Distance2D(playerPosition, candidate) <= maxRange)
                {
                    yield return candidate;
                }
            }
        }

        if (Geometry.Distance2D(playerPosition, safeMovementDestination) <= maxRange)
        {
            yield return safeMovementDestination;
        }

        for (var i = 0; i < 16; i++)
        {
            var angle = i * (MathF.Tau / 16f);
            var direction = new Vector3(MathF.Cos(angle), 0f, MathF.Sin(angle));
            foreach (var offset in new[] { 2f, 4f })
            {
                var candidate = safeMovementDestination + direction * offset;
                candidate.Y = playerPosition.Y;
                if (Geometry.Distance2D(playerPosition, candidate) <= maxRange)
                {
                    yield return candidate;
                }
            }
        }

        foreach (var candidate in this.EnumerateEscapeLocationCandidates(playerPosition, maxRange, target: null, Positional.Any))
        {
            yield return candidate;
        }
    }

    private Positional ResolveDesiredDashPositional(IBattleChara player, IBattleChara? target)
    {
        var positional = positionalIntent();
        return target != null &&
               PositionalDashPolicy.IsActive(positional) &&
               !PositionalDashPolicy.IsSatisfied(positional, player.Position, target.Position, target.Rotation)
            ? positional
            : Positional.Any;
    }

    private bool ShouldAssistSafeBossModMovement(float safeMovementDistance, out string reason)
        => ShouldAssistSafeBossModMovement(
            config.CombatStyle,
            bossModMovementDiagnostics(),
            safeMovementDistance,
            config.MinimumGapCloserDistance,
            out reason);

    internal static bool ShouldAssistSafeBossModMovement(
        CombatStyle combatStyle,
        BossModMovementDiagnostics movement,
        float safeMovementDistance,
        float minimumGapCloserDistance,
        out string reason)
    {
        if (combatStyle == CombatStyle.Greed)
        {
            reason = "greedy timing allows safe movement dash";
            return true;
        }

        var farSafeZone = safeMovementDistance >= MathF.Max(FarSafeAssistMinimumDistance, minimumGapCloserDistance);
        if (!TryGetBossModMovementUrgency(movement, out var urgencySeconds))
        {
            reason = combatStyle == CombatStyle.Normal
                ? "current position safe; normal timing walks"
                : $"{combatStyle}: waiting for BMR movement timing";
            return false;
        }

        if (farSafeZone)
        {
            var walkTime = safeMovementDistance / WalkSpeedYalmsPerSecond;
            var farThreshold = walkTime + FarSafeAssistBufferSeconds;
            if (urgencySeconds <= farThreshold)
            {
                reason = $"{combatStyle}: far safe zone {safeMovementDistance:0.0}y, BMR movement due in {urgencySeconds:0.0}s";
                return true;
            }
        }

        if (combatStyle == CombatStyle.Normal)
        {
            reason = "current position safe; normal timing walks";
            return false;
        }

        var threshold = combatStyle == CombatStyle.GreedLastMoment
            ? GreedLastMomentAssistUrgencySeconds
            : GreedGcdAssistUrgencySeconds;
        if (urgencySeconds > threshold)
        {
            reason = $"{combatStyle}: BMR movement in {urgencySeconds:0.0}s";
            return false;
        }

        reason = $"{combatStyle}: BMR movement due in {urgencySeconds:0.0}s";
        return true;
    }

    private static bool TryGetBossModMovementUrgency(BossModMovementDiagnostics movement, out float urgencySeconds)
    {
        urgencySeconds = float.MaxValue;
        var found = false;
        AddUrgency(movement.NavigationDetails.ForceMovementIn, ref urgencySeconds, ref found);
        AddUrgency(movement.NavigationDetails.LeewaySeconds, ref urgencySeconds, ref found);
        return found;
    }

    private static void AddUrgency(float? value, ref float urgencySeconds, ref bool found)
    {
        if (!value.HasValue ||
            !float.IsFinite(value.Value) ||
            value.Value < 0f)
        {
            return;
        }

        urgencySeconds = MathF.Min(urgencySeconds, value.Value);
        found = true;
    }

    private void RecordEscapeActionUsed(uint actionId, string actionName, string? styleReason)
    {
        if (styleReason != null)
        {
            dashStyleController.RecordStyleUse(styleReason);
        }

        dashStyleController.RecordPairedReturn(actionId, actionName);
    }

    private bool HasActiveCircleOfPower()
    {
        return services.ObjectTable.LocalPlayer?.StatusList.Any(status => status.StatusId == ActionUse.CircleOfPowerStatusId && status.RemainingTime > 0) == true;
    }

    private bool GreedyUnsafeEscapeDashesEnabled()
    {
        return config.UseGapCloser && config.CombatStyle != CombatStyle.Normal;
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

    private static bool TryCalculateTargetBackstepDestination(IBattleChara player, IBattleNpc enemy, float backstepDistance, out Vector3 destination)
    {
        var awayFromTarget = player.Position - enemy.Position;
        awayFromTarget.Y = 0;
        if (awayFromTarget.LengthSquared() <= 0.0001f)
        {
            destination = default;
            return false;
        }

        awayFromTarget = Vector3.Normalize(awayFromTarget);
        destination = player.Position + awayFromTarget * backstepDistance;
        return true;
    }
}

using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Game.ClientState.Objects.Enums;
using Dalamud.Game.ClientState.Objects.Types;
using Dalamud.Plugin.Services;
using XelsCombatAI.Game;

namespace XelsCombatAI.Runtime;

internal sealed class CombatRuntime(
    Configuration config,
    DalamudServices services,
    BossModIpc bossMod,
    BossModMechanicPressureMonitor mechanicPressure,
    BossModRuntimeGate bossModGate,
    DependencyChecker dependencyChecker,
    BossModPresetController presetController,
    PositionalsController positionalsController,
    RotationSolverIpc rotationSolverIpc,
    RotationSolverActionReflection rotationSolverActions,
    BossModReflectionSafety bossModSafety,
    BossModGoalZoneHook aoeGoalHook,
    AoePackPositioningController aoePackPositioningController,
    PassageOfArmsPositioningController passageOfArmsPositioningController,
    HealerAoePositioningController healerAoePositioningController,
    SurvivabilityZonePositioningController survivabilityZonePositioningController,
    ArenaEdgePositioningController arenaEdgePositioningController,
    TankBehaviorController tankBehaviorController,
    RedMageMeleeComboController redMageMeleeComboController,
    CombatLogWriter combatLogWriter,
    ManualMovementInputDetector manualMovement,
    AutoFaceTargetOptionController autoFaceTargetOptionController,
    ManualCorrectionFeedback manualCorrectionFeedback,
    MobilityDecisionEvaluator mobilityDecisionEvaluator,
    GapCloserController gapCloserController,
    EscapeGapCloserController escapeGapCloserController,
    DashStyleController dashStyleController,
    FacingController facingController,
    JobRangeProvider jobRangeProvider,
    Action saveConfig,
    Action updateDtr,
    Action<string> print)
{
    private static readonly TimeSpan ManualMovementResumeDelay = TimeSpan.FromMilliseconds(350);
    private static readonly TimeSpan CombatDependencyGrace = TimeSpan.FromSeconds(15);
    private const int MaxReviewActorSnapshots = 48;
    private const float MaxReviewActorDistance = 60f;

    private bool wasDead;
    private bool wasInCombat;
    private DateTime nextRuntimeUpdate = DateTime.MinValue;
    private DateTime manualMovementSuppressUntil = DateTime.MinValue;
    private DateTime dependencyGraceUntil = DateTime.MinValue;
    private DateTime nextRuntimeErrorLog = DateTime.MinValue;
    private string? lastMissingDependencies;
    private readonly CombatHistory combatHistory = new();
    private bool combatHistorySaved;
    private bool combatHistoryActive;

    public bool AutomatedMovementSuppressed => DateTime.UtcNow < this.manualMovementSuppressUntil;

    public void OnFrameworkUpdate(IFramework framework)
    {
        try
        {
            this.OnFrameworkUpdateCore(framework);
        }
        catch (Exception ex)
        {
            if (DateTime.UtcNow >= this.nextRuntimeErrorLog)
            {
                services.Log.Warning(ex, "XCAI runtime update failed; integrations will be retried after plugin-manager churn settles.");
                this.nextRuntimeErrorLog = DateTime.UtcNow.AddSeconds(10);
            }

            bossModGate.Close();
            this.ResetRuntimeCache(resetBossModHook: false);
        }
    }

    private void OnFrameworkUpdateCore(IFramework framework)
    {
        _ = framework;
        jobRangeProvider.Tick();

        var combatEngagement = CombatEngagementDetector.Detect(services);
        if (!config.Enabled)
        {
            autoFaceTargetOptionController.Restore();
            this.HandleDisabled(flushCombatHistory: false);
            this.UpdateFightReviewLogging(combatEngagement.EffectiveInCombat, this.DisabledLoggingInactiveReason());
            return;
        }

        var now = DateTime.UtcNow;
        var manualMovementRequested = manualMovement.IsManualMovementRequested();
        autoFaceTargetOptionController.Update(now < this.manualMovementSuppressUntil || manualMovementRequested);
        var dependenciesAvailable = dependencyChecker.DependenciesAvailable(out var missing);
        var bossModAvailable = dependenciesAvailable || dependencyChecker.IsBossModAvailable();
        this.SetBossModGate(bossModAvailable);
        mechanicPressure.Update(bossMod);
        if (!dependenciesAvailable)
        {
            if (!this.ShouldContinueThroughTransientDependencyLoss(
                    combatEngagement.EffectiveInCombat,
                    now,
                    bossModAvailable))
            {
                this.UpdateFightReviewLogging(CombatEngagementDetector.IsEffectivelyInCombat(services), "dependencies unavailable");
                this.WaitForDependencies(missing, bossModAvailable);
                return;
            }
        }

        if (dependenciesAvailable)
        {
            this.ClearDependencyWaitState();
        }

        if (!combatEngagement.EffectiveInCombat)
        {
            if (this.wasInCombat || presetController.InitializedPreset)
            {
                this.HandleOutOfCombat();
            }

            this.UpdateFightReviewLogging(
                combatEngagement.EffectiveInCombat,
                this.combatHistoryActive && !this.IsDutyContextActive() ? "duty ended" : "combat ended");
            this.wasInCombat = false;
            this.wasDead = false;
            this.manualMovementSuppressUntil = DateTime.MinValue;
            autoFaceTargetOptionController.Update(manualMovementRequested);
            return;
        }

        if (!this.wasInCombat)
        {
            this.wasInCombat = true;
        }

        var isDead = services.Condition[ConditionFlag.Unconscious];
        if (isDead)
        {
            if (!this.wasDead)
            {
                this.HandleDeath();
            }

            this.UpdateFightReviewLogging(combatEngagement.EffectiveInCombat, "dead");
            this.wasDead = true;
            return;
        }

        if (this.wasDead)
        {
            this.ResetRuntimeCache();
        }

        this.wasDead = isDead;

        if (this.ShouldUseGoalHook())
        {
            aoeGoalHook.EnsureActive();
        }

        if (now < this.nextRuntimeUpdate)
        {
            return;
        }
        this.nextRuntimeUpdate = now.AddMilliseconds(250);
        redMageMeleeComboController.Tick();
        tankBehaviorController.Tick();

        if (!presetController.InitializedPreset && !presetController.Initialize())
        {
            this.UpdateFightReviewLogging(combatEngagement.EffectiveInCombat, "preset unavailable");
            return;
        }

        var suppressAutomatedMovement = this.ShouldSuppressAutomatedMovement(now, manualMovementRequested);
        presetController.ApplyStrategies(suppressAutomatedMovement);
        facingController.Update(now, suppressAutomatedMovement, aoeGoalHook.MovementDiagnostics);
        this.UpdateFightReviewLogging(combatEngagement.EffectiveInCombat, "logging disabled");
    }

    public bool SetEnabled(bool enabled, bool warn = true)
    {
        if (enabled && !dependencyChecker.DependenciesAvailable(out var missing, forceRefresh: true))
        {
            var bossModAvailable = dependencyChecker.IsBossModAvailable();
            this.SetBossModGate(bossModAvailable);
            config.Enabled = true;
            this.ResetRuntimeCache(resetBossModHook: bossModAvailable);
            saveConfig();
            if (warn)
            {
                services.Log.Verbose($"XCAI enabled while waiting for dependencies: {missing}.");
            }

            return true;
        }

        config.Enabled = enabled;
        if (!enabled)
        {
            this.DeactivatePresetIfBossModAvailable();
            this.ResetRuntimeCache(resetBossModHook: bossModGate.IsOpen);
            bossModGate.Close();
        }

        saveConfig();
        print(config.Enabled
            ? "Enabled."
            : config.FightReviewLoggingEnabled
                ? "Disabled. Run-review logging remains active."
                : "Disabled.");
        return true;
    }

    public void ResetRuntimeCache()
    {
        this.ResetRuntimeCache(resetBossModHook: bossModGate.IsOpen);
    }

    private void ResetRuntimeCache(bool resetBossModHook)
    {
        this.manualMovementSuppressUntil = DateTime.MinValue;
        autoFaceTargetOptionController.Restore();
        this.dependencyGraceUntil = DateTime.MinValue;
        mechanicPressure.Reset();
        presetController.ResetCache();
        aoePackPositioningController.Reset();
        passageOfArmsPositioningController.Reset();
        healerAoePositioningController.Reset();
        survivabilityZonePositioningController.Reset();
        tankBehaviorController.Reset();
        redMageMeleeComboController.Reset();
        if (resetBossModHook)
        {
            aoeGoalHook.Reset();
        }
        else
        {
            aoeGoalHook.MarkBossModUnavailable();
        }

        manualCorrectionFeedback.Reset();
        mobilityDecisionEvaluator.Reset();
        dashStyleController.Reset();
        facingController.Reset();
    }

    public void EnsureRsrTrueNorthDisabled()
    {
        positionalsController.EnsureRsrTrueNorthDisabled();
        updateDtr();
    }

    public string? GetDependencyWarning()
    {
        return dependencyChecker.GetDependencyWarning();
    }

    public string? GetTrueNorthWarning()
    {
        return dependencyChecker.GetTrueNorthWarning(positionalsController.RsrTrueNorthDisabled);
    }

    public RuntimeStatus GetStatus()
    {
        var player = services.ObjectTable.LocalPlayer;
        var target = services.TargetManager.Target;

        return new RuntimeStatus(
            config.Enabled,
            CombatEngagementDetector.IsEffectivelyInCombat(services),
            services.Condition[ConditionFlag.Unconscious],
            player?.ClassJob.RowId ?? 0,
            services.ClientState.TerritoryType,
            services.DutyState.ContentFinderCondition.RowId,
            player?.Position,
            player?.Rotation ?? 0f,
            jobRangeProvider.PackAoeRange,
            jobRangeProvider.EngagementRange,
            target != null,
            target?.BaseId ?? 0,
            target?.GameObjectId ?? 0,
            target?.Position,
            target?.Rotation ?? 0f,
            target?.HitboxRadius ?? 0f,
            player != null ? PartyAllyProvider.GetVisiblePartyAllies(services, player).Members.Count : services.PartyList.Count,
            mechanicPressure.Current,
            this.GetDependencyWarning(),
            this.GetTrueNorthWarning(),
            positionalsController.RsrTrueNorthDisabled,
            rotationSolverIpc.Diagnostics,
            rotationSolverActions.RedMageMeleeDiagnostics,
            presetController.LastPositional,
            positionalsController.GetTrueNorthCharges(),
            positionalsController.HasActiveTrueNorth(),
            presetController.LastTargetUptimeRange,
            presetController.LastMovement,
            presetController.LastMovementRangeStrategy,
            presetController.LastForbiddenZoneCushion,
            presetController.LastLeylinesBetweenTheLines,
            presetController.LastLeylinesRetrace,
            presetController.LastLeylinesGoal,
            config.GapCloserPLD,
            config.GapCloserWAR,
            config.GapCloserDRK,
            config.GapCloserGNB,
            config.GapCloserMNK,
            config.GapCloserDRG,
            config.GapCloserBRD,
            config.GapCloserNIN,
            config.GapCloserSAM,
            config.GapCloserDNC,
            config.GapCloserRPR,
            config.GapCloserVPR,
            config.GapCloserWHM,
            config.GapCloserBLM,
            config.GapCloserRDM,
            config.GapCloserSGE,
            config.GapCloserPCT,
            dashStyleController.ReengageStyleActive || dashStyleController.EscapeStyleActive,
            dashStyleController.LastStyleReason,
            config.UseGapCloser && config.CombatStyle != CombatStyle.Normal,
            bossModSafety.Status,
            bossModSafety.Diagnostics,
            aoeGoalHook.Status,
            aoeGoalHook.LastGoalPriority,
            aoeGoalHook.LastGoalSources,
            aoeGoalHook.Diagnostics,
            aoeGoalHook.MovementDiagnostics,
            aoePackPositioningController.Status,
            passageOfArmsPositioningController.Status,
            healerAoePositioningController.Status, // HealerCoveragePositioning
            survivabilityZonePositioningController.Status,
            redMageMeleeComboController.Status,
            manualMovement.Status,
            this.AutomatedMovementSuppressed,
            facingController.Status,
            mobilityDecisionEvaluator.LastDecision,
            gapCloserController.LastGapCloserSafety,
            escapeGapCloserController.LastEscapeGapCloserSafety,
            escapeGapCloserController.LastSafeEscapeDestination,
            presetController.InitializedPreset,
            arenaEdgePositioningController.LastReason);
    }

    public void DisposeRuntime()
    {
        if (this.combatHistoryActive || this.combatHistory.HasFrames)
        {
            this.FlushCombatHistory("plugin disposed");
        }

        if (config.Enabled)
        {
            this.DeactivatePresetIfBossModAvailable();
        }

        aoeGoalHook.Dispose();
        survivabilityZonePositioningController.Dispose();
        redMageMeleeComboController.Dispose();
        autoFaceTargetOptionController.Restore();
    }

    private void HandleOutOfCombat()
    {
        this.DeactivatePresetIfBossModAvailable();

        this.ResetRuntimeCache();
    }

    private void WaitForDependencies(string missing, bool bossModAvailable)
    {
        this.DeactivatePresetIfBossModAvailable(bossModAvailable);

        this.ResetRuntimeCache(resetBossModHook: bossModAvailable);
        if (!string.Equals(this.lastMissingDependencies, missing, StringComparison.Ordinal))
        {
            this.lastMissingDependencies = missing;
            services.Log.Verbose($"XCAI waiting for dependencies: {missing}.");
            updateDtr();
        }
    }

    private void DeactivatePresetIfBossModAvailable()
    {
        this.DeactivatePresetIfBossModAvailable(dependencyChecker.IsBossModAvailable());
    }

    private void DeactivatePresetIfBossModAvailable(bool bossModAvailable)
    {
        this.SetBossModGate(bossModAvailable);
        if (!presetController.InitializedPreset)
        {
            return;
        }

        if (bossModAvailable)
        {
            presetController.Deactivate();
            return;
        }

        presetController.MarkUninitialized();
    }

    private void SetBossModGate(bool bossModAvailable)
    {
        if (bossModAvailable)
        {
            bossModGate.Open();
            return;
        }

        bossModGate.Close();
    }

    private void ClearDependencyWaitState()
    {
        this.dependencyGraceUntil = DateTime.MinValue;
        if (this.lastMissingDependencies == null)
        {
            return;
        }

        this.lastMissingDependencies = null;
        updateDtr();
    }

    private bool ShouldContinueThroughTransientDependencyLoss(bool effectiveInCombat, DateTime now, bool bossModAvailable)
    {
        if (!effectiveInCombat || !bossModAvailable)
        {
            this.dependencyGraceUntil = DateTime.MinValue;
            return false;
        }

        if (presetController.InitializedPreset && this.dependencyGraceUntil == DateTime.MinValue)
        {
            this.dependencyGraceUntil = now.Add(CombatDependencyGrace);
        }

        return this.dependencyGraceUntil != DateTime.MinValue &&
               now < this.dependencyGraceUntil;
    }

    private bool ShouldSuppressAutomatedMovement(DateTime now, bool manualMovementRequested)
    {
        if (!config.RespectManualMovement)
        {
            this.manualMovementSuppressUntil = DateTime.MinValue;
            return false;
        }

        if (manualMovementRequested)
        {
            this.manualMovementSuppressUntil = now.Add(ManualMovementResumeDelay);
            var player = services.ObjectTable.LocalPlayer;
            if (player != null)
            {
                manualCorrectionFeedback.RecordManualMovement(player.Position, aoeGoalHook.LastGoalSources, aoeGoalHook.MovementDiagnostics, now);
            }
        }

        return now < this.manualMovementSuppressUntil;
    }

    private void HandleDisabled(bool flushCombatHistory = true)
    {
        if (!this.wasInCombat && !this.wasDead && !presetController.InitializedPreset)
        {
            bossModGate.Close();
            return;
        }

        if (flushCombatHistory && (this.wasInCombat || this.combatHistoryActive || this.combatHistory.HasFrames))
        {
            this.FlushCombatHistory("disabled");
        }

        this.DeactivatePresetIfBossModAvailable();

        this.wasInCombat = false;
        this.wasDead = false;
        this.ResetRuntimeCache();
        bossModGate.Close();
    }

    private string DisabledLoggingInactiveReason()
    {
        return this.combatHistoryActive && !this.IsDutyContextActive()
            ? "duty ended while disabled"
            : "plugin disabled";
    }

    private void HandleDeath()
    {
        this.DeactivatePresetIfBossModAvailable();

        this.ResetRuntimeCache();
    }

    private bool ShouldUseGoalHook()
    {
        return config.ManageAoePackPositioning ||
               config.KeepTrashTargetSelected ||
               config.AvoidStandingInsideEnemies ||
               config.ManageHealerCoverageZone ||
               config.ManageDefensiveGroundZonePositioning ||
               config.ManagePassageOfArmsPositioning ||
               config.AvoidArenaEdge ||
               config.ManageSocialTurning ||
               config.ManageSocialSpacing ||
               config.UseRedMageMeleeComboMovement ||
               config.TankIgnoreFrontConeMovement ||
               config.TankKeepFrontConeAwayFromParty;
    }

    private void FlushCombatHistory(string reason)
    {
        if (!config.FightReviewLoggingEnabled)
        {
            this.combatHistory.Reset();
            this.combatHistorySaved = false;
            this.combatHistoryActive = false;
            return;
        }

        if (this.combatHistorySaved)
        {
            return;
        }

        this.combatHistorySaved = true;
        combatLogWriter.WriteFight(this.combatHistory, config, reason);
        this.combatHistory.Reset();
        this.combatHistorySaved = false;
        this.combatHistoryActive = false;
    }

    private void UpdateFightReviewLogging(bool effectiveInCombat, string inactiveReason)
    {
        if (!config.FightReviewLoggingEnabled)
        {
            if (this.combatHistoryActive || this.combatHistory.HasFrames)
            {
                this.FlushCombatHistory(inactiveReason);
            }

            return;
        }

        if (!this.IsFightReviewRunActive(effectiveInCombat))
        {
            if (this.combatHistoryActive || this.combatHistory.HasFrames)
            {
                this.FlushCombatHistory(inactiveReason);
            }

            return;
        }

        if (!this.combatHistoryActive)
        {
            this.combatHistory.Reset(this.IsDutyContextActive() ? "instance-run" : "combat");
            this.combatHistorySaved = false;
            this.combatHistoryActive = true;
        }

        if (!this.combatHistory.ShouldRecord(effectiveInCombat))
        {
            return;
        }

        this.combatHistory.Record(this.GetStatus(), aoePackPositioningController.Status, this.BuildActorSnapshots());
    }

    private bool IsFightReviewRunActive(bool effectiveInCombat)
    {
        return this.IsDutyContextActive() || effectiveInCombat;
    }

    private bool IsDutyContextActive()
    {
        return services.DutyState.IsDutyStarted ||
               services.Condition[ConditionFlag.BoundByDuty];
    }

    private IReadOnlyList<CombatHistoryActorSnapshot> BuildActorSnapshots()
    {
        var player = services.ObjectTable.LocalPlayer;
        if (player == null)
        {
            return [];
        }

        var partyEntityIds = services.PartyList
            .Select(member => member.EntityId)
            .Where(id => id != 0)
            .ToHashSet();
        var targetObjectId = services.TargetManager.Target?.GameObjectId ?? 0;
        var playerPosition = player.Position;

        return services.ObjectTable
            .OfType<IBattleChara>()
            .Where(actor => actor.IsValid())
            .Select(actor => BuildActorSnapshot(actor, player, partyEntityIds, targetObjectId, playerPosition))
            .Where(snapshot => snapshot != null)
            .Select(snapshot => snapshot!)
            .OrderBy(snapshot => SnapshotRelationPriority(snapshot.Relation))
            .ThenBy(snapshot => snapshot.DistanceToPlayer)
            .ThenBy(snapshot => snapshot.GameObjectId)
            .Take(MaxReviewActorSnapshots)
            .ToArray();
    }

    private static CombatHistoryActorSnapshot? BuildActorSnapshot(IBattleChara actor, IGameObject player, HashSet<uint> partyEntityIds, ulong targetObjectId, Vector3 playerPosition)
    {
        var distance = Vector3.Distance(playerPosition, actor.Position);
        var isPlayer = actor.GameObjectId == player.GameObjectId;
        var isParty = !isPlayer && partyEntityIds.Contains(actor.EntityId);
        var isCurrentTarget = actor.GameObjectId == targetObjectId;
        var isTargetingPlayer = actor.TargetObjectId == player.GameObjectId;
        if (!isPlayer && !isParty && !isCurrentTarget && !isTargetingPlayer && distance > MaxReviewActorDistance)
        {
            return null;
        }

        var relation = isPlayer
            ? "player"
            : isParty
                ? "party"
                : isCurrentTarget
                    ? "target"
                    : isTargetingPlayer
                        ? "targeting-player"
                        : "nearby";

        return new CombatHistoryActorSnapshot(
            relation,
            actor.GameObjectId,
            actor.EntityId,
            actor.BaseId,
            actor.ObjectKind.ToString(),
            actor.SubKind,
            actor.ClassJob.RowId,
            actor.Level,
            actor.Position,
            actor.Rotation,
            actor.HitboxRadius,
            actor.IsTargetable,
            actor.IsDead,
            actor.StatusFlags.HasFlag(StatusFlags.InCombat),
            actor.CurrentHp,
            actor.MaxHp,
            actor.TargetObjectId,
            distance);
    }

    private static int SnapshotRelationPriority(string relation)
    {
        return relation switch
        {
            "player" => 0,
            "party" => 1,
            "target" => 2,
            "targeting-player" => 3,
            _ => 4
        };
    }
}

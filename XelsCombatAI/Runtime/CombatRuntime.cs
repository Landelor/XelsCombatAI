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
    DependencyChecker dependencyChecker,
    BossModPresetController presetController,
    PositionalsController positionalsController,
    BossModReflectionSafety bossModSafety,
    BossModGoalZoneHook aoeGoalHook,
    AoePackPositioningController aoePackPositioningController,
    PassageOfArmsPositioningController passageOfArmsPositioningController,
    HealerAoePositioningController healerAoePositioningController,
    SurvivabilityZonePositioningController survivabilityZonePositioningController,
    AggroSafetyController aggroSafetyController,
    ArenaEdgePositioningController arenaEdgePositioningController,
    RedMageMeleeComboController redMageMeleeComboController,
    CombatLogWriter combatLogWriter,
    ManualMovementInputDetector manualMovement,
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
    private const int MaxReviewActorSnapshots = 48;
    private const float MaxReviewActorDistance = 60f;

    private bool wasDead;
    private bool wasInCombat;
    private DateTime nextRuntimeUpdate = DateTime.MinValue;
    private DateTime manualMovementSuppressUntil = DateTime.MinValue;
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

            this.ResetRuntimeCache();
        }
    }

    private void OnFrameworkUpdateCore(IFramework framework)
    {
        _ = framework;
        jobRangeProvider.Tick();

        if (!config.Enabled)
        {
            this.HandleDisabled();
            return;
        }

        if (!dependencyChecker.DependenciesAvailable(out var missing))
        {
            this.UpdateFightReviewLogging(CombatEngagementDetector.IsEffectivelyInCombat(services), "dependencies unavailable");
            this.WaitForDependencies(missing);
            return;
        }

        this.ClearDependencyWaitState();

        var combatEngagement = CombatEngagementDetector.Detect(services);
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

        var now = DateTime.UtcNow;
        if (now < this.nextRuntimeUpdate)
        {
            return;
        }
        this.nextRuntimeUpdate = now.AddMilliseconds(250);
        redMageMeleeComboController.Tick();

        if (!presetController.InitializedPreset && !presetController.Initialize())
        {
            this.UpdateFightReviewLogging(combatEngagement.EffectiveInCombat, "preset unavailable");
            return;
        }

        var suppressAutomatedMovement = this.ShouldSuppressAutomatedMovement(now);
        presetController.ApplyStrategies(suppressAutomatedMovement);
        facingController.Update(now, suppressAutomatedMovement, aoeGoalHook.MovementDiagnostics);
        this.UpdateFightReviewLogging(combatEngagement.EffectiveInCombat, "logging disabled");
    }

    public bool SetEnabled(bool enabled, bool warn = true)
    {
        if (enabled && !dependencyChecker.DependenciesAvailable(out var missing, forceRefresh: true))
        {
            config.Enabled = true;
            this.ResetRuntimeCache();
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
            presetController.Deactivate();
            this.ResetRuntimeCache();
        }

        saveConfig();
        print(config.Enabled ? "Enabled." : "Disabled.");
        return true;
    }

    public void ResetRuntimeCache()
    {
        this.manualMovementSuppressUntil = DateTime.MinValue;
        presetController.ResetCache();
        aoePackPositioningController.Reset();
        passageOfArmsPositioningController.Reset();
        healerAoePositioningController.Reset();
        survivabilityZonePositioningController.Reset();
        aggroSafetyController.Reset();
        redMageMeleeComboController.Reset();
        aoeGoalHook.Reset();
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
            this.GetDependencyWarning(),
            this.GetTrueNorthWarning(),
            positionalsController.RsrTrueNorthDisabled,
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
            aoeGoalHook.PlannerDiagnostics,
            aoePackPositioningController.Status,
            passageOfArmsPositioningController.Status,
            healerAoePositioningController.Status, // HealerCoveragePositioning
            survivabilityZonePositioningController.Status,
            aggroSafetyController.Status,
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
            presetController.Deactivate();
        }

        aoeGoalHook.Dispose();
        survivabilityZonePositioningController.Dispose();
        redMageMeleeComboController.Dispose();
    }

    private void HandleOutOfCombat()
    {
        if (presetController.InitializedPreset)
        {
            presetController.Deactivate();
        }

        this.ResetRuntimeCache();
    }

    private void WaitForDependencies(string missing)
    {
        if (presetController.InitializedPreset)
        {
            presetController.Deactivate();
        }

        this.ResetRuntimeCache();
        if (!string.Equals(this.lastMissingDependencies, missing, StringComparison.Ordinal))
        {
            this.lastMissingDependencies = missing;
            services.Log.Verbose($"XCAI waiting for dependencies: {missing}.");
            updateDtr();
        }
    }

    private void ClearDependencyWaitState()
    {
        if (this.lastMissingDependencies == null)
        {
            return;
        }

        this.lastMissingDependencies = null;
        updateDtr();
    }

    private bool ShouldSuppressAutomatedMovement(DateTime now)
    {
        if (!config.RespectManualMovement)
        {
            this.manualMovementSuppressUntil = DateTime.MinValue;
            return false;
        }

        if (manualMovement.IsManualMovementRequested())
        {
            this.manualMovementSuppressUntil = now.Add(ManualMovementResumeDelay);
        }

        return now < this.manualMovementSuppressUntil;
    }

    private void HandleDisabled()
    {
        if (!this.wasInCombat && !this.wasDead && !presetController.InitializedPreset)
        {
            return;
        }

        if (this.wasInCombat || this.combatHistoryActive || this.combatHistory.HasFrames)
        {
            this.FlushCombatHistory("disabled");
        }

        if (presetController.InitializedPreset)
        {
            presetController.Deactivate();
        }

        this.wasInCombat = false;
        this.wasDead = false;
        this.ResetRuntimeCache();
    }

    private void HandleDeath()
    {
        if (presetController.InitializedPreset)
        {
            presetController.Deactivate();
        }

        this.ResetRuntimeCache();
    }

    private bool ShouldUseGoalHook()
    {
        return config.ManageAoePackPositioning ||
               config.KeepTrashTargetSelected ||
               config.ManageTargetUptime ||
               config.ManageAggroSafetyMovement ||
               config.AvoidStandingInsideEnemies ||
               config.ManageHealerCoverageZone ||
               config.ManageDefensiveGroundZonePositioning ||
               config.ManagePassageOfArmsPositioning ||
               config.AvoidArenaEdge ||
               config.ManageSocialTurning ||
               config.UseRedMageMeleeComboMovement;
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

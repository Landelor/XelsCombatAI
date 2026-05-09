using System;
using Dalamud.Game.ClientState.Conditions;
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
    BossFrontalConeController bossFrontalConeController,
    ManualMovementInputDetector manualMovement,
    GapCloserController gapCloserController,
    EscapeGapCloserController escapeGapCloserController,
    JobRangeProvider jobRangeProvider,
    Action saveConfig,
    Action updateDtr,
    Action<string> print)
{
    private static readonly TimeSpan ManualMovementResumeDelay = TimeSpan.FromMilliseconds(750);

    private bool wasDead;
    private bool wasInCombat;
    private DateTime nextRuntimeUpdate = DateTime.MinValue;
    private DateTime manualMovementSuppressUntil = DateTime.MinValue;
    private string? lastMissingDependencies;
    private readonly CombatHistory combatHistory = new();

    public bool AutomatedMovementSuppressed => DateTime.UtcNow < this.manualMovementSuppressUntil;

    public void OnFrameworkUpdate(IFramework framework)
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
            this.WaitForDependencies(missing);
            return;
        }

        this.ClearDependencyWaitState();

        if (!services.Condition[ConditionFlag.InCombat])
        {
            if (this.wasInCombat || presetController.InitializedPreset)
            {
                this.HandleOutOfCombat();
            }

            this.wasInCombat = false;
            this.wasDead = false;
            this.manualMovementSuppressUntil = DateTime.MinValue;
            return;
        }

        if (!this.wasInCombat)
        {
            this.combatHistory.Reset();
            this.wasInCombat = true;
        }

        var isDead = services.Condition[ConditionFlag.Unconscious];
        if (isDead)
        {
            if (!this.wasDead)
            {
                this.HandleDeath();
            }

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

        if (!presetController.InitializedPreset && !presetController.Initialize())
        {
            return;
        }

        var suppressAutomatedMovement = this.ShouldSuppressAutomatedMovement(now);
        presetController.ApplyStrategies(suppressAutomatedMovement);
        this.combatHistory.Record(this.GetStatus(), aoePackPositioningController.Status);
    }

    public bool SetEnabled(bool enabled, bool warn = true)
    {
        if (enabled && !dependencyChecker.DependenciesAvailable(out var missing))
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
        bossFrontalConeController.Reset();
        aoeGoalHook.Reset();
    }

    public string GetCombatHistory() => this.combatHistory.Build(config);

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
            services.Condition[ConditionFlag.InCombat],
            services.Condition[ConditionFlag.Unconscious],
            player?.ClassJob.RowId ?? 0,
            jobRangeProvider.PackAoeRange,
            jobRangeProvider.EngagementRange,
            target != null,
            target?.BaseId ?? 0,
            target?.GameObjectId ?? 0,
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
            config.GapCloserNIN,
            config.GapCloserSAM,
            config.GapCloserDNC,
            config.GapCloserRPR,
            config.GapCloserVPR,
            config.GapCloserWHM,
            config.EscapeGapCloserMNK,
            config.EscapeGapCloserNIN,
            config.EscapeGapCloserDNC,
            config.EscapeGapCloserRPR,
            config.EscapeGapCloserVPR,
            config.EscapeGapCloserWHM,
            config.EscapeGapCloserBLM,
            config.EscapeGapCloserSGE,
            config.EscapeGapCloserPCT,
            bossModSafety.Status,
            bossModSafety.Diagnostics,
            aoeGoalHook.Status,
            aoeGoalHook.Diagnostics,
            aoePackPositioningController.Status,
            passageOfArmsPositioningController.Status,
            healerAoePositioningController.Status, // HealerCoveragePositioning
            survivabilityZonePositioningController.Status,
            aggroSafetyController.Status,
            manualMovement.Status,
            this.AutomatedMovementSuppressed,
            gapCloserController.LastGapCloserSafety,
            escapeGapCloserController.LastEscapeGapCloserSafety,
            presetController.InitializedPreset,
            bossFrontalConeController.LastReason);
    }

    public void DisposeRuntime()
    {
        if (config.Enabled)
        {
            presetController.Deactivate();
        }

        aoeGoalHook.Dispose();
        survivabilityZonePositioningController.Dispose();
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
               config.AvoidBossFrontalCone ||
               config.AvoidArenaEdge;
    }
}

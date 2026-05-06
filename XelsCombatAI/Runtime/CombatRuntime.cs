using System;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Plugin.Services;

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
    ManualMovementInputDetector manualMovement,
    GapCloserController gapCloserController,
    EscapeGapCloserController escapeGapCloserController,
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
        if (!config.Enabled)
        {
            return;
        }

        if (config.ManageAoePackPositioning)
        {
            aoeGoalHook.EnsureActive();
        }

        if (!dependencyChecker.DependenciesAvailable(out var missing))
        {
            this.WaitForDependencies(missing);
            return;
        }

        this.ClearDependencyWaitState();

        if (!services.Condition[ConditionFlag.InCombat])
        {
            this.wasInCombat = false;
            this.HandleOutOfCombat();
            return;
        }

        if (!this.wasInCombat)
        {
            this.combatHistory.Reset();
            this.wasInCombat = true;
        }

        var isDead = services.Condition[ConditionFlag.Unconscious];
        if (this.wasDead && !isDead)
            this.ResetRuntimeCache();
        this.wasDead = isDead;

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
            target != null,
            target?.BaseId ?? 0,
            services.PartyList.Count,
            this.GetDependencyWarning(),
            this.GetTrueNorthWarning(),
            positionalsController.RsrTrueNorthDisabled,
            presetController.LastPositional,
            positionalsController.GetTrueNorthCharges(),
            positionalsController.HasActiveTrueNorth(),
            presetController.LastRange,
            presetController.LastMovement,
            presetController.LastMovementRangeStrategy,
            presetController.LastForbiddenZoneCushion,
            presetController.LastLeylinesBetweenTheLines,
            presetController.LastLeylinesRetrace,
            presetController.LastLeylinesGoal,
            presetController.LastHealerStayNearParty,
            presetController.LastHealerHeal,
            presetController.LastHealerEsuna,
            presetController.LastHealerOutOfCombat,
            presetController.LastHealerRaise,
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
            config.EscapeGapCloserMNK,
            config.EscapeGapCloserNIN,
            config.EscapeGapCloserDNC,
            config.EscapeGapCloserRPR,
            config.EscapeGapCloserVPR,
            config.EscapeGapCloserBLM,
            config.EscapeGapCloserSGE,
            config.EscapeGapCloserPCT,
            config.EscapeGapCloserBLU,
            bossModSafety.Status,
            aoeGoalHook.Status,
            aoePackPositioningController.Status,
            manualMovement.Status,
            this.AutomatedMovementSuppressed,
            gapCloserController.LastGapCloserSafety,
            escapeGapCloserController.LastEscapeGapCloserSafety,
            presetController.InitializedPreset);
    }

    public void DisposeRuntime()
    {
        if (config.Enabled)
        {
            presetController.Deactivate();
        }

        aoeGoalHook.Dispose();
    }

    private void HandleOutOfCombat()
    {
        if (presetController.InitializedPreset)
        {
            presetController.Deactivate();
            this.ResetRuntimeCache();
        }

        this.manualMovementSuppressUntil = DateTime.MinValue;
    }

    private void WaitForDependencies(string missing)
    {
        if (presetController.InitializedPreset)
        {
            presetController.Deactivate();
        }

        presetController.ResetCache();
        this.manualMovementSuppressUntil = DateTime.MinValue;
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
}

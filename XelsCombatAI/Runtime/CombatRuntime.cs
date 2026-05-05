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
    GapCloserController gapCloserController,
    EscapeGapCloserController escapeGapCloserController,
    Action saveConfig,
    Action updateDtr,
    Action<string> print)
{
    private bool wasDead;
    private DateTime nextRuntimeUpdate = DateTime.MinValue;
    private string? lastMissingDependencies;

    public void OnFrameworkUpdate(IFramework framework)
    {
        _ = framework;
        if (!config.Enabled)
        {
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
            this.HandleOutOfCombat();
            return;
        }

        var isDead = services.Condition[ConditionFlag.Unconscious];
        if (this.wasDead && !isDead)
            this.ResetRuntimeCache();
        this.wasDead = isDead;

        if (DateTime.UtcNow < this.nextRuntimeUpdate)
        {
            return;
        }
        this.nextRuntimeUpdate = DateTime.UtcNow.AddMilliseconds(250);

        if (!presetController.InitializedPreset && !presetController.Initialize())
        {
            return;
        }

        presetController.ApplyStrategies();
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
        presetController.MarkUninitialized();
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
        return new RuntimeStatus(
            config.Enabled,
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
            presetController.LastPartyRole,
            presetController.LastLeylinesBetweenTheLines,
            presetController.LastLeylinesRetrace,
            presetController.LastLeylinesGoal,
            presetController.LastMonkThunderclap,
            presetController.LastDragoonWingedGlide,
            presetController.LastNinjaShukuchi,
            presetController.LastViperSlither,
            config.GapCloserPLD,
            config.GapCloserWAR,
            config.GapCloserDRK,
            config.GapCloserGNB,
            config.GapCloserSAM,
            config.GapCloserRPR,
            config.EscapeGapCloserMNK,
            config.EscapeGapCloserNIN,
            config.EscapeGapCloserRPR,
            config.EscapeGapCloserVPR,
            config.EscapeGapCloserBLM,
            config.EscapeGapCloserSGE,
            config.EscapeGapCloserPCT,
            config.EscapeGapCloserBLU,
            bossModSafety.Status,
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
    }

    private void HandleOutOfCombat()
    {
        if (presetController.InitializedPreset)
        {
            presetController.Deactivate();
            this.ResetRuntimeCache();
        }
    }

    private void WaitForDependencies(string missing)
    {
        if (presetController.InitializedPreset)
        {
            presetController.Deactivate();
        }

        presetController.ResetCache();
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
}

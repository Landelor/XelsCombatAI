using System;

namespace XelsCombatAI.Runtime;

internal sealed class BossModPresetController(
    Configuration config,
    DalamudServices services,
    BossModIpc bossMod,
    BossModReflectionSafety bossModSafety,
    TargetUptimePlanner targetUptimePlanner,
    PositionalsController positionalsController,
    GapCloserController gapCloserController,
    EscapeGapCloserController escapeGapCloserController,
    RedMageMeleeComboController redMageMeleeComboController)
{
    public Positional LastPositional { get; private set; } = Positional.Any;
    public float LastTargetUptimeRange { get; private set; } = -1f;
    public bool? LastMovement { get; private set; }
    public string? LastMovementRangeStrategy { get; private set; }
    public string? LastForbiddenZoneCushion { get; private set; }
    public bool? LastLeylinesBetweenTheLines { get; private set; }
    public bool? LastLeylinesRetrace { get; private set; }
    public bool? LastLeylinesGoal { get; private set; }
    public bool InitializedPreset { get; private set; }

    public bool Initialize()
    {
        try
        {
            if (!bossMod.EnsurePreset())
            {
                return false;
            }

            if (!bossMod.SetActive(BossModIpc.DefaultPresetName))
            {
                return false;
            }

            this.InitializedPreset = true;
            return true;
        }
        catch (Exception ex)
        {
            services.Log.Verbose(ex, "Could not initialize BossMod preset yet.");
            return false;
        }
    }

    public void Deactivate()
    {
        var presetName = BossModIpc.DefaultPresetName;
        try
        {
            this.WriteNeutralStrategies(presetName);
        }
        catch (Exception ex)
        {
            services.Log.Verbose(ex, "Could not write neutral BossMod strategies during deactivation.");
        }

        try
        {
            if (bossMod.GetActive() == presetName)
            {
                bossMod.ClearActive();
            }
        }
        catch (Exception ex)
        {
            services.Log.Verbose(ex, "Could not clear active BossMod preset.");
        }

        try
        {
            bossMod.ClearTransientPresetStrategies(presetName);
        }
        catch (Exception ex)
        {
            services.Log.Verbose(ex, "Could not clear BossMod transient preset strategies.");
        }
    }

    public void ApplyStrategies(bool suppressAutomatedMovement)
    {
        try
        {
            this.SetTargetUptimeRange(targetUptimePlanner.CalculateTargetUptimeRange(config.ManageTargetUptime));

            this.SetForbiddenZoneCushion(config.ManageForbiddenZoneDistance
                ? MapForbiddenZoneCushion(config.PreferredForbiddenZoneDistance)
                : "None");

            this.SetMovementRangeStrategy(config.ManageMovement
                ? MapCombatStyle(config.CombatStyle)
                : "Any");


            if (config.ManagePositionals)
            {
                positionalsController.Apply();
            }
            else
            {
                this.SetPositional(Positional.Any);
            }

            this.SetMovement(config.ManageMovement && !suppressAutomatedMovement);

            this.SetLeylines(
                config.ManageLeylines && !suppressAutomatedMovement && config.UseBetweenTheLines,
                config.ManageLeylines && !suppressAutomatedMovement && config.UseRetrace,
                config.ManageLeylines && !suppressAutomatedMovement && config.ReturnToLeylines);

            this.SetGapClosers(suppressAutomatedMovement);
        }
        catch (Exception ex)
        {
            services.Log.Verbose(ex, "Could not update BossMod strategies yet.");
            this.InitializedPreset = false;
        }
    }

    public void ResetCache()
    {
        this.InitializedPreset = false;
        this.LastPositional = Positional.Any;
        this.LastTargetUptimeRange = -1f;
        this.LastMovement = null;
        this.LastMovementRangeStrategy = null;
        this.LastForbiddenZoneCushion = null;
        this.LastLeylinesBetweenTheLines = null;
        this.LastLeylinesRetrace = null;
        this.LastLeylinesGoal = null;
        positionalsController.Reset();
        gapCloserController.Reset();
        escapeGapCloserController.Reset();
        redMageMeleeComboController.Reset();
        bossModSafety.Reset();
    }

    public void MarkUninitialized()
    {
        this.InitializedPreset = false;
    }

    public void SetPositional(Positional positional)
    {
        if (positional == this.LastPositional)
        {
            return;
        }

        if (bossMod.SetPositional(BossModIpc.DefaultPresetName, positional))
        {
            this.LastPositional = positional;
        }
    }

    private void SetTargetUptimeRange(float range)
    {
        if (Math.Abs(this.LastTargetUptimeRange - range) <= 0.01f)
        {
            return;
        }

        if (bossMod.SetRange(BossModIpc.DefaultPresetName, range))
        {
            this.LastTargetUptimeRange = range;
        }
    }

    private void SetMovement(bool enabled)
    {
        if (this.LastMovement == enabled)
        {
            return;
        }

        if (bossMod.SetMovement(BossModIpc.DefaultPresetName, enabled))
        {
            this.LastMovement = enabled;
        }
    }

    private void SetForbiddenZoneCushion(string cushion)
    {
        if (this.LastForbiddenZoneCushion == cushion)
        {
            return;
        }

        if (bossMod.SetForbiddenZoneCushion(BossModIpc.DefaultPresetName, cushion))
        {
            this.LastForbiddenZoneCushion = cushion;
        }
    }

    private void SetMovementRangeStrategy(string strategy)
    {
        if (this.LastMovementRangeStrategy == strategy)
        {
            return;
        }

        if (bossMod.SetMovementRangeStrategy(BossModIpc.DefaultPresetName, strategy))
        {
            this.LastMovementRangeStrategy = strategy;
        }
    }

    private static string MapCombatStyle(CombatStyle style)
    {
        return style switch
        {
            CombatStyle.Greed => "GreedAutomatic",
            CombatStyle.GreedGCD => "GreedGCDExplicit",
            CombatStyle.GreedLastMoment => "GreedLastMomentExplicit",
            _ => "Any"
        };
    }

    private static string MapForbiddenZoneCushion(float distance)
    {
        return distance switch
        {
            <= 0.25f => "None",
            < 1.0f => "Small",
            <= 2.25f => "Medium",
            _ => "Large"
        };
    }


    private void SetLeylines(bool useBetweenTheLines, bool useRetrace, bool returnToLeylines)
    {
        if (this.LastLeylinesBetweenTheLines != useBetweenTheLines &&
            bossMod.SetLeylinesBetweenTheLines(BossModIpc.DefaultPresetName, useBetweenTheLines))
        {
            this.LastLeylinesBetweenTheLines = useBetweenTheLines;
        }

        if (this.LastLeylinesRetrace != useRetrace &&
            bossMod.SetLeylinesRetrace(BossModIpc.DefaultPresetName, useRetrace))
        {
            this.LastLeylinesRetrace = useRetrace;
        }

        if (this.LastLeylinesGoal != returnToLeylines &&
            bossMod.SetLeylinesGoal(BossModIpc.DefaultPresetName, returnToLeylines))
        {
            this.LastLeylinesGoal = returnToLeylines;
        }
    }

    private void SetGapClosers(bool suppressAutomatedMovement)
    {
        if (suppressAutomatedMovement)
        {
            return;
        }

        if (!config.UseGapCloser)
        {
            if (redMageMeleeComboController.TryUseComboJump())
            {
                return;
            }

            return;
        }

        if (escapeGapCloserController.TryUseEscapeGapCloser())
        {
            return;
        }

        if (redMageMeleeComboController.TryUseComboJump())
        {
            return;
        }

        gapCloserController.TryUseReengageGapCloser();
    }

    private void WriteNeutralStrategies(string presetName)
    {
        bossMod.SetMovement(presetName, false);
        bossMod.SetMovementRangeStrategy(presetName, "Any");
        bossMod.SetRange(presetName, Configuration.InternalDisabledUptimeRange);
        bossMod.SetForbiddenZoneCushion(presetName, "None");
        bossMod.SetPositional(presetName, Positional.Any);
        bossMod.SetLeylinesBetweenTheLines(presetName, false);
        bossMod.SetLeylinesRetrace(presetName, false);
        bossMod.SetLeylinesGoal(presetName, false);
    }
}

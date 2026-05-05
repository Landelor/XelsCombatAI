using System;

namespace XelsCombatAI.Runtime;

internal sealed class BossModPresetController(
    Configuration config,
    DalamudServices services,
    BossModIpc bossMod,
    BossModReflectionSafety bossModSafety,
    RangePlanner rangePlanner,
    PositionalsController positionalsController,
    GapCloserController gapCloserController,
    EscapeGapCloserController escapeGapCloserController)
{
    public Positional LastPositional { get; private set; } = Positional.Any;
    public float LastRange { get; private set; } = -1f;
    public bool? LastMovement { get; private set; }
    public string? LastMovementRangeStrategy { get; private set; }
    public string? LastForbiddenZoneCushion { get; private set; }
    public string? LastPartyRole { get; private set; }
    public bool? LastLeylinesBetweenTheLines { get; private set; }
    public bool? LastLeylinesRetrace { get; private set; }
    public bool? LastLeylinesGoal { get; private set; }
    public bool? LastMonkThunderclap { get; private set; }
    public bool? LastDragoonWingedGlide { get; private set; }
    public bool? LastNinjaShukuchi { get; private set; }
    public bool? LastViperSlither { get; private set; }
    public bool? LastHealerStayNearParty { get; private set; }
    public bool? LastHealerHeal { get; private set; }
    public bool? LastHealerEsuna { get; private set; }
    public bool? LastHealerOutOfCombat { get; private set; }
    public string? LastHealerRaise { get; private set; }
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
        try
        {
            var presetName = BossModIpc.DefaultPresetName;

            bossMod.SetMovement(presetName, false);
            bossMod.SetMovementRangeStrategy(presetName, "Any");
            bossMod.SetPositional(presetName, Positional.Any);
            bossMod.SetPartyRole(presetName, "None");
            bossMod.SetLeylinesBetweenTheLines(presetName, false);
            bossMod.SetLeylinesRetrace(presetName, false);
            bossMod.SetLeylinesGoal(presetName, false);
            bossMod.SetMonkThunderclap(presetName, false);
            bossMod.SetDragoonWingedGlide(presetName, false);
            bossMod.SetNinjaShukuchi(presetName, false);
            bossMod.SetViperSlither(presetName, false);
            bossMod.SetHealerStayNearParty(presetName, false);
            bossMod.SetHealerHeal(presetName, false);
            bossMod.SetHealerEsuna(presetName, false);
            bossMod.SetHealerOutOfCombat(presetName, false);
            bossMod.SetHealerRaise(presetName, "None");

            if (bossMod.GetActive() == presetName)
            {
                bossMod.ClearActive();
            }
        }
        catch (Exception ex)
        {
            services.Log.Verbose(ex, "Could not deactivate BossMod preset.");
        }
    }

    public void ApplyStrategies()
    {
        try
        {
            if (config.ManageRange)
            {
                if (config.HealerPartyCoverage &&
                    rangePlanner.GetCurrentRangeRole() == RangeRole.Healer &&
                    rangePlanner.CurrentTargetHasBossModule())
                    this.SetRange(rangePlanner.CalculateHealerCoverageRange());
                else
                    this.SetRange(rangePlanner.CalculateDesiredRange());
            }

            if (config.ManageForbiddenZoneDistance)
            {
                this.SetForbiddenZoneCushion(MapForbiddenZoneCushion(config.PreferredForbiddenZoneDistance));
            }

            this.SetMovementRangeStrategy(MapCombatStyle(config.CombatStyle));

            if (config.ManagePartyRoleFollow)
            {
                this.SetPartyRole(rangePlanner.CurrentTargetHasBossModule() ? "None" : "Tank");
            }

            positionalsController.Apply();

            if (config.ManageMovement)
            {
                this.SetMovement(true);
            }

            this.SetLeylines(
                config.ManageLeylines && config.UseBetweenTheLines,
                config.ManageLeylines && config.UseRetrace,
                config.ManageLeylines && config.ReturnToLeylines);

            this.SetHealerAi();
            this.SetGapClosers();
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
        this.LastRange = -1f;
        this.LastMovement = null;
        this.LastMovementRangeStrategy = null;
        this.LastForbiddenZoneCushion = null;
        this.LastPartyRole = null;
        this.LastLeylinesBetweenTheLines = null;
        this.LastLeylinesRetrace = null;
        this.LastLeylinesGoal = null;
        this.LastMonkThunderclap = null;
        this.LastDragoonWingedGlide = null;
        this.LastNinjaShukuchi = null;
        this.LastViperSlither = null;
        this.LastHealerStayNearParty = null;
        this.LastHealerHeal = null;
        this.LastHealerEsuna = null;
        this.LastHealerOutOfCombat = null;
        this.LastHealerRaise = null;
        positionalsController.Reset();
        gapCloserController.Reset();
        escapeGapCloserController.Reset();
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

    private void SetRange(float range)
    {
        if (Math.Abs(this.LastRange - range) <= 0.01f)
        {
            return;
        }

        if (bossMod.SetRange(BossModIpc.DefaultPresetName, range))
        {
            this.LastRange = range;
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

    private void SetPartyRole(string role)
    {
        if (this.LastPartyRole == role)
        {
            return;
        }

        if (bossMod.SetPartyRole(BossModIpc.DefaultPresetName, role))
        {
            this.LastPartyRole = role;
        }
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

    private void SetHealerAi()
    {
        var presetName = BossModIpc.DefaultPresetName;
        var stayNearParty = config.HealerPartyCoverage &&
                            rangePlanner.GetCurrentRangeRole() == RangeRole.Healer &&
                            rangePlanner.CurrentTargetHasBossModule();

        if (this.LastHealerRaise != "None" && bossMod.SetHealerRaise(presetName, "None"))
        {
            this.LastHealerRaise = "None";
        }

        if (this.LastHealerHeal != false && bossMod.SetHealerHeal(presetName, false))
        {
            this.LastHealerHeal = false;
        }

        if (this.LastHealerEsuna != false && bossMod.SetHealerEsuna(presetName, false))
        {
            this.LastHealerEsuna = false;
        }

        if (this.LastHealerOutOfCombat != false && bossMod.SetHealerOutOfCombat(presetName, false))
        {
            this.LastHealerOutOfCombat = false;
        }

        if (this.LastHealerStayNearParty != stayNearParty &&
            bossMod.SetHealerStayNearParty(presetName, stayNearParty))
        {
            this.LastHealerStayNearParty = stayNearParty;
        }
    }

    private void SetGapClosers()
    {
        var presetName = BossModIpc.DefaultPresetName;

        this.SetGapCloser(nameof(this.LastMonkThunderclap), this.LastMonkThunderclap, false, value => bossMod.SetMonkThunderclap(presetName, value));
        this.SetGapCloser(nameof(this.LastDragoonWingedGlide), this.LastDragoonWingedGlide, false, value => bossMod.SetDragoonWingedGlide(presetName, value));
        this.SetGapCloser(nameof(this.LastNinjaShukuchi), this.LastNinjaShukuchi, false, value => bossMod.SetNinjaShukuchi(presetName, value));
        this.SetGapCloser(nameof(this.LastViperSlither), this.LastViperSlither, false, value => bossMod.SetViperSlither(presetName, value));

        if (config.UseEscapeGapCloser && escapeGapCloserController.TryUseEscapeGapCloser())
        {
            return;
        }

        if (config.UseGapCloser)
        {
            gapCloserController.TryUseReengageGapCloser();
        }
    }

    private void SetGapCloser(string cacheName, bool? last, bool enabled, Func<bool, bool> setter)
    {
        if (last == enabled)
        {
            return;
        }

        if (!setter(enabled))
        {
            return;
        }

        switch (cacheName)
        {
            case nameof(this.LastMonkThunderclap):
                this.LastMonkThunderclap = enabled;
                break;
            case nameof(this.LastDragoonWingedGlide):
                this.LastDragoonWingedGlide = enabled;
                break;
            case nameof(this.LastNinjaShukuchi):
                this.LastNinjaShukuchi = enabled;
                break;
            case nameof(this.LastViperSlither):
                this.LastViperSlither = enabled;
                break;
        }
    }
}

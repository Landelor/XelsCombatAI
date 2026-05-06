using System;
using System.Collections.Generic;
using System.Numerics;
using Dalamud.Game.ClientState.Keys;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Windowing;
using Dalamud.Plugin.Services;

namespace XelsCombatAI.UI;

internal sealed class ConfigWindow : Window, IDisposable
{
    private const float TooltipWrapWidth = 360f;

    private readonly Configuration config;
    private readonly Configuration defaultConfig = new();
    private readonly Action save;
    private readonly Action resetRuntimeState;
    private readonly Action<bool> setEnabled;
    private readonly Func<string> debugState;
    private readonly Func<string> combatHistory;
    private readonly Func<string?> dependencyWarning;
    private readonly Func<string?> trueNorthWarning;
    private readonly Action manageTrueNorthEnabled;
    private readonly IKeyState keyState;
    private readonly HashSet<string> editingSliders = [];
    private DateTime copiedDebugStateUntil = DateTime.MinValue;
    private DateTime copiedHistoryUntil = DateTime.MinValue;
    private bool backspacePressedThisFrame;
    private bool wasBackspaceDown;

    public ConfigWindow(Configuration config, Action save, Action resetRuntimeState, Action<bool> setEnabled, Func<string> debugState, Func<string> combatHistory, Func<string?> dependencyWarning, Func<string?> trueNorthWarning, Action manageTrueNorthEnabled, IKeyState keyState)
        : base("Xel's Combat AI Configuration###XelsCombatAIConfig")
    {
        this.config = config;
        this.save = save;
        this.resetRuntimeState = resetRuntimeState;
        this.setEnabled = setEnabled;
        this.debugState = debugState;
        this.combatHistory = combatHistory;
        this.dependencyWarning = dependencyWarning;
        this.trueNorthWarning = trueNorthWarning;
        this.manageTrueNorthEnabled = manageTrueNorthEnabled;
        this.keyState = keyState;
        this.Flags = ImGuiWindowFlags.AlwaysAutoResize;
        this.SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new(480, 0),
            MaximumSize = new(480, float.MaxValue)
        };
    }

    public void Dispose()
    {
    }

    public override void Draw()
    {
        var changed = false;
        var dependencyWarningText = this.dependencyWarning();
        var trueNorthWarningText = this.trueNorthWarning();
        var backspaceDown = this.keyState[VirtualKey.BACK];
        this.backspacePressedThisFrame = backspaceDown && !this.wasBackspaceDown;
        this.wasBackspaceDown = backspaceDown;

        if (dependencyWarningText != null)
        {
            ImGui.TextColored(0xff4040ff, $"Waiting for: {dependencyWarningText}");
            ImGui.Separator();
        }

        if (trueNorthWarningText != null)
        {
            ImGui.TextColored(0xff40a0ff, $"Warning: {trueNorthWarningText}");
            ImGui.Separator();
        }

        if (ImGui.BeginTabBar("##xcai_tabs"))
        {
            if (ImGui.BeginTabItem("Main"))
            {
                ImGui.Spacing();
                changed |= this.DrawMainTab();
                ImGui.EndTabItem();
            }

            if (ImGui.BeginTabItem("Positioning"))
            {
                ImGui.Spacing();
                changed |= this.DrawPositioningTab();
                ImGui.EndTabItem();
            }

            if (ImGui.BeginTabItem("Distance"))
            {
                ImGui.Spacing();
                changed |= this.DrawDistanceTab();
                ImGui.EndTabItem();
            }

            if (ImGui.BeginTabItem("Gap Closers"))
            {
                ImGui.Spacing();
                changed |= this.DrawGapClosersTab();
                ImGui.EndTabItem();
            }

            if (ImGui.BeginTabItem("Chat & Reset"))
            {
                ImGui.Spacing();
                changed |= this.DrawChatAndResetTab();
                ImGui.EndTabItem();
            }

            ImGui.EndTabBar();
        }

        if (changed)
        {
            this.config.Clamp();
            this.resetRuntimeState();
            this.save();
        }
    }

    private bool DrawMainTab()
    {
        var changed = false;

        this.DrawEnabledCheckbox();
        ImGui.Spacing();

        this.DrawSectionHeader("Movement");
        changed |= this.Checkbox("Manage movement in combat", this.config.ManageMovement, this.defaultConfig.ManageMovement, v => this.config.ManageMovement = v, "Enables or disables automated movement.\nAll other settings remain active and will apply when re-enabled.");
        changed |= this.Checkbox("Pause movement while moving manually", this.config.RespectManualMovement, this.defaultConfig.RespectManualMovement, v => this.config.RespectManualMovement = v, "Temporarily pauses automated movement when manual movement input is detected.\nResumes automatically shortly after input stops.");
        changed |= this.Combo("Combat style", this.config.CombatStyle, this.defaultConfig.CombatStyle, v => this.config.CombatStyle = v, "Normal — prioritizes getting to safety immediately.\n\nGreed — tries to maintain uptime while still respecting mechanics.\n\nGreed GCD — holds position until the last GCD window before a mechanic requires movement.\n\nGreed Last Moment — holds position until the absolute last moment before a mechanic requires movement.", v => v switch
        {
            CombatStyle.GreedGCD        => "Greed GCD",
            CombatStyle.GreedLastMoment => "Greed Last Moment",
            _                           => v.ToString()
        });
        changed |= this.Checkbox("Healer: stay near party", this.config.HealerPartyCoverage, this.defaultConfig.HealerPartyCoverage, v => this.config.HealerPartyCoverage = v, "Positions the healer to stay within range of as many party members as possible.\nOverrides the healer max distance sliders.");
        ImGui.Unindent(8f);
        ImGui.Spacing();

        return changed;
    }

    private bool DrawPositioningTab()
    {
        var changed = false;

        changed |= this.DrawToggleSectionHeader("Positionals", this.config.ManagePositionals, this.defaultConfig.ManagePositionals, v => this.config.ManagePositionals = v, "Requests rear or flank positioning based on Avarice positional data.\nOnly active when the current job and target support positionals.");
        var positionalsDisabledTooltip = !this.config.ManagePositionals ? "Disabled by Positionals in the Positioning tab." : null;
        if (!this.config.ManagePositionals)
            ImGui.BeginDisabled();
        changed |= this.Checkbox(
            "Manage True North",
            this.config.ManageTrueNorth,
            this.defaultConfig.ManageTrueNorth,
            v => { this.config.ManageTrueNorth = v; if (v) this.manageTrueNorthEnabled(); },
            "Manages True North here instead of RotationSolver Reborn.\nDisables RSR's Auto True North while active.\nRSR will not restore it automatically when disabled.",
            positionalsDisabledTooltip);
        if (!this.config.ManagePositionals)
            ImGui.EndDisabled();
        ImGui.Unindent(8f);
        ImGui.Spacing();

        changed |= this.DrawToggleSectionHeader("Ley Lines", this.config.ManageLeylines, this.defaultConfig.ManageLeylines, v => this.config.ManageLeylines = v, "Manages positioning relative to existing Ley Lines.\nDoes not place new Ley Lines.");
        var leylinesDisabledTooltip = !this.config.ManageLeylines ? "Disabled by Ley Lines in the Positioning tab." : null;
        if (!this.config.ManageLeylines)
            ImGui.BeginDisabled();
        changed |= this.Checkbox("Use Between the Lines", this.config.UseBetweenTheLines, this.defaultConfig.UseBetweenTheLines, v => this.config.UseBetweenTheLines = v, disabledTooltip: leylinesDisabledTooltip);
        changed |= this.Checkbox("Use Retrace", this.config.UseRetrace, this.defaultConfig.UseRetrace, v => this.config.UseRetrace = v, disabledTooltip: leylinesDisabledTooltip);
        changed |= this.Checkbox("Walk back to Ley Lines", this.config.ReturnToLeylines, this.defaultConfig.ReturnToLeylines, v => this.config.ReturnToLeylines = v, disabledTooltip: leylinesDisabledTooltip);
        if (!this.config.ManageLeylines)
            ImGui.EndDisabled();
        ImGui.Unindent(8f);
        ImGui.Spacing();

        this.DrawSectionHeader("Decision Overlay");
        changed |= this.Checkbox(
            "Show decision overlay",
            this.config.ShowDecisionOverlay,
            this.defaultConfig.ShowDecisionOverlay,
            v => this.config.ShowDecisionOverlay = v,
            "Draws current movement decisions and candidates in the world overlay.\nUses projected screen-space lines and markers.");
        ImGui.Unindent(8f);
        ImGui.Spacing();

        changed |= this.DrawToggleSectionHeader(
            "AoE Pack Positioning",
            this.config.ManageAoePackPositioning,
            this.defaultConfig.ManageAoePackPositioning,
            v => this.config.ManageAoePackPositioning = v,
            "Repositions to hit more enemies with AoE attacks using RSR's next GCD preview.\nOnly moves when 2+ in-combat targets exist and a better position is available.",
            FontAwesomeIcon.LayerGroup,
            null,
            !this.config.ManageMovement ? "Disabled by Manage movement in combat on the Main tab." : null);
        var aoePackDisabledTooltip = !this.config.ManageAoePackPositioning
            ? "Disabled by AoE Pack Positioning in the Positioning tab."
            : !this.config.ManageMovement
                ? "Disabled by Manage movement in combat on the Main tab."
                : null;
        if (!this.config.ManageAoePackPositioning || !this.config.ManageMovement)
            ImGui.BeginDisabled();
        changed |= this.SliderInt(
            "Minimum extra AoE targets",
            this.config.AoePackPositioningMinimumExtraTargets,
            this.defaultConfig.AoePackPositioningMinimumExtraTargets,
            1,
            5,
            v => this.config.AoePackPositioningMinimumExtraTargets = v,
            aoePackDisabledTooltip);
        changed |= this.Checkbox(
            "Control RSR targeting during repositioning",
            this.config.AoePackPositioningControlRsrTarget,
            this.defaultConfig.AoePackPositioningControlRsrTarget,
            v => this.config.AoePackPositioningControlRsrTarget = v,
            "Switches RSR to Henched mode and sets the best AoE primary target during repositioning.\nRestores RSR's previous state when no improvement is found.",
            aoePackDisabledTooltip);
        changed |= this.Checkbox(
            "AoE Combat Control",
            this.config.AoePackPositioningAoeCombatControl,
            this.defaultConfig.AoePackPositioningAoeCombatControl,
            v => this.config.AoePackPositioningAoeCombatControl = v,
            "Takes over RSR targeting on trash pulls as soon as the enemy threshold is met.\nPulls toward the pack centroid when out of AoE range.",
            aoePackDisabledTooltip);
        if (!this.config.ManageAoePackPositioning || !this.config.ManageMovement)
            ImGui.EndDisabled();
        ImGui.Unindent(8f);
        ImGui.Spacing();

        return changed;
    }

    private bool DrawDistanceTab()
    {
        var changed = false;

        changed |= this.DrawToggleSectionHeader("Range Management", this.config.ManageRange, this.defaultConfig.ManageRange, v => this.config.ManageRange = v, "Automatically adjusts positioning distance based on your role, target count, and the settings below.");
        var manageRangeDisabledTooltip = "Disabled by Range Management in the Distance tab.";
        ImGui.Unindent(8f);
        ImGui.Spacing();

        changed |= this.DrawToggleSectionHeader("Single Target Distance", this.config.RoleBasedRange, this.defaultConfig.RoleBasedRange, v => this.config.RoleBasedRange = v, "Applies these distances when fighting a single enemy.\nAlso used in multi-target situations until the AoE threshold is reached.", disabledTooltip: !this.config.ManageRange ? manageRangeDisabledTooltip : null);
        if (!this.config.ManageRange)
            ImGui.BeginDisabled();
        var singleTargetDisabledTooltip = !this.config.ManageRange ? manageRangeDisabledTooltip : !this.config.RoleBasedRange ? "Disabled by Single target distance in the Distance tab." : null;
        var aoeDisabledTooltip = !this.config.ManageRange ? manageRangeDisabledTooltip : !this.config.AoERangeInMultiTarget ? "Disabled by AoE target distance in the Distance tab." : null;
        var healerCoverageDisabledTooltip = "Disabled by Healer: stay near party in Main.";
        var singleTargetHealerDisabledTooltip = !this.config.ManageRange ? manageRangeDisabledTooltip : this.config.HealerPartyCoverage ? healerCoverageDisabledTooltip : singleTargetDisabledTooltip;
        var aoeHealerDisabledTooltip = !this.config.ManageRange ? manageRangeDisabledTooltip : this.config.HealerPartyCoverage ? healerCoverageDisabledTooltip : aoeDisabledTooltip;

        if (!this.config.RoleBasedRange)
            ImGui.BeginDisabled();
        changed |= this.SliderFloat("Melee max distance", this.config.MeleeRange, this.defaultConfig.MeleeRange, Configuration.BossModMinRange, Configuration.BossModMaxRange, v => this.config.MeleeRange = v, disabledTooltip: singleTargetDisabledTooltip);
        changed |= this.SliderFloat("Physical ranged max distance", this.config.PhysicalRangedRange, this.defaultConfig.PhysicalRangedRange, Configuration.BossModMinRange, Configuration.BossModMaxRange, v => this.config.PhysicalRangedRange = v, disabledTooltip: singleTargetDisabledTooltip);
        if (this.config.HealerPartyCoverage) ImGui.BeginDisabled();
        changed |= this.SliderFloat("Healer max distance", this.config.HealerRange, this.defaultConfig.HealerRange, Configuration.BossModMinRange, Configuration.BossModMaxRange, v => this.config.HealerRange = v, disabledTooltip: singleTargetHealerDisabledTooltip);
        if (this.config.HealerPartyCoverage) ImGui.EndDisabled();
        changed |= this.SliderFloat("Magic ranged max distance", this.config.MagicRangedRange, this.defaultConfig.MagicRangedRange, Configuration.BossModMinRange, Configuration.BossModMaxRange, v => this.config.MagicRangedRange = v, disabledTooltip: singleTargetDisabledTooltip);
        if (!this.config.RoleBasedRange)
            ImGui.EndDisabled();
        ImGui.Unindent(8f);
        ImGui.Spacing();

        if (this.config.AoePackPositioningAoeCombatControl) ImGui.BeginDisabled();
        changed |= this.DrawToggleSectionHeader("AoE Target Distance", this.config.AoERangeInMultiTarget, this.defaultConfig.AoERangeInMultiTarget, v => this.config.AoERangeInMultiTarget = v, "Switches to the AoE distances when enough enemies are nearby.\nThe enemy threshold below controls when this kicks in.\nNot used when AoE Combat Control is enabled.", disabledTooltip: !this.config.ManageRange ? manageRangeDisabledTooltip : this.config.AoePackPositioningAoeCombatControl ? "Disabled by AoE Combat Control in the Positioning tab." : null);
        if (!this.config.AoERangeInMultiTarget)
            ImGui.BeginDisabled();
        changed |= this.Checkbox("Non-AST healers use melee AoE range", this.config.AoEHealerMeleeRange, this.defaultConfig.AoEHealerMeleeRange, v => this.config.AoEHealerMeleeRange = v, disabledTooltip: aoeDisabledTooltip);
        changed |= this.SliderFloat("AoE melee max distance", this.config.AoEMeleeRange, this.defaultConfig.AoEMeleeRange, Configuration.BossModMinRange, Configuration.BossModMaxRange, v => this.config.AoEMeleeRange = v, disabledTooltip: aoeDisabledTooltip);
        changed |= this.SliderFloat("AoE physical ranged max distance", this.config.AoEPhysicalRangedRange, this.defaultConfig.AoEPhysicalRangedRange, Configuration.BossModMinRange, Configuration.BossModMaxRange, v => this.config.AoEPhysicalRangedRange = v, disabledTooltip: aoeDisabledTooltip);
        if (this.config.HealerPartyCoverage) ImGui.BeginDisabled();
        changed |= this.SliderFloat("AoE healer max distance", this.config.AoEHealerRange, this.defaultConfig.AoEHealerRange, Configuration.BossModMinRange, Configuration.BossModMaxRange, v => this.config.AoEHealerRange = v, disabledTooltip: aoeHealerDisabledTooltip);
        if (this.config.HealerPartyCoverage) ImGui.EndDisabled();
        changed |= this.SliderFloat("AoE magic ranged max distance", this.config.AoEMagicRangedRange, this.defaultConfig.AoEMagicRangedRange, Configuration.BossModMinRange, Configuration.BossModMaxRange, v => this.config.AoEMagicRangedRange = v, disabledTooltip: aoeDisabledTooltip);
        changed |= this.SliderInt("AoE enemy threshold", this.config.AoEEnemyThreshold, this.defaultConfig.AoEEnemyThreshold, 1, 10, v => this.config.AoEEnemyThreshold = v, disabledTooltip: aoeDisabledTooltip);
        if (!this.config.AoERangeInMultiTarget)
            ImGui.EndDisabled();
        if (this.config.AoePackPositioningAoeCombatControl) ImGui.EndDisabled();
        ImGui.Unindent(8f);
        ImGui.Spacing();

        if (!this.config.ManageRange)
            ImGui.EndDisabled();

        changed |= this.DrawToggleSectionHeader("Forbidden Zone", this.config.ManageForbiddenZoneDistance, this.defaultConfig.ManageForbiddenZoneDistance, v => this.config.ManageForbiddenZoneDistance = v, "Maintains extra distance from forbidden zones when possible.\nMechanic-required movement always takes priority.");
        var forbiddenZoneDisabledTooltip = !this.config.ManageForbiddenZoneDistance ? "Disabled by Manage forbidden-zone distance in the Distance tab." : null;
        if (!this.config.ManageForbiddenZoneDistance)
            ImGui.BeginDisabled();
        changed |= this.SliderFloat("Preferred distance to forbidden zones", this.config.PreferredForbiddenZoneDistance, this.defaultConfig.PreferredForbiddenZoneDistance, 0f, 3f, v => this.config.PreferredForbiddenZoneDistance = v, disabledTooltip: forbiddenZoneDisabledTooltip);
        if (!this.config.ManageForbiddenZoneDistance)
            ImGui.EndDisabled();
        ImGui.Unindent(8f);
        ImGui.Spacing();

        return changed;
    }

    private bool DrawGapClosersTab()
    {
        var changed = false;

        changed |= this.DrawToggleSectionHeader(
            "Re-engage",
            this.config.UseGapCloser,
            this.defaultConfig.UseGapCloser,
            v => this.config.UseGapCloser = v,
            "Uses selected gap closers to close distance and return to range.\nOnly fires when the destination is reported safe.",
            FontAwesomeIcon.SkullCrossbones,
            "This can put you in danger during combat.\nSome arena hazards and timing checks may need manual judgment.");
        var reengageDisabledTooltip = !this.config.UseGapCloser ? "Disabled by Use gap closer to (re)engage in Gap Closers." : null;
        if (!this.config.UseGapCloser)
            ImGui.BeginDisabled();
        ImGui.Columns(3, "reengageJobs", false);
        changed |= this.Checkbox("PLD", this.config.GapCloserPLD, this.defaultConfig.GapCloserPLD, v => this.config.GapCloserPLD = v, disabledTooltip: reengageDisabledTooltip);
        ImGui.NextColumn();
        changed |= this.Checkbox("WAR", this.config.GapCloserWAR, this.defaultConfig.GapCloserWAR, v => this.config.GapCloserWAR = v, disabledTooltip: reengageDisabledTooltip);
        ImGui.NextColumn();
        changed |= this.Checkbox("DRK", this.config.GapCloserDRK, this.defaultConfig.GapCloserDRK, v => this.config.GapCloserDRK = v, disabledTooltip: reengageDisabledTooltip);
        ImGui.NextColumn();
        changed |= this.Checkbox("GNB", this.config.GapCloserGNB, this.defaultConfig.GapCloserGNB, v => this.config.GapCloserGNB = v, disabledTooltip: reengageDisabledTooltip);
        ImGui.NextColumn();
        changed |= this.Checkbox("MNK", this.config.GapCloserMNK, this.defaultConfig.GapCloserMNK, v => this.config.GapCloserMNK = v, disabledTooltip: reengageDisabledTooltip);
        ImGui.NextColumn();
        changed |= this.Checkbox("DRG", this.config.GapCloserDRG, this.defaultConfig.GapCloserDRG, v => this.config.GapCloserDRG = v, disabledTooltip: reengageDisabledTooltip);
        ImGui.NextColumn();
        changed |= this.Checkbox("NIN", this.config.GapCloserNIN, this.defaultConfig.GapCloserNIN, v => this.config.GapCloserNIN = v, disabledTooltip: reengageDisabledTooltip);
        ImGui.NextColumn();
        changed |= this.Checkbox("SAM", this.config.GapCloserSAM, this.defaultConfig.GapCloserSAM, v => this.config.GapCloserSAM = v, disabledTooltip: reengageDisabledTooltip);
        ImGui.NextColumn();
        changed |= this.Checkbox("DNC", this.config.GapCloserDNC, this.defaultConfig.GapCloserDNC, v => this.config.GapCloserDNC = v, disabledTooltip: reengageDisabledTooltip);
        ImGui.NextColumn();
        changed |= this.Checkbox("RPR", this.config.GapCloserRPR, this.defaultConfig.GapCloserRPR, v => this.config.GapCloserRPR = v, disabledTooltip: reengageDisabledTooltip);
        ImGui.NextColumn();
        changed |= this.Checkbox("VPR", this.config.GapCloserVPR, this.defaultConfig.GapCloserVPR, v => this.config.GapCloserVPR = v, disabledTooltip: reengageDisabledTooltip);
        ImGui.Columns(1);
        changed |= this.SliderFloat(
            "Minimum (re)engage distance",
            this.config.MinimumReengageGapCloserDistance,
            this.defaultConfig.MinimumReengageGapCloserDistance,
            Configuration.MinimumGapCloserDistanceMin,
            Configuration.MinimumGapCloserDistanceMax,
            v => this.config.MinimumReengageGapCloserDistance = v,
            "%.0f",
            reengageDisabledTooltip);
        if (!this.config.UseGapCloser)
            ImGui.EndDisabled();
        ImGui.Unindent(8f);
        ImGui.Spacing();

        changed |= this.DrawToggleSectionHeader(
            "Escape",
            this.config.UseEscapeGapCloser,
            this.defaultConfig.UseEscapeGapCloser,
            v => this.config.UseEscapeGapCloser = v,
            "Uses selected movement skills to escape danger.\nOnly fires when the landing point is reported safe.\nIn Greed mode, still moves toward the safe destination.\nBLM will not escape out of active Ley Lines.",
            FontAwesomeIcon.SkullCrossbones,
            "This can put you in danger during combat.\nSome arena hazards and timing checks may need manual judgment.");
        var escapeDisabledTooltip = !this.config.UseEscapeGapCloser ? "Disabled by Use gap closer to escape danger in Gap Closers." : null;
        if (!this.config.UseEscapeGapCloser)
            ImGui.BeginDisabled();
        ImGui.Columns(3, "escapeJobs", false);
        changed |= this.Checkbox("MNK", this.config.EscapeGapCloserMNK, this.defaultConfig.EscapeGapCloserMNK, v => this.config.EscapeGapCloserMNK = v, disabledTooltip: escapeDisabledTooltip);
        ImGui.NextColumn();
        changed |= this.Checkbox("NIN", this.config.EscapeGapCloserNIN, this.defaultConfig.EscapeGapCloserNIN, v => this.config.EscapeGapCloserNIN = v, disabledTooltip: escapeDisabledTooltip);
        ImGui.NextColumn();
        changed |= this.Checkbox("DNC", this.config.EscapeGapCloserDNC, this.defaultConfig.EscapeGapCloserDNC, v => this.config.EscapeGapCloserDNC = v, disabledTooltip: escapeDisabledTooltip);
        ImGui.NextColumn();
        changed |= this.Checkbox("RPR", this.config.EscapeGapCloserRPR, this.defaultConfig.EscapeGapCloserRPR, v => this.config.EscapeGapCloserRPR = v, disabledTooltip: escapeDisabledTooltip);
        ImGui.NextColumn();
        changed |= this.Checkbox("VPR", this.config.EscapeGapCloserVPR, this.defaultConfig.EscapeGapCloserVPR, v => this.config.EscapeGapCloserVPR = v, disabledTooltip: escapeDisabledTooltip);
        ImGui.NextColumn();
        changed |= this.Checkbox("BLM", this.config.EscapeGapCloserBLM, this.defaultConfig.EscapeGapCloserBLM, v => this.config.EscapeGapCloserBLM = v, disabledTooltip: escapeDisabledTooltip);
        ImGui.NextColumn();
        changed |= this.Checkbox("SGE", this.config.EscapeGapCloserSGE, this.defaultConfig.EscapeGapCloserSGE, v => this.config.EscapeGapCloserSGE = v, disabledTooltip: escapeDisabledTooltip);
        ImGui.NextColumn();
        changed |= this.Checkbox("PCT", this.config.EscapeGapCloserPCT, this.defaultConfig.EscapeGapCloserPCT, v => this.config.EscapeGapCloserPCT = v, disabledTooltip: escapeDisabledTooltip);
        ImGui.NextColumn();
        changed |= this.Checkbox("BLU", this.config.EscapeGapCloserBLU, this.defaultConfig.EscapeGapCloserBLU, v => this.config.EscapeGapCloserBLU = v, disabledTooltip: escapeDisabledTooltip);
        ImGui.Columns(1);
        changed |= this.SliderFloat(
            "Minimum safety gap-close distance",
            this.config.MinimumEscapeGapCloserDistance,
            this.defaultConfig.MinimumEscapeGapCloserDistance,
            Configuration.MinimumGapCloserDistanceMin,
            Configuration.MinimumGapCloserDistanceMax,
            v => this.config.MinimumEscapeGapCloserDistance = v,
            "%.0f",
            escapeDisabledTooltip);
        if (!this.config.UseEscapeGapCloser)
            ImGui.EndDisabled();
        ImGui.Unindent(8f);
        ImGui.Spacing();

        return changed;
    }

    private bool DrawChatAndResetTab()
    {
        var changed = false;

        this.DrawSectionHeader("Chat");
        changed |= this.Checkbox("Echo command status to chat", this.config.EchoStatusToChat, this.defaultConfig.EchoStatusToChat, v => this.config.EchoStatusToChat = v, "Prints a message to chat when the plugin is enabled or disabled via command.");
        ImGui.Unindent(8f);
        ImGui.Spacing();

        this.DrawSectionHeader("Debug");
        if (ImGui.Button("Copy debug state"))
        {
            ImGui.SetClipboardText(this.debugState());
            this.copiedDebugStateUntil = DateTime.UtcNow.AddSeconds(2);
        }

        this.DrawInfoIcon("Copies current runtime state, BossMod strategy cache, integration state, and configuration values.");
        if (DateTime.UtcNow < this.copiedDebugStateUntil)
        {
            ImGui.SameLine();
            ImGui.TextColored(new Vector4(0.5f, 1.0f, 0.5f, 1.0f), "Copied.");
        }

        if (ImGui.Button("Copy combat history"))
        {
            ImGui.SetClipboardText(this.combatHistory());
            this.copiedHistoryUntil = DateTime.UtcNow.AddSeconds(2);
        }

        this.DrawInfoIcon("Copies AoE positioning decisions recorded during the last combat.\nResets when a new combat starts.");
        if (DateTime.UtcNow < this.copiedHistoryUntil)
        {
            ImGui.SameLine();
            ImGui.TextColored(new Vector4(0.5f, 1.0f, 0.5f, 1.0f), "Copied.");
        }

        ImGui.Unindent(8f);
        ImGui.Spacing();

        this.DrawSectionHeader("Reset");
        if (ImGui.Button("Reset ranges"))
        {
            this.config.ResetRanges();
            changed = true;
        }

        ImGui.SameLine();
        if (ImGui.Button("Reset all"))
        {
            this.config.ResetAll();
            changed = true;
        }

        ImGui.Unindent(8f);
        ImGui.Spacing();

        return changed;
    }

    // Emits a tinted section title followed by a separator and an indent.
    // Callers must call ImGui.Unindent(8f) after their section contents.
    private void DrawSectionHeader(string title)
    {
        ImGui.TextColored(new Vector4(0.75f, 0.75f, 1.0f, 1.0f), title);
        ImGui.Separator();
        ImGui.Indent(8f);
    }

    // Emits a tinted section title that also toggles the section's master setting.
    // Callers must call ImGui.Unindent(8f) after their section contents.
    private bool DrawToggleSectionHeader(string title, bool value, bool defaultValue, Action<bool> setter, string? tooltip = null, FontAwesomeIcon? icon = null, string? iconTooltip = null, string? disabledTooltip = null)
    {
        var changed = false;
        var current = value;
        var id = $"##{title}";
        var enabled = disabledTooltip == null;
        if (!enabled)
            ImGui.BeginDisabled();

        if (ImGui.Checkbox(id, ref current))
        {
            setter(current);
            changed = true;
        }

        var checkboxHovered = this.IsItemHoveredAllowDisabled();
        ImGui.SameLine();
        ImGui.TextColored(new Vector4(0.75f, 0.75f, 1.0f, 1.0f), title);
        var titleHovered = this.IsItemHoveredAllowDisabled();
        var iconHovered = false;

        if (icon != null)
        {
            ImGui.SameLine();
            ImGui.PushFont(UiBuilder.IconFont);
            ImGui.TextUnformatted(icon.Value.ToIconString());
            ImGui.PopFont();
            iconHovered = this.IsItemHoveredAllowDisabled();
            if (iconHovered && iconTooltip != null)
            {
                DrawWrappedTooltip(iconTooltip);
            }
        }

        this.DrawInfoIcon(tooltip);

        if (!enabled)
            ImGui.EndDisabled();

        if (enabled && (titleHovered || iconHovered) && ImGui.IsMouseClicked(ImGuiMouseButton.Left))
        {
            setter(!value);
            changed = true;
        }

        var hovered = checkboxHovered || titleHovered || iconHovered;
        this.DrawTooltip(hovered, tooltip, disabledTooltip);

        if (enabled && IsResetRequested(hovered))
        {
            if (value == defaultValue)
            {
                ImGui.Separator();
                ImGui.Indent(8f);
                return changed;
            }

            setter(defaultValue);
            changed = true;
        }

        ImGui.Separator();
        ImGui.Indent(8f);
        return changed;
    }

    private void DrawEnabledCheckbox()
    {
        var current = this.config.Enabled;
        var changed = ImGui.Checkbox("Enabled", ref current);
        if (IsResetRequested(ImGui.IsItemHovered()))
        {
            if (this.config.Enabled != this.defaultConfig.Enabled)
                this.setEnabled(this.defaultConfig.Enabled);
            return;
        }

        if (!changed)
        {
            return;
        }

        this.setEnabled(current);
    }

    private bool Checkbox(string label, bool value, bool defaultValue, Action<bool> setter, string? tooltip = null, string? disabledTooltip = null)
    {
        var current = value;
        var changed = ImGui.Checkbox(label, ref current);
        var hoveredForTooltip = this.IsItemHoveredAllowDisabled();
        var hovered = ImGui.IsItemHovered();
        this.DrawInfoIcon(tooltip);
        this.DrawTooltip(hoveredForTooltip, disabledTooltip: disabledTooltip);

        if (IsResetRequested(hovered))
        {
            if (value == defaultValue)
                return false;

            setter(defaultValue);
            return true;
        }

        if (!changed)
            return false;

        setter(current);
        return true;
    }

    private bool Combo<T>(string label, T value, T defaultValue, Action<T> setter, string? tooltip = null, Func<T, string>? displayName = null, string? disabledTooltip = null)
        where T : struct, Enum
    {
        var changed = false;
        ImGui.TextUnformatted(label);
        var labelHovered = ImGui.IsItemHovered();
        this.DrawTooltip(labelHovered, tooltip, disabledTooltip);
        this.DrawInfoIcon(tooltip);

        var getName = displayName ?? (v => v.ToString());

        ImGui.SetNextItemWidth(-1f);
        if (ImGui.BeginCombo($"##{label}", getName(value)))
        {
            foreach (var option in Enum.GetValues<T>())
            {
                var selected = EqualityComparer<T>.Default.Equals(value, option);
                if (ImGui.Selectable(getName(option), selected))
                {
                    setter(option);
                    changed = true;
                }

                if (selected)
                    ImGui.SetItemDefaultFocus();
            }

            ImGui.EndCombo();
        }

        var hovered = labelHovered || ImGui.IsItemHovered();

        if (IsResetRequested(hovered))
        {
            if (EqualityComparer<T>.Default.Equals(value, defaultValue))
                return changed;

            setter(defaultValue);
            return true;
        }

        return changed;
    }

    private bool DrawInfoIcon(string? tooltip)
    {
        if (tooltip == null)
        {
            return false;
        }

        ImGui.SameLine();
        ImGui.PushFont(UiBuilder.IconFont);
        ImGui.TextColored(new Vector4(0.55f, 0.65f, 1.0f, 1.0f), FontAwesomeIcon.InfoCircle.ToIconString());
        ImGui.PopFont();

        var hovered = this.IsItemHoveredAllowDisabled();
        if (hovered)
        {
            DrawWrappedTooltip(tooltip);
        }

        return hovered;
    }

    private bool SliderFloat(string label, float value, float defaultValue, float min, float max, Action<float> setter, string format = "%.1f", string? disabledTooltip = null)
    {
        var id = $"##{label}";
        ImGui.TextUnformatted(label);
        var labelHoveredForTooltip = this.IsItemHoveredAllowDisabled();
        var labelHovered = ImGui.IsItemHovered();
        this.DrawTooltip(labelHoveredForTooltip, disabledTooltip: disabledTooltip);
        ImGui.SetNextItemWidth(-1f);

        if (this.editingSliders.Contains(label))
        {
            var input = value;
            ImGui.SetKeyboardFocusHere(0);
            if (ImGui.InputFloat(id, ref input, 0f, 0f, format, ImGuiInputTextFlags.EnterReturnsTrue))
            {
                this.editingSliders.Remove(label);
                input = Math.Clamp(input, min, max);
                if (format == "%.0f")
                    input = MathF.Round(input);
                setter(input);
                return true;
            }

            var inputHoveredForTooltip = this.IsItemHoveredAllowDisabled();
            var inputHovered = ImGui.IsItemHovered();
            this.DrawTooltip(inputHoveredForTooltip, disabledTooltip: disabledTooltip);
            if (IsResetRequested(labelHovered || inputHovered))
            {
                this.editingSliders.Remove(label);
                setter(defaultValue);
                return true;
            }

            if (ImGui.IsItemDeactivated())
                this.editingSliders.Remove(label);

            return false;
        }

        var current = value;
        var changed = ImGui.SliderFloat(id, ref current, min, max, format);
        var sliderHoveredForTooltip = this.IsItemHoveredAllowDisabled();
        var sliderHovered = ImGui.IsItemHovered();
        this.DrawTooltip(sliderHoveredForTooltip, disabledTooltip: disabledTooltip);
        if (IsResetRequested(labelHovered || sliderHovered))
        {
            this.editingSliders.Remove(label);
            if (Math.Abs(value - defaultValue) <= 0.01f)
                return false;

            setter(defaultValue);
            return true;
        }

        if (!changed)
        {
            if (sliderHovered && ImGui.IsMouseDoubleClicked(ImGuiMouseButton.Left))
                this.editingSliders.Add(label);
            return false;
        }

        if (format == "%.0f")
            current = MathF.Round(current);
        setter(current);
        return true;
    }

    private bool SliderInt(string label, int value, int defaultValue, int min, int max, Action<int> setter, string? disabledTooltip = null)
    {
        var id = $"##{label}";
        ImGui.TextUnformatted(label);
        var labelHoveredForTooltip = this.IsItemHoveredAllowDisabled();
        var labelHovered = ImGui.IsItemHovered();
        this.DrawTooltip(labelHoveredForTooltip, disabledTooltip: disabledTooltip);
        ImGui.SetNextItemWidth(-1f);

        if (this.editingSliders.Contains(label))
        {
            var input = value;
            ImGui.SetKeyboardFocusHere(0);
            if (ImGui.InputInt(id, ref input, 0, 0, default, ImGuiInputTextFlags.EnterReturnsTrue))
            {
                this.editingSliders.Remove(label);
                input = Math.Clamp(input, min, max);
                setter(input);
                return true;
            }

            var inputHoveredForTooltip = this.IsItemHoveredAllowDisabled();
            var inputHovered = ImGui.IsItemHovered();
            this.DrawTooltip(inputHoveredForTooltip, disabledTooltip: disabledTooltip);
            if (IsResetRequested(labelHovered || inputHovered))
            {
                this.editingSliders.Remove(label);
                setter(defaultValue);
                return true;
            }

            if (ImGui.IsItemDeactivated())
                this.editingSliders.Remove(label);

            return false;
        }

        var current = value;
        var changed = ImGui.SliderInt(id, ref current, min, max);
        var sliderHoveredForTooltip = this.IsItemHoveredAllowDisabled();
        var sliderHovered = ImGui.IsItemHovered();
        this.DrawTooltip(sliderHoveredForTooltip, disabledTooltip: disabledTooltip);
        if (IsResetRequested(labelHovered || sliderHovered))
        {
            this.editingSliders.Remove(label);
            if (value == defaultValue)
                return false;

            setter(defaultValue);
            return true;
        }

        if (!changed)
        {
            if (sliderHovered && ImGui.IsMouseDoubleClicked(ImGuiMouseButton.Left))
                this.editingSliders.Add(label);
            return false;
        }

        setter(current);
        return true;
    }

    private bool IsResetRequested(bool hovered)
    {
        return hovered && !ImGui.IsAnyItemActive() && this.backspacePressedThisFrame;
    }

    private bool IsItemHoveredAllowDisabled()
    {
        return ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled);
    }

    private void DrawTooltip(bool hovered, string? tooltip = null, string? disabledTooltip = null)
    {
        if (!hovered)
        {
            return;
        }

        if (disabledTooltip != null)
        {
            DrawWrappedTooltip(disabledTooltip);
            return;
        }

        if (tooltip != null)
        {
            DrawWrappedTooltip(tooltip);
        }
    }

    private static void DrawWrappedTooltip(string tooltip)
    {
        ImGui.BeginTooltip();
        ImGui.PushTextWrapPos(TooltipWrapWidth);
        ImGui.TextUnformatted(tooltip);
        ImGui.PopTextWrapPos();
        ImGui.EndTooltip();
    }
}

using System;
using System.Collections.Generic;
using System.IO;
using System.Numerics;
using Dalamud.Game.ClientState.Keys;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Textures;
using Dalamud.Interface.Windowing;
using Dalamud.Plugin.Services;

namespace XelsCombatAI.UI;

internal sealed class ConfigWindow : Window, IDisposable
{
    private const float TooltipWrapWidth = 360f;
    private const float SidebarWidth = 135f;
    private const float LogoSize = SidebarWidth;

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
    private readonly string iconPath;
    private readonly ISharedImmediateTexture? iconTexture;
    private readonly HashSet<string> editingSliders = [];
    private ConfigPage selectedPage = ConfigPage.General;
    private DateTime copiedDebugStateUntil = DateTime.MinValue;
    private DateTime copiedHistoryUntil = DateTime.MinValue;
    private bool backspacePressedThisFrame;
    private bool wasBackspaceDown;

    public ConfigWindow(Configuration config, Action save, Action resetRuntimeState, Action<bool> setEnabled, Func<string> debugState, Func<string> combatHistory, Func<string?> dependencyWarning, Func<string?> trueNorthWarning, Action manageTrueNorthEnabled, IKeyState keyState, ITextureProvider textureProvider, string iconPath)
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
        this.iconPath = iconPath;
        if (File.Exists(this.iconPath))
        {
            this.iconTexture = textureProvider.GetFromFile(this.iconPath);
        }

        this.Flags = ImGuiWindowFlags.AlwaysAutoResize;
        this.SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new(640, 0),
            MaximumSize = new(760, float.MaxValue)
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

        if (ImGui.BeginTable("##xcai_layout", 2, ImGuiTableFlags.SizingFixedFit))
        {
            ImGui.TableSetupColumn("##nav", ImGuiTableColumnFlags.WidthFixed, SidebarWidth);
            ImGui.TableSetupColumn("##page", ImGuiTableColumnFlags.WidthStretch);
            ImGui.TableNextRow();
            ImGui.TableSetColumnIndex(0);
            this.DrawSidebar();
            ImGui.TableSetColumnIndex(1);
            ImGui.Spacing();
            changed |= this.DrawSelectedPage();
            ImGui.EndTable();
        }

        if (changed)
        {
            this.config.Clamp();
            this.resetRuntimeState();
            this.save();
        }
    }

    private bool DrawSelectedPage()
    {
        return this.selectedPage switch
        {
            ConfigPage.General         => this.DrawGeneralTab(),
            ConfigPage.Movement        => this.DrawMovementTab(),
            ConfigPage.AoeAndTrash     => this.DrawAoeAndTrashTab(),
            ConfigPage.Positionals     => this.DrawPositionalsTab(),
            ConfigPage.BlackMage       => this.DrawBlackMageTab(),
            ConfigPage.Dashes          => this.DrawDashesTab(),
            ConfigPage.Troubleshooting => this.DrawTroubleshootingTab(),
            _                          => false
        };
    }

    private void DrawSidebar()
    {
        this.DrawLogo();
        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();
        this.DrawNavItem(ConfigPage.General, "General");
        this.DrawNavItem(ConfigPage.Movement, "Movement");
        this.DrawNavItem(ConfigPage.AoeAndTrash, "AoE & Trash");
        this.DrawNavItem(ConfigPage.Positionals, "Positionals");
        this.DrawNavItem(ConfigPage.BlackMage, "Black Mage");
        this.DrawNavItem(ConfigPage.Dashes, "Dashes");
        ImGui.Separator();
        this.DrawNavItem(ConfigPage.Troubleshooting, "Troubleshooting");
    }

    private void DrawLogo()
    {
        var icon = this.iconTexture?.GetWrapOrDefault();
        if (icon?.Handle != null)
        {
            ImGui.Image(icon.Handle, new Vector2(LogoSize, LogoSize));
            return;
        }

        var text = "XCAI";
        var textSize = ImGui.CalcTextSize(text);
        ImGui.SetCursorPosX(ImGui.GetCursorPosX() + MathF.Max(0f, (LogoSize - textSize.X) * 0.5f));
        ImGui.Dummy(new Vector2(LogoSize, 28f));
        ImGui.SetCursorPosY(ImGui.GetCursorPosY() - 22f);
        ImGui.TextColored(new Vector4(0.75f, 0.75f, 1.0f, 1.0f), text);
    }

    private void DrawNavItem(ConfigPage page, string label)
    {
        var selected = this.selectedPage == page;
        ImGui.PushStyleVar(ImGuiStyleVar.SelectableTextAlign, new Vector2(0.5f, 0.5f));
        if (selected)
        {
            ImGui.PushStyleColor(ImGuiCol.Header, new Vector4(0.25f, 0.28f, 0.55f, 0.95f));
            ImGui.PushStyleColor(ImGuiCol.HeaderHovered, new Vector4(0.30f, 0.34f, 0.65f, 1.0f));
        }

        if (ImGui.Selectable(label, selected, ImGuiSelectableFlags.None, new Vector2(SidebarWidth - 8f, 24f)))
        {
            this.selectedPage = page;
        }

        if (selected)
        {
            ImGui.PopStyleColor(2);
        }

        ImGui.PopStyleVar();
    }

    private bool DrawGeneralTab()
    {
        var changed = false;

        this.DrawEnabledCheckbox();
        ImGui.Spacing();

        this.DrawSectionHeader("Chat");
        changed |= this.Checkbox("Print command messages in chat", this.config.EchoStatusToChat, this.defaultConfig.EchoStatusToChat, v => this.config.EchoStatusToChat = v, "Prints a chat message when /xcai turns automation on or off.");
        ImGui.Unindent(8f);
        ImGui.Spacing();

        this.DrawSectionHeader("Reset");
        if (ImGui.Button("Reset movement settings"))
        {
            this.config.ResetBehaviorSettings();
            changed = true;
        }

        ImGui.SameLine();
        if (ImGui.Button("Reset everything"))
        {
            if (this.config.Enabled)
            {
                this.setEnabled(false);
            }

            this.config.ResetAll();
            changed = true;
        }

        ImGui.Unindent(8f);
        ImGui.Spacing();

        return changed;
    }

    private bool DrawMovementTab()
    {
        var changed = false;

        changed |= this.Checkbox("Automate movement", this.config.ManageMovement, this.defaultConfig.ManageMovement, v => this.config.ManageMovement = v, "Lets BossMod move you during combat.");
        var movementDisabledTooltip = !this.config.ManageMovement ? "Disabled by Automate movement." : null;
        if (!this.config.ManageMovement)
            ImGui.BeginDisabled();

        changed |= this.Checkbox("Pause when I move", this.config.RespectManualMovement, this.defaultConfig.RespectManualMovement, v => this.config.RespectManualMovement = v, "Stops automatic movement while you move manually. Starts again when you stop.", movementDisabledTooltip);
        changed |= this.Combo("Movement timing", this.config.CombatStyle, this.defaultConfig.CombatStyle, v => this.config.CombatStyle = v, "Controls how long to wait before moving for mechanics.", v => v switch
        {
            CombatStyle.Greed           => "Greedy",
            CombatStyle.GreedGCD        => "Greedy until next GCD",
            CombatStyle.GreedLastMoment => "Last second",
            _                           => "Safe first"
        }, movementDisabledTooltip);
        changed |= this.Checkbox("Close in to attack range", this.config.ManageTargetUptime, this.defaultConfig.ManageTargetUptime, v => this.config.ManageTargetUptime = v, "Moves into attack range of your target or trash pack. Uses your job's actual attack range.", movementDisabledTooltip);
        changed |= this.DrawToggleSectionHeader("Avoid danger zones", this.config.ManageForbiddenZoneDistance, this.defaultConfig.ManageForbiddenZoneDistance, v => this.config.ManageForbiddenZoneDistance = v, "Keeps extra space from danger zones when possible.", disabledTooltip: movementDisabledTooltip);
        var forbiddenZoneDisabledTooltip = !this.config.ManageForbiddenZoneDistance ? "Disabled by Avoid danger zones." : movementDisabledTooltip;
        if (!this.config.ManageForbiddenZoneDistance)
            ImGui.BeginDisabled();
        changed |= this.SliderFloat("Extra danger-zone space", this.config.PreferredForbiddenZoneDistance, this.defaultConfig.PreferredForbiddenZoneDistance, 0f, 3f, v => this.config.PreferredForbiddenZoneDistance = v, tooltip: "How much extra space to keep from the edge of danger zones.", disabledTooltip: forbiddenZoneDisabledTooltip);
        if (!this.config.ManageForbiddenZoneDistance)
            ImGui.EndDisabled();
        ImGui.Unindent(8f);
        ImGui.Spacing();

        changed |= this.Checkbox(
            "Stay in healer range (healers only)",
            this.config.ManageHealerCoverageZone,
            this.defaultConfig.ManageHealerCoverageZone,
            v => this.config.ManageHealerCoverageZone = v,
            "Healers only: prefers safe positions where your 20-yalm healing circle covers as many visible party members as possible.",
            movementDisabledTooltip);
        changed |= this.Checkbox(
            "Stand in defensive ground effects",
            this.config.ManageDefensiveGroundZonePositioning,
            this.defaultConfig.ManageDefensiveGroundZonePositioning,
            v => this.config.ManageDefensiveGroundZonePositioning = v,
            "Moves into helpful party ground effects like Asylum, Sacred Soil, Earthly Star, and Collective Unconscious.",
            movementDisabledTooltip);
        changed |= this.Checkbox(
            "Stand behind Passage of Arms",
            this.config.ManagePassageOfArmsPositioning,
            this.defaultConfig.ManagePassageOfArmsPositioning,
            v => this.config.ManagePassageOfArmsPositioning = v,
            "Moves behind a Paladin using Passage of Arms when it is safe.",
            movementDisabledTooltip);
        changed |= this.Checkbox(
            "Bring aggro to tank",
            this.config.ManageAggroSafetyMovement,
            this.defaultConfig.ManageAggroSafetyMovement,
            v => this.config.ManageAggroSafetyMovement = v,
            "If an enemy targets you for more than 3 seconds, moves toward a party tank and lowers that enemy's target priority. Does nothing on tanks.",
            movementDisabledTooltip);
        changed |= this.Checkbox(
            "Avoid standing inside bosses",
            this.config.AvoidStandingInsideEnemies,
            this.defaultConfig.AvoidStandingInsideEnemies,
            v => this.config.AvoidStandingInsideEnemies = v,
            "Moves out of the center of a boss hitbox when BossMod has an active module for the current target.",
            movementDisabledTooltip);
        changed |= this.Checkbox(
            "Avoid boss frontal cone",
            this.config.AvoidBossFrontalCone,
            this.defaultConfig.AvoidBossFrontalCone,
            v => this.config.AvoidBossFrontalCone = v,
            "Slightly prefers not standing in front of the boss where the tank holds aggro.\nOnly applies when a boss module is active and a tank is in the party.",
            movementDisabledTooltip);
        changed |= this.Checkbox(
            "Avoid arena edge",
            this.config.AvoidArenaEdge,
            this.defaultConfig.AvoidArenaEdge,
            v => this.config.AvoidArenaEdge = v,
            "Slightly prefers not standing on the arena boundary when other movement goals do not matter.",
            movementDisabledTooltip);
        if (!this.config.ManageMovement)
            ImGui.EndDisabled();
        ImGui.Spacing();

        return changed;
    }

    private bool DrawAoeAndTrashTab()
    {
        var changed = false;
        var movementDisabledTooltip = !this.config.ManageMovement ? "Disabled by Automate movement on the Movement tab." : null;

        if (!this.config.ManageMovement)
            ImGui.BeginDisabled();
        changed |= this.Checkbox("Move for better AoE hits", this.config.ManageAoePackPositioning, this.defaultConfig.ManageAoePackPositioning, v => this.config.ManageAoePackPositioning = v, "Moves to a better spot when your next AoE can hit more enemies.", movementDisabledTooltip);
        if (!this.config.ManageMovement)
            ImGui.EndDisabled();

        changed |= this.Checkbox("Pick better AoE target", this.config.PickBetterAoeTarget, this.defaultConfig.PickBetterAoeTarget, v => this.config.PickBetterAoeTarget = v, "Targets the enemy that lets your AoE hit more enemies.");
        changed |= this.Checkbox("Keep a trash target selected", this.config.KeepTrashTargetSelected, this.defaultConfig.KeepTrashTargetSelected, v => this.config.KeepTrashTargetSelected = v, "Keeps targeting a useful enemy during trash pulls.");

        return changed;
    }

    private bool DrawPositionalsTab()
    {
        var changed = false;

        changed |= this.DrawToggleSectionHeader("Do positionals", this.config.ManagePositionals, this.defaultConfig.ManagePositionals, v => this.config.ManagePositionals = v, "Moves to the rear or flank when your job needs a positional.");
        var positionalsDisabledTooltip = !this.config.ManagePositionals ? "Disabled by Do positionals." : null;
        if (!this.config.ManagePositionals)
            ImGui.BeginDisabled();
        changed |= this.Checkbox(
            "Use True North",
            this.config.ManageTrueNorth,
            this.defaultConfig.ManageTrueNorth,
            v => { this.config.ManageTrueNorth = v; if (v) this.manageTrueNorthEnabled(); },
            "Uses True North when you need a positional but cannot reach the right side of the target.",
            positionalsDisabledTooltip);
        if (!this.config.ManagePositionals)
            ImGui.EndDisabled();
        ImGui.Unindent(8f);
        ImGui.Spacing();

        return changed;
    }

    private bool DrawBlackMageTab()
    {
        var changed = false;

        changed |= this.DrawToggleSectionHeader("Stay in Ley Lines", this.config.ManageLeylines, this.defaultConfig.ManageLeylines, v => this.config.ManageLeylines = v, "Tries to stay in your existing Ley Lines when safe. Does not place Ley Lines.");
        var leylinesDisabledTooltip = !this.config.ManageLeylines ? "Disabled by Stay in Ley Lines." : null;
        if (!this.config.ManageLeylines)
            ImGui.BeginDisabled();
        changed |= this.Checkbox("Use Between the Lines", this.config.UseBetweenTheLines, this.defaultConfig.UseBetweenTheLines, v => this.config.UseBetweenTheLines = v, "Uses Between the Lines to return to Ley Lines.", leylinesDisabledTooltip);
        changed |= this.Checkbox("Use Retrace", this.config.UseRetrace, this.defaultConfig.UseRetrace, v => this.config.UseRetrace = v, "Uses Retrace to return to Ley Lines.", leylinesDisabledTooltip);
        changed |= this.Checkbox("Walk back to Ley Lines", this.config.ReturnToLeylines, this.defaultConfig.ReturnToLeylines, v => this.config.ReturnToLeylines = v, "Walks back to Ley Lines when teleport skills are not used or not available.", leylinesDisabledTooltip);
        if (!this.config.ManageLeylines)
            ImGui.EndDisabled();
        ImGui.Unindent(8f);
        ImGui.Spacing();

        return changed;
    }

    private bool DrawDashesTab()
    {
        var changed = false;

        changed |= this.DrawToggleSectionHeader(
            "Dash back to target",
            this.config.UseGapCloser,
            this.defaultConfig.UseGapCloser,
            v => this.config.UseGapCloser = v,
            "Uses movement skills to return to your target when you are knocked away.",
            FontAwesomeIcon.SkullCrossbones,
            "Very likely to kill you in some fights. Dashes can land in bad timing, snapshots, knockbacks, cleaves, or arena hazards. Use only if you accept that risk.");
        var reengageDisabledTooltip = !this.config.UseGapCloser ? "Disabled by Dash back to target." : null;
        if (!this.config.UseGapCloser)
            ImGui.BeginDisabled();
        this.DrawSectionHeader("Jobs allowed to dash back");
        if (ImGui.BeginTable("reengageJobs", 3, ImGuiTableFlags.SizingStretchSame))
        {
            changed |= this.JobCheckbox("PLD", this.config.GapCloserPLD, this.defaultConfig.GapCloserPLD, v => this.config.GapCloserPLD = v, reengageDisabledTooltip);
            changed |= this.JobCheckbox("WAR", this.config.GapCloserWAR, this.defaultConfig.GapCloserWAR, v => this.config.GapCloserWAR = v, reengageDisabledTooltip);
            changed |= this.JobCheckbox("DRK", this.config.GapCloserDRK, this.defaultConfig.GapCloserDRK, v => this.config.GapCloserDRK = v, reengageDisabledTooltip);
            changed |= this.JobCheckbox("GNB", this.config.GapCloserGNB, this.defaultConfig.GapCloserGNB, v => this.config.GapCloserGNB = v, reengageDisabledTooltip);
            changed |= this.JobCheckbox("MNK", this.config.GapCloserMNK, this.defaultConfig.GapCloserMNK, v => this.config.GapCloserMNK = v, reengageDisabledTooltip);
            changed |= this.JobCheckbox("DRG", this.config.GapCloserDRG, this.defaultConfig.GapCloserDRG, v => this.config.GapCloserDRG = v, reengageDisabledTooltip);
            changed |= this.JobCheckbox("NIN", this.config.GapCloserNIN, this.defaultConfig.GapCloserNIN, v => this.config.GapCloserNIN = v, reengageDisabledTooltip);
            changed |= this.JobCheckbox("SAM", this.config.GapCloserSAM, this.defaultConfig.GapCloserSAM, v => this.config.GapCloserSAM = v, reengageDisabledTooltip);
            changed |= this.JobCheckbox("DNC", this.config.GapCloserDNC, this.defaultConfig.GapCloserDNC, v => this.config.GapCloserDNC = v, reengageDisabledTooltip);
            changed |= this.JobCheckbox("RPR", this.config.GapCloserRPR, this.defaultConfig.GapCloserRPR, v => this.config.GapCloserRPR = v, reengageDisabledTooltip);
            changed |= this.JobCheckbox("VPR", this.config.GapCloserVPR, this.defaultConfig.GapCloserVPR, v => this.config.GapCloserVPR = v, reengageDisabledTooltip);
            changed |= this.JobCheckbox("WHM", this.config.GapCloserWHM, this.defaultConfig.GapCloserWHM, v => this.config.GapCloserWHM = v, reengageDisabledTooltip);
            ImGui.EndTable();
        }
        ImGui.Unindent(8f);
        changed |= this.SliderFloat(
            "Minimum dash-back distance",
            this.config.MinimumReengageGapCloserDistance,
            this.defaultConfig.MinimumReengageGapCloserDistance,
            Configuration.MinimumGapCloserDistanceMin,
            Configuration.MinimumGapCloserDistanceMax,
            v => this.config.MinimumReengageGapCloserDistance = v,
            "%.0f",
            tooltip: "Only dashes back if the target is at least this far away.",
            disabledTooltip: reengageDisabledTooltip);
        if (!this.config.UseGapCloser)
            ImGui.EndDisabled();
        ImGui.Unindent(8f);
        ImGui.Spacing();

        changed |= this.DrawToggleSectionHeader(
            "Dash to safety",
            this.config.UseEscapeGapCloser,
            this.defaultConfig.UseEscapeGapCloser,
            v => this.config.UseEscapeGapCloser = v,
            "Uses movement skills to reach a safe spot faster.",
            FontAwesomeIcon.SkullCrossbones,
            "Very likely to kill you in some fights. A dash can choose a safe-looking spot that becomes unsafe, resolves too late, or fails fight-specific timing. Use only if you accept that risk.");
        var escapeDisabledTooltip = !this.config.UseEscapeGapCloser ? "Disabled by Dash to safety." : null;
        if (!this.config.UseEscapeGapCloser)
            ImGui.BeginDisabled();
        this.DrawSectionHeader("Jobs allowed to dash to safety");
        if (ImGui.BeginTable("escapeJobs", 3, ImGuiTableFlags.SizingStretchSame))
        {
            changed |= this.JobCheckbox("MNK", this.config.EscapeGapCloserMNK, this.defaultConfig.EscapeGapCloserMNK, v => this.config.EscapeGapCloserMNK = v, escapeDisabledTooltip);
            changed |= this.JobCheckbox("NIN", this.config.EscapeGapCloserNIN, this.defaultConfig.EscapeGapCloserNIN, v => this.config.EscapeGapCloserNIN = v, escapeDisabledTooltip);
            changed |= this.JobCheckbox("DNC", this.config.EscapeGapCloserDNC, this.defaultConfig.EscapeGapCloserDNC, v => this.config.EscapeGapCloserDNC = v, escapeDisabledTooltip);
            changed |= this.JobCheckbox("RPR", this.config.EscapeGapCloserRPR, this.defaultConfig.EscapeGapCloserRPR, v => this.config.EscapeGapCloserRPR = v, escapeDisabledTooltip);
            changed |= this.JobCheckbox("VPR", this.config.EscapeGapCloserVPR, this.defaultConfig.EscapeGapCloserVPR, v => this.config.EscapeGapCloserVPR = v, escapeDisabledTooltip);
            changed |= this.JobCheckbox("WHM", this.config.EscapeGapCloserWHM, this.defaultConfig.EscapeGapCloserWHM, v => this.config.EscapeGapCloserWHM = v, escapeDisabledTooltip);
            changed |= this.JobCheckbox("BLM", this.config.EscapeGapCloserBLM, this.defaultConfig.EscapeGapCloserBLM, v => this.config.EscapeGapCloserBLM = v, escapeDisabledTooltip);
            changed |= this.JobCheckbox("SGE", this.config.EscapeGapCloserSGE, this.defaultConfig.EscapeGapCloserSGE, v => this.config.EscapeGapCloserSGE = v, escapeDisabledTooltip);
            changed |= this.JobCheckbox("PCT", this.config.EscapeGapCloserPCT, this.defaultConfig.EscapeGapCloserPCT, v => this.config.EscapeGapCloserPCT = v, escapeDisabledTooltip);
            ImGui.EndTable();
        }
        ImGui.Unindent(8f);
        changed |= this.SliderFloat(
            "Minimum safety-dash distance",
            this.config.MinimumEscapeGapCloserDistance,
            this.defaultConfig.MinimumEscapeGapCloserDistance,
            Configuration.MinimumGapCloserDistanceMin,
            Configuration.MinimumGapCloserDistanceMax,
            v => this.config.MinimumEscapeGapCloserDistance = v,
            "%.0f",
            tooltip: "Only dashes to safety if the safe spot is at least this far away.",
            disabledTooltip: escapeDisabledTooltip);
        if (!this.config.UseEscapeGapCloser)
            ImGui.EndDisabled();
        ImGui.Unindent(8f);
        ImGui.Spacing();

        return changed;
    }

    private bool DrawTroubleshootingTab()
    {
        var changed = false;

        this.DrawSectionHeader("Overlay");
        changed |= this.Checkbox(
            "Show movement overlay",
            this.config.ShowDecisionOverlay,
            this.defaultConfig.ShowDecisionOverlay,
            v => this.config.ShowDecisionOverlay = v,
            "Shows movement goals, suggested positions, and debug visuals in the game world.");
        var overlayDisabledTooltip = !this.config.ShowDecisionOverlay ? "Disabled by Show movement overlay." : null;
        if (!this.config.ShowDecisionOverlay)
            ImGui.BeginDisabled();
        changed |= this.Checkbox(
            "Show overlay debug HUD",
            this.config.ShowDecisionOverlayHud,
            this.defaultConfig.ShowDecisionOverlayHud,
            v => this.config.ShowDecisionOverlayHud = v,
            "Shows a movable debug window with current overlay settings and effective states.",
            overlayDisabledTooltip);
        if (!this.config.ShowDecisionOverlay)
            ImGui.EndDisabled();
        ImGui.Unindent(8f);
        ImGui.Spacing();

        this.DrawSectionHeader("Copy");
        if (ImGui.Button("Copy debug snapshot"))
        {
            ImGui.SetClipboardText(this.debugState());
            this.copiedDebugStateUntil = DateTime.UtcNow.AddSeconds(2);
        }

        this.DrawInfoIcon("Copies current settings and plugin status for troubleshooting.");
        if (DateTime.UtcNow < this.copiedDebugStateUntil)
        {
            ImGui.SameLine();
            ImGui.TextColored(new Vector4(0.5f, 1.0f, 0.5f, 1.0f), "Copied.");
        }

        if (ImGui.Button("Copy combat log"))
        {
            ImGui.SetClipboardText(this.combatHistory());
            this.copiedHistoryUntil = DateTime.UtcNow.AddSeconds(2);
        }

        this.DrawInfoIcon("Copies recent AoE and movement decisions for troubleshooting.");
        if (DateTime.UtcNow < this.copiedHistoryUntil)
        {
            ImGui.SameLine();
            ImGui.TextColored(new Vector4(0.5f, 1.0f, 0.5f, 1.0f), "Copied.");
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
        var hovered = ImGui.IsItemHovered();
        this.DrawInfoIcon("Turns combat automation on or off.");
        if (IsResetRequested(hovered))
        {
            if (this.config.Enabled != this.defaultConfig.Enabled)
                this.setEnabled(this.defaultConfig.Enabled);
            return;
        }

        this.DrawTooltip(hovered, "Turns combat automation on or off.");

        if (!changed)
        {
            return;
        }

        this.setEnabled(current);
    }

    private bool Checkbox(string label, bool value, bool defaultValue, Action<bool> setter, string? tooltip = null, string? disabledTooltip = null, FontAwesomeIcon? icon = null, string? iconTooltip = null)
    {
        var current = value;
        var changed = ImGui.Checkbox(label, ref current);
        var hoveredForTooltip = this.IsItemHoveredAllowDisabled();
        var hovered = ImGui.IsItemHovered();
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
        this.DrawTooltip(hoveredForTooltip, disabledTooltip: disabledTooltip);

        if (IsResetRequested(hovered || iconHovered))
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

    private bool JobCheckbox(string label, bool value, bool defaultValue, Action<bool> setter, string? disabledTooltip)
    {
        ImGui.TableNextColumn();
        return this.Checkbox(label, value, defaultValue, setter, disabledTooltip: disabledTooltip);
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

    private bool SliderFloat(string label, float value, float defaultValue, float min, float max, Action<float> setter, string format = "%.1f", string? tooltip = null, string? disabledTooltip = null)
    {
        var id = $"##{label}";
        ImGui.TextUnformatted(label);
        var labelHoveredForTooltip = this.IsItemHoveredAllowDisabled();
        var labelHovered = ImGui.IsItemHovered();
        this.DrawTooltip(labelHoveredForTooltip, tooltip, disabledTooltip);
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
            this.DrawTooltip(inputHoveredForTooltip, tooltip, disabledTooltip);
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
        this.DrawTooltip(sliderHoveredForTooltip, tooltip, disabledTooltip);
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

    private enum ConfigPage
    {
        General,
        Movement,
        AoeAndTrash,
        Positionals,
        BlackMage,
        Dashes,
        Troubleshooting
    }
}

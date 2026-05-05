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
    private readonly Configuration config;
    private readonly Configuration defaultConfig = new();
    private readonly Action save;
    private readonly Action resetRuntimeState;
    private readonly Action<bool> setEnabled;
    private readonly Func<string?> dependencyWarning;
    private readonly Func<string?> trueNorthWarning;
    private readonly Action manageTrueNorthEnabled;
    private readonly IKeyState keyState;
    private readonly HashSet<string> editingSliders = [];
    private readonly HashSet<string> openSections = [];
    private bool backspacePressedThisFrame;
    private bool wasBackspaceDown;

    public ConfigWindow(Configuration config, Action save, Action resetRuntimeState, Action<bool> setEnabled, Func<string?> dependencyWarning, Func<string?> trueNorthWarning, Action manageTrueNorthEnabled, IKeyState keyState)
        : base("Xel's Combat AI Configuration###XelsCombatAIConfig")
    {
        this.config = config;
        this.save = save;
        this.resetRuntimeState = resetRuntimeState;
        this.setEnabled = setEnabled;
        this.dependencyWarning = dependencyWarning;
        this.trueNorthWarning = trueNorthWarning;
        this.manageTrueNorthEnabled = manageTrueNorthEnabled;
        this.keyState = keyState;
        this.SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new(480, 360),
            MaximumSize = new(float.MaxValue, float.MaxValue)
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

        this.DrawEnabledCheckbox(dependencyWarningText);
        ImGui.Spacing();

        if (ImGui.BeginTabBar("##xcai_tabs"))
        {
            if (ImGui.BeginTabItem("General"))
            {
                ImGui.Spacing();
                changed |= this.DrawGeneralTab();
                ImGui.EndTabItem();
            }

            if (ImGui.BeginTabItem("Ranges"))
            {
                ImGui.Spacing();
                changed |= this.DrawRangesTab();
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

    private bool DrawGeneralTab()
    {
        var changed = false;

        this.DrawSectionHeader("Movement");
        changed |= this.Checkbox("Manage movement in combat", this.config.ManageMovement, this.defaultConfig.ManageMovement, v => this.config.ManageMovement = v);
        changed |= this.Checkbox("Follow tank on trash", this.config.ManagePartyRoleFollow, this.defaultConfig.ManagePartyRoleFollow, v => this.config.ManagePartyRoleFollow = v, "Follow the tank on targets without boss modules.\nDisabled automatically on boss encounters.");
        ImGui.Unindent(8f);
        ImGui.Spacing();

        this.DrawSectionHeader("Positioning");
        if (this.CollapsingCheckbox("Manage positionals", this.config.ManagePositionals, this.defaultConfig.ManagePositionals, v => { this.config.ManagePositionals = v; changed = true; }))
        {
            ImGui.Indent();
            changed |= this.Checkbox(
                "Manage True North",
                this.config.ManageTrueNorth,
                this.defaultConfig.ManageTrueNorth,
                v => { this.config.ManageTrueNorth = v; if (v) this.manageTrueNorthEnabled(); },
                "Uses True North from XCAI.\nDisables RSR Auto True North while enabled.\nRSR is not restored automatically.");
            ImGui.Unindent();
        }

        if (this.CollapsingCheckbox("Manage Ley Lines", this.config.ManageLeylines, this.defaultConfig.ManageLeylines, v => { this.config.ManageLeylines = v; changed = true; }, tooltip: "Manages existing Ley Lines only.\nWill not place Ley Lines."))
        {
            ImGui.Indent();
            changed |= this.Checkbox("Use Between the Lines", this.config.UseBetweenTheLines, this.defaultConfig.UseBetweenTheLines, v => this.config.UseBetweenTheLines = v);
            changed |= this.Checkbox("Use Retrace", this.config.UseRetrace, this.defaultConfig.UseRetrace, v => this.config.UseRetrace = v);
            changed |= this.Checkbox("Walk back to Ley Lines", this.config.ReturnToLeylines, this.defaultConfig.ReturnToLeylines, v => this.config.ReturnToLeylines = v);
            ImGui.Unindent();
        }

        if (this.CollapsingCheckbox("Use gap closer to (re)engage", this.config.UseGapCloser, this.defaultConfig.UseGapCloser, v => { this.config.UseGapCloser = v; changed = true; }, tooltip: "Gets back into melee range when BossMod says it is safe.\nHoles in the floor will still absolutely kill you.", icon: FontAwesomeIcon.SkullCrossbones))
        {
            ImGui.Indent();
            changed |= this.Checkbox("PLD — Intervene", this.config.GapCloserPLD, this.defaultConfig.GapCloserPLD, v => this.config.GapCloserPLD = v);
            changed |= this.Checkbox("WAR — Onslaught", this.config.GapCloserWAR, this.defaultConfig.GapCloserWAR, v => this.config.GapCloserWAR = v);
            changed |= this.Checkbox("DRK — Shadowstride", this.config.GapCloserDRK, this.defaultConfig.GapCloserDRK, v => this.config.GapCloserDRK = v);
            changed |= this.Checkbox("GNB — Trajectory", this.config.GapCloserGNB, this.defaultConfig.GapCloserGNB, v => this.config.GapCloserGNB = v);
            changed |= this.Checkbox("MNK — Thunderclap", this.config.GapCloserMNK, this.defaultConfig.GapCloserMNK, v => this.config.GapCloserMNK = v);
            changed |= this.Checkbox("DRG — Winged Glide", this.config.GapCloserDRG, this.defaultConfig.GapCloserDRG, v => this.config.GapCloserDRG = v);
            changed |= this.Checkbox("NIN — Shukuchi", this.config.GapCloserNIN, this.defaultConfig.GapCloserNIN, v => this.config.GapCloserNIN = v);
            changed |= this.Checkbox("SAM — Hissatsu: Gyoten", this.config.GapCloserSAM, this.defaultConfig.GapCloserSAM, v => this.config.GapCloserSAM = v);
            changed |= this.Checkbox("DNC — En Avant", this.config.GapCloserDNC, this.defaultConfig.GapCloserDNC, v => this.config.GapCloserDNC = v);
            changed |= this.Checkbox("RPR — Hell's Ingress", this.config.GapCloserRPR, this.defaultConfig.GapCloserRPR, v => this.config.GapCloserRPR = v);
            changed |= this.Checkbox("VPR — Slither", this.config.GapCloserVPR, this.defaultConfig.GapCloserVPR, v => this.config.GapCloserVPR = v);
            ImGui.Unindent();
        }

        if (this.CollapsingCheckbox("Use gap closer to escape danger", this.config.UseEscapeGapCloser, this.defaultConfig.UseEscapeGapCloser, v => { this.config.UseEscapeGapCloser = v; changed = true; }, tooltip: "Leaves danger when BossMod says the landing point is safe.\nGreed mode still requires movement toward BossMod's safe point.\nBLM will not leave Ley Lines.\nHoles in the floor will still absolutely kill you.", icon: FontAwesomeIcon.SkullCrossbones))
        {
            ImGui.Indent();
            changed |= this.Checkbox("MNK — Thunderclap to ally", this.config.EscapeGapCloserMNK, this.defaultConfig.EscapeGapCloserMNK, v => this.config.EscapeGapCloserMNK = v);
            changed |= this.Checkbox("NIN — Shukuchi", this.config.EscapeGapCloserNIN, this.defaultConfig.EscapeGapCloserNIN, v => this.config.EscapeGapCloserNIN = v);
            changed |= this.Checkbox("DNC — En Avant", this.config.EscapeGapCloserDNC, this.defaultConfig.EscapeGapCloserDNC, v => this.config.EscapeGapCloserDNC = v);
            changed |= this.Checkbox("RPR — Hell's Ingress", this.config.EscapeGapCloserRPR, this.defaultConfig.EscapeGapCloserRPR, v => this.config.EscapeGapCloserRPR = v);
            changed |= this.Checkbox("VPR — Slither to ally", this.config.EscapeGapCloserVPR, this.defaultConfig.EscapeGapCloserVPR, v => this.config.EscapeGapCloserVPR = v);
            changed |= this.Checkbox("BLM — Aetherial Manipulation", this.config.EscapeGapCloserBLM, this.defaultConfig.EscapeGapCloserBLM, v => this.config.EscapeGapCloserBLM = v);
            changed |= this.Checkbox("SGE — Icarus", this.config.EscapeGapCloserSGE, this.defaultConfig.EscapeGapCloserSGE, v => this.config.EscapeGapCloserSGE = v);
            changed |= this.Checkbox("PCT — Smudge", this.config.EscapeGapCloserPCT, this.defaultConfig.EscapeGapCloserPCT, v => this.config.EscapeGapCloserPCT = v);
            changed |= this.Checkbox("BLU — Loom", this.config.EscapeGapCloserBLU, this.defaultConfig.EscapeGapCloserBLU, v => this.config.EscapeGapCloserBLU = v);
            ImGui.Unindent();
        }

        changed |= this.Checkbox("Healer: stay near party", this.config.HealerPartyCoverage, this.defaultConfig.HealerPartyCoverage, v => this.config.HealerPartyCoverage = v, "Positions healer to cover the most party members on boss fights.\nOverrides healer max distance.");
        ImGui.Unindent(8f);
        ImGui.Spacing();

        this.DrawSectionHeader("Combat Behavior");
        changed |= this.Combo("Combat style", this.config.CombatStyle, this.defaultConfig.CombatStyle, v => this.config.CombatStyle = v, "Normal moves directly to safety.\nGreed balances uptime against mechanic safety.");
        ImGui.Unindent(8f);
        ImGui.Spacing();

        this.DrawSectionHeader("Feedback");
        changed |= this.Checkbox("Echo command status to chat", this.config.EchoStatusToChat, this.defaultConfig.EchoStatusToChat, v => this.config.EchoStatusToChat = v);
        ImGui.Unindent(8f);
        ImGui.Spacing();

        ImGui.Separator();
        if (ImGui.Button("Reset all"))
        {
            this.config.ResetAll();
            changed = true;
        }

        return changed;
    }

    private bool DrawRangesTab()
    {
        var changed = false;

        changed |= this.Checkbox("Manage range", this.config.ManageRange, this.defaultConfig.ManageRange, v => this.config.ManageRange = v);
        ImGui.Spacing();

        if (!this.config.ManageRange)
            ImGui.BeginDisabled();

        this.DrawSectionHeader("Single Target Distance");
        changed |= this.Checkbox("Single target distance", this.config.RoleBasedRange, this.defaultConfig.RoleBasedRange, v => this.config.RoleBasedRange = v);
        if (!this.config.RoleBasedRange)
            ImGui.BeginDisabled();
        changed |= this.SliderFloat("Melee max distance", this.config.MeleeRange, this.defaultConfig.MeleeRange, Configuration.BossModMinRange, Configuration.BossModMaxRange, v => this.config.MeleeRange = v);
        changed |= this.SliderFloat("Physical ranged max distance", this.config.PhysicalRangedRange, this.defaultConfig.PhysicalRangedRange, Configuration.BossModMinRange, Configuration.BossModMaxRange, v => this.config.PhysicalRangedRange = v);
        if (this.config.HealerPartyCoverage) ImGui.BeginDisabled();
        changed |= this.SliderFloat("Healer max distance", this.config.HealerRange, this.defaultConfig.HealerRange, Configuration.BossModMinRange, Configuration.BossModMaxRange, v => this.config.HealerRange = v);
        if (this.config.HealerPartyCoverage) ImGui.EndDisabled();
        changed |= this.SliderFloat("Magic ranged max distance", this.config.MagicRangedRange, this.defaultConfig.MagicRangedRange, Configuration.BossModMinRange, Configuration.BossModMaxRange, v => this.config.MagicRangedRange = v);
        if (!this.config.RoleBasedRange)
            ImGui.EndDisabled();
        ImGui.Unindent(8f);
        ImGui.Spacing();

        this.DrawSectionHeader("AoE Target Distance");
        changed |= this.Checkbox("AoE target distance", this.config.AoERangeInMultiTarget, this.defaultConfig.AoERangeInMultiTarget, v => this.config.AoERangeInMultiTarget = v);
        if (!this.config.AoERangeInMultiTarget)
            ImGui.BeginDisabled();
        changed |= this.Checkbox("Non-AST healers use melee AoE range", this.config.AoEHealerMeleeRange, this.defaultConfig.AoEHealerMeleeRange, v => this.config.AoEHealerMeleeRange = v);
        changed |= this.SliderFloat("AoE melee max distance", this.config.AoEMeleeRange, this.defaultConfig.AoEMeleeRange, Configuration.BossModMinRange, Configuration.BossModMaxRange, v => this.config.AoEMeleeRange = v);
        changed |= this.SliderFloat("AoE physical ranged max distance", this.config.AoEPhysicalRangedRange, this.defaultConfig.AoEPhysicalRangedRange, Configuration.BossModMinRange, Configuration.BossModMaxRange, v => this.config.AoEPhysicalRangedRange = v);
        if (this.config.HealerPartyCoverage) ImGui.BeginDisabled();
        changed |= this.SliderFloat("AoE healer max distance", this.config.AoEHealerRange, this.defaultConfig.AoEHealerRange, Configuration.BossModMinRange, Configuration.BossModMaxRange, v => this.config.AoEHealerRange = v);
        if (this.config.HealerPartyCoverage) ImGui.EndDisabled();
        changed |= this.SliderFloat("AoE magic ranged max distance", this.config.AoEMagicRangedRange, this.defaultConfig.AoEMagicRangedRange, Configuration.BossModMinRange, Configuration.BossModMaxRange, v => this.config.AoEMagicRangedRange = v);
        changed |= this.SliderInt("AoE enemy threshold", this.config.AoEEnemyThreshold, this.defaultConfig.AoEEnemyThreshold, 1, 10, v => this.config.AoEEnemyThreshold = v);
        if (!this.config.AoERangeInMultiTarget)
            ImGui.EndDisabled();
        ImGui.Unindent(8f);
        ImGui.Spacing();

        if (!this.config.ManageRange)
            ImGui.EndDisabled();

        this.DrawSectionHeader("Forbidden Zone");
        changed |= this.Checkbox("Manage forbidden-zone distance", this.config.ManageForbiddenZoneDistance, this.defaultConfig.ManageForbiddenZoneDistance, v => this.config.ManageForbiddenZoneDistance = v);
        if (!this.config.ManageForbiddenZoneDistance)
            ImGui.BeginDisabled();
        changed |= this.SliderFloat("Preferred distance to forbidden zones", this.config.PreferredForbiddenZoneDistance, this.defaultConfig.PreferredForbiddenZoneDistance, 0f, 3f, v => this.config.PreferredForbiddenZoneDistance = v);
        if (!this.config.ManageForbiddenZoneDistance)
            ImGui.EndDisabled();
        ImGui.Unindent(8f);
        ImGui.Spacing();

        ImGui.Separator();
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

    // Renders a toggle triangle button + checkbox on the same line. Triangle is disabled (and
    // section forced closed) when the parent feature is off. Returns true when expanded.
    private bool CollapsingCheckbox(string label, bool value, bool defaultValue, Action<bool> setter, bool enabled = true, string? tooltip = null, FontAwesomeIcon? icon = null)
    {
        if (!enabled)
            this.openSections.Remove(label);

        var isOpen = this.openSections.Contains(label);
        var arrow = isOpen ? "v" : ">";

        if (!enabled)
            ImGui.BeginDisabled();

        ImGui.PushID($"##{label}arrow");
        if (ImGui.SmallButton(arrow))
        {
            if (isOpen)
                this.openSections.Remove(label);
            else
                this.openSections.Add(label);
        }
        ImGui.PopID();

        if (!enabled)
            ImGui.EndDisabled();

        ImGui.SameLine();
        var current = value;
        if (ImGui.Checkbox(label, ref current))
            setter(current);

        var optionHovered = ImGui.IsItemHovered();
        if (tooltip != null && optionHovered)
            ImGui.SetTooltip(tooltip);

        if (icon != null)
        {
            ImGui.SameLine();
            ImGui.PushFont(UiBuilder.IconFont);
            ImGui.TextUnformatted(icon.Value.ToIconString());
            ImGui.PopFont();
            if (tooltip != null && ImGui.IsItemHovered())
                ImGui.SetTooltip(tooltip);
        }

        if (IsResetRequested(optionHovered))
            setter(defaultValue);

        return isOpen && enabled && value;
    }

    private void DrawEnabledCheckbox(string? dependencyWarningText)
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

    private bool Checkbox(string label, bool value, bool defaultValue, Action<bool> setter, string? tooltip = null)
    {
        var current = value;
        var changed = ImGui.Checkbox(label, ref current);
        var hovered = ImGui.IsItemHovered();
        if (tooltip != null && hovered)
            ImGui.SetTooltip(tooltip);

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

    private bool Combo<T>(string label, T value, T defaultValue, Action<T> setter, string? tooltip = null)
        where T : struct, Enum
    {
        var changed = false;
        ImGui.TextUnformatted(label);
        var labelHovered = ImGui.IsItemHovered();
        if (tooltip != null && labelHovered)
            ImGui.SetTooltip(tooltip);

        ImGui.SetNextItemWidth(-1f);
        if (ImGui.BeginCombo($"##{label}", value.ToString()))
        {
            foreach (var option in Enum.GetValues<T>())
            {
                var selected = EqualityComparer<T>.Default.Equals(value, option);
                if (ImGui.Selectable(option.ToString(), selected))
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
        if (tooltip != null && ImGui.IsItemHovered())
            ImGui.SetTooltip(tooltip);

        if (IsResetRequested(hovered))
        {
            if (EqualityComparer<T>.Default.Equals(value, defaultValue))
                return changed;

            setter(defaultValue);
            return true;
        }

        return changed;
    }

    private bool SliderFloat(string label, float value, float defaultValue, float min, float max, Action<float> setter)
    {
        var id = $"##{label}";
        ImGui.TextUnformatted(label);
        var labelHovered = ImGui.IsItemHovered();
        ImGui.SetNextItemWidth(-1f);

        if (this.editingSliders.Contains(label))
        {
            var input = value;
            ImGui.SetKeyboardFocusHere(0);
            if (ImGui.InputFloat(id, ref input, 0f, 0f, "%.1f", ImGuiInputTextFlags.EnterReturnsTrue))
            {
                this.editingSliders.Remove(label);
                input = Math.Clamp(input, min, max);
                setter(input);
                return true;
            }

            if (IsResetRequested(labelHovered || ImGui.IsItemHovered()))
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
        var changed = ImGui.SliderFloat(id, ref current, min, max, "%.1f");
        var sliderHovered = ImGui.IsItemHovered();
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

        setter(current);
        return true;
    }

    private bool SliderInt(string label, int value, int defaultValue, int min, int max, Action<int> setter)
    {
        var id = $"##{label}";
        ImGui.TextUnformatted(label);
        var labelHovered = ImGui.IsItemHovered();
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

            if (IsResetRequested(labelHovered || ImGui.IsItemHovered()))
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
        var sliderHovered = ImGui.IsItemHovered();
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
}

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
    private readonly Func<string?> dependencyWarning;
    private readonly Func<string?> trueNorthWarning;
    private readonly Action manageTrueNorthEnabled;
    private readonly IKeyState keyState;
    private readonly string iconPath;
    private readonly ISharedImmediateTexture? iconTexture;
    private readonly HashSet<string> editingSliders = [];
    private ConfigPage selectedPage = ConfigPage.General;
    private DateTime copiedDebugStateUntil = DateTime.MinValue;
    private bool backspacePressedThisFrame;
    private bool wasBackspaceDown;

    public ConfigWindow(Configuration config, Action save, Action resetRuntimeState, Action<bool> setEnabled, Func<string> debugState, Func<string?> dependencyWarning, Func<string?> trueNorthWarning, Action manageTrueNorthEnabled, IKeyState keyState, ITextureProvider textureProvider, string iconPath)
        : base("Xel's Combat AI Configuration###XelsCombatAIConfig")
    {
        this.config = config;
        this.save = save;
        this.resetRuntimeState = resetRuntimeState;
        this.setEnabled = setEnabled;
        this.debugState = debugState;
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
            ConfigPage.General => this.DrawGeneralTab(),
            ConfigPage.Movement => this.DrawMovementTab(),
            ConfigPage.AoeAndTrash => this.DrawAoeAndTrashTab(),
            ConfigPage.Positionals => this.DrawPositionalsTab(),
            ConfigPage.JobSpecific => this.DrawJobSpecificTab(),
            ConfigPage.Dashes => this.DrawDashesTab(),
            ConfigPage.Troubleshooting => this.DrawTroubleshootingTab(),
            _ => false
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
        this.DrawNavItem(ConfigPage.JobSpecific, "Job Specific");
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
        changed |= this.Checkbox("Print command messages in chat", this.config.EchoStatusToChat, this.defaultConfig.EchoStatusToChat, v => this.config.EchoStatusToChat = v);
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

        changed |= this.Checkbox("Automate movement", this.config.ManageMovement, this.defaultConfig.ManageMovement, v => this.config.ManageMovement = v);
        changed |= this.Checkbox("Follow party facing during downtime", this.config.ManageSocialTurning, this.defaultConfig.ManageSocialTurning, v => this.config.ManageSocialTurning = v, "During downtime, turns to face with nearby party members when the group lines up.");
        var movementDisabledTooltip = !this.config.ManageMovement ? "Requires Automate movement." : null;
        if (!this.config.ManageMovement)
            ImGui.BeginDisabled();

        changed |= this.Checkbox("Pause when I move", this.config.RespectManualMovement, this.defaultConfig.RespectManualMovement, v => this.config.RespectManualMovement = v, disabledTooltip: movementDisabledTooltip);
        changed |= this.Combo("Movement timing", this.config.CombatStyle, this.defaultConfig.CombatStyle, v => this.config.CombatStyle = v, "Chooses how late to move for mechanics.\nSafe settings move earlier; greedy settings wait longer.", v => v switch
        {
            CombatStyle.Greed => "Greedy",
            CombatStyle.GreedGCD => "Greedy until next GCD",
            CombatStyle.GreedLastMoment => "Last second",
            _ => "Safe first"
        }, movementDisabledTooltip);
        changed |= this.DrawToggleSectionHeader("Avoid danger zones", this.config.ManageForbiddenZoneDistance, this.defaultConfig.ManageForbiddenZoneDistance, v => this.config.ManageForbiddenZoneDistance = v, "Keeps a little more space from AoE edges when a safe option exists.", disabledTooltip: movementDisabledTooltip);
        var forbiddenZoneDisabledTooltip = !this.config.ManageForbiddenZoneDistance ? "Requires Avoid danger zones." : movementDisabledTooltip;
        if (!this.config.ManageForbiddenZoneDistance)
            ImGui.BeginDisabled();
        changed |= this.SliderFloat("Extra danger-zone space", this.config.PreferredForbiddenZoneDistance, this.defaultConfig.PreferredForbiddenZoneDistance, 0f, 3f, v => this.config.PreferredForbiddenZoneDistance = v, disabledTooltip: forbiddenZoneDisabledTooltip);
        if (!this.config.ManageForbiddenZoneDistance)
            ImGui.EndDisabled();
        ImGui.Unindent(8f);
        ImGui.Spacing();

        changed |= this.Checkbox(
            "Stay in healer range (healers only)",
            this.config.ManageHealerCoverageZone,
            this.defaultConfig.ManageHealerCoverageZone,
            v => this.config.ManageHealerCoverageZone = v,
            "Healers only: prefers safe spots where more party members are comfortably in AoE healing range.\nHolds during casts and active BossMod mechanic positioning.",
            movementDisabledTooltip);
        changed |= this.Checkbox(
            "Stand in defensive ground effects",
            this.config.ManageDefensiveGroundZonePositioning,
            this.defaultConfig.ManageDefensiveGroundZonePositioning,
            v => this.config.ManageDefensiveGroundZonePositioning = v,
            "Stands in friendly ground effects when safe, such as Asylum or Sacred Soil.",
            movementDisabledTooltip);
        changed |= this.Checkbox(
            "Stand behind Passage of Arms",
            this.config.ManagePassageOfArmsPositioning,
            this.defaultConfig.ManagePassageOfArmsPositioning,
            v => this.config.ManagePassageOfArmsPositioning = v,
            disabledTooltip: movementDisabledTooltip);
        changed |= this.Checkbox(
            "Avoid standing inside bosses",
            this.config.AvoidStandingInsideEnemies,
            this.defaultConfig.AvoidStandingInsideEnemies,
            v => this.config.AvoidStandingInsideEnemies = v,
            "Avoids favoring positions inside boss-sized hitboxes.",
            movementDisabledTooltip);
        changed |= this.Checkbox(
            "Avoid arena edge",
            this.config.AvoidArenaEdge,
            this.defaultConfig.AvoidArenaEdge,
            v => this.config.AvoidArenaEdge = v,
            disabledTooltip: movementDisabledTooltip);
        if (!this.config.ManageMovement)
            ImGui.EndDisabled();
        ImGui.Spacing();

        return changed;
    }

    private bool DrawAoeAndTrashTab()
    {
        var changed = false;
        var movementDisabledTooltip = !this.config.ManageMovement ? "Requires Automate movement on the Movement tab." : null;

        if (!this.config.ManageMovement)
            ImGui.BeginDisabled();
        changed |= this.Checkbox("Move for better AoE hits", this.config.ManageAoePackPositioning, this.defaultConfig.ManageAoePackPositioning, v => this.config.ManageAoePackPositioning = v, "Moves to a safe spot where your AoE can hit more enemies.\nYields to active BossMod mechanic safety.", movementDisabledTooltip);
        if (!this.config.ManageMovement)
            ImGui.EndDisabled();

        changed |= this.Checkbox("Pick better AoE target", this.config.PickBetterAoeTarget, this.defaultConfig.PickBetterAoeTarget, v => this.config.PickBetterAoeTarget = v);
        changed |= this.Checkbox("Keep a trash target selected", this.config.KeepTrashTargetSelected, this.defaultConfig.KeepTrashTargetSelected, v => this.config.KeepTrashTargetSelected = v);

        return changed;
    }

    private bool DrawPositionalsTab()
    {
        var changed = false;

        changed |= this.DrawToggleSectionHeader("Do positionals", this.config.ManagePositionals, this.defaultConfig.ManagePositionals, v => this.config.ManagePositionals = v);
        var positionalsDisabledTooltip = !this.config.ManagePositionals ? "Requires Do positionals." : null;
        if (!this.config.ManagePositionals)
            ImGui.BeginDisabled();
        changed |= this.Checkbox(
            "Use True North",
            this.config.ManageTrueNorth,
            this.defaultConfig.ManageTrueNorth,
            v =>
            {
                this.config.ManageTrueNorth = v;
                if (v)
                {
                    this.manageTrueNorthEnabled();
                }
            },
            "Uses True North when the correct rear/flank is unsafe or unreachable.",
            positionalsDisabledTooltip);
        if (!this.config.ManagePositionals)
            ImGui.EndDisabled();
        ImGui.Unindent(8f);
        ImGui.Spacing();

        return changed;
    }

    private bool DrawJobSpecificTab()
    {
        var changed = false;

        this.DrawSectionHeader("Black Mage");
        changed |= this.Checkbox("Stay in Ley Lines", this.config.ManageLeylines, this.defaultConfig.ManageLeylines, v => this.config.ManageLeylines = v, "Black Mage: tries to stay in your Ley Lines when safe.\nDoes not place Ley Lines.");
        var leylinesDisabledTooltip = !this.config.ManageLeylines ? "Requires Stay in Ley Lines." : null;
        if (!this.config.ManageLeylines)
            ImGui.BeginDisabled();
        changed |= this.Checkbox("Use Between the Lines", this.config.UseBetweenTheLines, this.defaultConfig.UseBetweenTheLines, v => this.config.UseBetweenTheLines = v, disabledTooltip: leylinesDisabledTooltip);
        changed |= this.Checkbox("Use Retrace", this.config.UseRetrace, this.defaultConfig.UseRetrace, v => this.config.UseRetrace = v, disabledTooltip: leylinesDisabledTooltip);
        changed |= this.Checkbox("Walk back to Ley Lines", this.config.ReturnToLeylines, this.defaultConfig.ReturnToLeylines, v => this.config.ReturnToLeylines = v, "Walks back to Ley Lines when teleport skills are not used or unavailable.", leylinesDisabledTooltip);
        if (!this.config.ManageLeylines)
            ImGui.EndDisabled();
        ImGui.Unindent(8f);
        ImGui.Spacing();

        this.DrawSectionHeader("Red Mage");
        var movementDisabledTooltip = !this.config.ManageMovement ? "Requires Automate movement." : null;
        if (!this.config.ManageMovement)
        {
            ImGui.BeginDisabled();
        }

        changed |= this.Checkbox(
            "Move for melee combo",
            this.config.UseRedMageMeleeComboMovement,
            this.defaultConfig.UseRedMageMeleeComboMovement,
            v => this.config.UseRedMageMeleeComboMovement = v,
            "Red Mage: uses mana, Mana Stacks, and RotationSolver target context to move into enchanted melee combo range.\nMay use Displacement after the finisher when safe.\nDoes not press attacks.",
            movementDisabledTooltip);
        if (!this.config.ManageMovement)
        {
            ImGui.EndDisabled();
        }

        ImGui.Unindent(8f);
        ImGui.Spacing();

        return changed;
    }

    private bool DrawDashesTab()
    {
        var changed = false;

        changed |= this.DrawToggleSectionHeader(
            "Gap closers",
            this.config.UseGapCloser,
            this.defaultConfig.UseGapCloser,
            v => this.config.UseGapCloser = v,
            icon: FontAwesomeIcon.SkullCrossbones,
            iconTooltip: "High risk. Dashes can land in cleaves, snapshots, knockbacks, or arena hazards.\n\nUse only in fights where you accept that risk.\n\nFixed-direction dashes may make a short setup turn before moving.");
        var gapCloserDisabledTooltip = !this.config.UseGapCloser ? "Requires Gap closers." : null;
        if (!this.config.UseGapCloser)
        {
            ImGui.BeginDisabled();
        }

        this.DrawSectionHeader("Jobs allowed to use gap closers");
        if (ImGui.BeginTable("gapCloserJobs", 3, ImGuiTableFlags.SizingStretchSame))
        {
            foreach (var toggle in Configuration.GapCloserJobToggles)
            {
                changed |= this.JobCheckbox(
                    toggle.Label,
                    toggle.Get(this.config),
                    toggle.Get(this.defaultConfig),
                    value => toggle.Set(this.config, value),
                    gapCloserDisabledTooltip);
            }

            ImGui.EndTable();
        }

        ImGui.Unindent(8f);
        changed |= this.SliderFloat(
            "Minimum gap-closer distance",
            this.config.MinimumGapCloserDistance,
            this.defaultConfig.MinimumGapCloserDistance,
            Configuration.MinimumGapCloserDistanceMin,
            Configuration.MinimumGapCloserDistanceMax,
            v => this.config.MinimumGapCloserDistance = v,
            "%.0f",
            tooltip: "Only dashes when it saves at least this much movement.",
            disabledTooltip: gapCloserDisabledTooltip);
        if (!this.config.UseGapCloser)
        {
            ImGui.EndDisabled();
        }

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
            v => this.config.ShowDecisionOverlay = v);
        var overlayDisabledTooltip = !this.config.ShowDecisionOverlay ? "Requires Show movement overlay." : null;
        if (!this.config.ShowDecisionOverlay)
            ImGui.BeginDisabled();
        changed |= this.Checkbox(
            "Show overlay debug HUD",
            this.config.ShowDecisionOverlayHud,
            this.defaultConfig.ShowDecisionOverlayHud,
            v => this.config.ShowDecisionOverlayHud = v,
            disabledTooltip: overlayDisabledTooltip);
        if (!this.config.ShowDecisionOverlay)
            ImGui.EndDisabled();
        ImGui.Unindent(8f);
        ImGui.Spacing();

        this.DrawSectionHeader("Fight Review");
        changed |= this.Checkbox(
            "Write run-review logs",
            this.config.FightReviewLoggingEnabled,
            this.defaultConfig.FightReviewLoggingEnabled,
            v => this.config.FightReviewLoggingEnabled = v,
            "Saves one JSONL log for the current duty or fallback combat.\nKeeps logging when movement control is disabled.\nDowntime is sampled slower to keep logging lightweight.");
        ImGui.Unindent(8f);
        ImGui.Spacing();

        this.DrawSectionHeader("Copy");
        if (ImGui.Button("Copy debug snapshot"))
        {
            ImGui.SetClipboardText(this.debugState());
            this.copiedDebugStateUntil = DateTime.UtcNow.AddSeconds(2);
        }

        if (DateTime.UtcNow < this.copiedDebugStateUntil)
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

        this.DrawInfoIcon(tooltip, disabledTooltip);

        if (!enabled)
            ImGui.EndDisabled();

        if (enabled && (titleHovered || iconHovered) && ImGui.IsMouseClicked(ImGuiMouseButton.Left))
        {
            setter(!value);
            changed = true;
        }

        var hovered = checkboxHovered || titleHovered || iconHovered;

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
        if (IsResetRequested(hovered))
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

    private bool Checkbox(string label, bool value, bool defaultValue, Action<bool> setter, string? tooltip = null, string? disabledTooltip = null, FontAwesomeIcon? icon = null, string? iconTooltip = null)
    {
        var current = value;
        var changed = ImGui.Checkbox(label, ref current);
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

        this.DrawInfoIcon(tooltip, disabledTooltip);

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
        this.DrawInfoIcon(tooltip, disabledTooltip);

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

    private bool DrawInfoIcon(string? tooltip, string? disabledTooltip = null)
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
            DrawWrappedTooltip(FormatInfoTooltip(tooltip, disabledTooltip));
        }

        return hovered;
    }

    private bool SliderFloat(string label, float value, float defaultValue, float min, float max, Action<float> setter, string format = "%.1f", string? tooltip = null, string? disabledTooltip = null)
    {
        var id = $"##{label}";
        ImGui.TextUnformatted(label);
        var labelHovered = ImGui.IsItemHovered();
        this.DrawInfoIcon(tooltip, disabledTooltip);
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

            var inputHovered = ImGui.IsItemHovered();
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

        if (format == "%.0f")
            current = MathF.Round(current);
        setter(current);
        return true;
    }

    private bool SliderInt(string label, int value, int defaultValue, int min, int max, Action<int> setter, string? disabledTooltip = null)
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

            var inputHovered = ImGui.IsItemHovered();
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

    private bool IsItemHoveredAllowDisabled()
    {
        return ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled);
    }

    private static string FormatInfoTooltip(string tooltip, string? disabledTooltip)
    {
        if (disabledTooltip == null)
        {
            return tooltip;
        }

        return $"{tooltip}\n\n{disabledTooltip}";
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
        JobSpecific,
        Dashes,
        Troubleshooting
    }
}

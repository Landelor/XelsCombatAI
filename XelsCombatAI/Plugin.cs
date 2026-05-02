using System;
using System.Collections.Generic;
using System.Linq;
using Dalamud.Game.Command;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Game.Gui.Dtr;
using Dalamud.Interface.Windowing;
using Dalamud.IoC;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using ECommons;
using ECommons.EzSharedDataManager;
using ECommons.GameFunctions;
using FFXIVClientStructs.FFXIV.Client.Game;

namespace XelsCombatAI;

public sealed class Plugin : IDalamudPlugin
{
    private const string CommandName = "/xcai";
    private const string AvaricePositionalStatusKey = "Avarice.PositionalStatus";
    private const uint TrueNorthActionId = 7546;
    private const uint TrueNorthStatusId = 1250;

    private enum RangeRole
    {
        Melee,
        PhysicalRanged,
        Healer,
        MagicRanged
    }

    [PluginService] internal static IDalamudPluginInterface PluginInterface { get; private set; } = null!;
    [PluginService] internal static ICommandManager CommandManager { get; private set; } = null!;
    [PluginService] internal static IFramework Framework { get; private set; } = null!;
    [PluginService] internal static IChatGui ChatGui { get; private set; } = null!;
    [PluginService] internal static IPluginLog Log { get; private set; } = null!;
    [PluginService] internal static IDtrBar DtrBar { get; private set; } = null!;
    [PluginService] internal static ICondition Condition { get; private set; } = null!;
    [PluginService] internal static IClientState ClientState { get; private set; } = null!;
    [PluginService] internal static IObjectTable ObjectTable { get; private set; } = null!;
    [PluginService] internal static ITargetManager TargetManager { get; private set; } = null!;

    private readonly Configuration config;
    private readonly BossModIpc bossMod;
    private readonly WindowSystem windowSystem = new("XelsCombatAI");
    private readonly ConfigWindow configWindow;
    private readonly IDtrBarEntry dtrEntry;
    private Positional lastPositional = Positional.Any;
    private float lastRange = -1f;
    private bool? lastMovement;
    private string? lastForbiddenZoneCushion;
    private string? lastPartyRole;
    private bool? lastLeylinesBetweenTheLines;
    private bool? lastLeylinesRetrace;
    private bool? lastLeylinesGoal;
    private bool initializedPreset;
    private DateTime nextRuntimeUpdate = DateTime.MinValue;

    public Plugin()
    {
        ECommonsMain.Init(PluginInterface, this, Module.ObjectFunctions);

        this.config = PluginInterface.GetPluginConfig() as Configuration ?? new Configuration();
        this.config.Migrate();
        this.config.Clamp();
        this.bossMod = new BossModIpc(PluginInterface);
        this.configWindow = new ConfigWindow(this.config, this.SaveConfig, this.ResetRuntimeCache, enabled => this.TrySetEnabled(enabled), this.GetDependencyWarning);
        this.windowSystem.AddWindow(this.configWindow);
        this.dtrEntry = DtrBar.Get("XelsCombatAI");
        this.dtrEntry.OnClick = this.OnDtrClick;
        this.UpdateDtr();

        CommandManager.AddHandler(CommandName, new CommandInfo(this.OnCommand)
        {
            HelpMessage = "Toggle Xel's Combat AI. Usage: /xcai [on|off|toggle|status|config]"
        });
        Framework.Update += this.OnFrameworkUpdate;
        PluginInterface.UiBuilder.Draw += this.windowSystem.Draw;
        PluginInterface.UiBuilder.OpenConfigUi += this.OpenConfig;
        PluginInterface.UiBuilder.OpenMainUi += this.OpenConfig;
    }

    public void Dispose()
    {
        Framework.Update -= this.OnFrameworkUpdate;
        PluginInterface.UiBuilder.OpenMainUi -= this.OpenConfig;
        PluginInterface.UiBuilder.OpenConfigUi -= this.OpenConfig;
        PluginInterface.UiBuilder.Draw -= this.windowSystem.Draw;
        CommandManager.RemoveHandler(CommandName);
        this.dtrEntry.Remove();
        this.windowSystem.RemoveAllWindows();
        this.configWindow.Dispose();
        ECommonsMain.Dispose();
    }

    private void OnFrameworkUpdate(IFramework framework)
    {
        if (!this.config.Enabled)
        {
            return;
        }

        if (!this.DependenciesAvailable(out var missing))
        {
            this.DisableDueToMissingDependencies(missing);
            return;
        }

        if (!Condition[ConditionFlag.InCombat])
        {
            this.HandleOutOfCombat();
            return;
        }

        if (!this.initializedPreset && !this.InitializePreset())
        {
            return;
        }

        if (DateTime.UtcNow < this.nextRuntimeUpdate)
        {
            return;
        }
        this.nextRuntimeUpdate = DateTime.UtcNow.AddMilliseconds(250);

        this.UpdateRuntimeBossModStrategies();
    }

    private bool InitializePreset()
    {
        try
        {
            if (!this.bossMod.EnsurePreset())
            {
                return false;
            }

            if (!this.bossMod.SetActive(BossModIpc.DefaultPresetName))
            {
                return false;
            }

            if (this.config.ManageMovement && !this.bossMod.SetMovement(BossModIpc.DefaultPresetName, true))
            {
                return false;
            }

            this.initializedPreset = true;
            return true;
        }
        catch (Exception ex)
        {
            Log.Verbose(ex, "Could not initialize BossMod preset yet.");
            return false;
        }
    }

    private void HandleOutOfCombat()
    {
        if (this.initializedPreset && this.lastMovement != false)
        {
            try
            {
                this.SetMovement(false);
            }
            catch (Exception ex)
            {
                Log.Verbose(ex, "Could not disable BossMod movement out of combat.");
            }
        }

        this.initializedPreset = false;
        this.lastPositional = Positional.Any;
        this.lastRange = -1f;
        this.lastPartyRole = null;
    }

    private void UpdateRuntimeBossModStrategies()
    {
        try
        {
            if (this.config.ManageMovement)
            {
                this.SetMovement(true);
            }

            if (this.config.ManageRange)
            {
                this.SetRange(this.CalculateDesiredRange());
            }

            if (this.config.ManageForbiddenZoneDistance)
            {
                this.SetForbiddenZoneCushion(MapForbiddenZoneCushion(this.config.PreferredForbiddenZoneDistance));
            }

            if (this.config.ManagePartyRoleFollow)
            {
                this.SetPartyRole(this.CurrentTargetHasBossModule() ? "None" : "Tank");
            }

            if (this.config.ManagePositionals)
            {
                this.SetPositional(HasTrueNorthCoverage() ? Positional.Any : ReadAvaricePositional());
            }

            this.SetLeylines(
                this.config.ManageLeylines && this.config.UseBetweenTheLines,
                this.config.ManageLeylines && this.config.UseRetrace,
                this.config.ManageLeylines && this.config.ReturnToLeylines);
        }
        catch (Exception ex)
        {
            Log.Verbose(ex, "Could not update BossMod strategies yet.");
            this.initializedPreset = false;
        }
    }

    private void SetPositional(Positional positional)
    {
        if (positional == this.lastPositional)
        {
            return;
        }

        if (this.bossMod.SetPositional(BossModIpc.DefaultPresetName, positional))
        {
            this.lastPositional = positional;
        }
    }

    private void SetRange(float range)
    {
        if (Math.Abs(this.lastRange - range) <= 0.01f)
        {
            return;
        }

        if (this.bossMod.SetRange(BossModIpc.DefaultPresetName, range))
        {
            this.lastRange = range;
        }
    }

    private void SetMovement(bool enabled)
    {
        if (this.lastMovement == enabled)
        {
            return;
        }

        if (this.bossMod.SetMovement(BossModIpc.DefaultPresetName, enabled))
        {
            this.lastMovement = enabled;
        }
    }

    private void SetForbiddenZoneCushion(string cushion)
    {
        if (this.lastForbiddenZoneCushion == cushion)
        {
            return;
        }

        if (this.bossMod.SetForbiddenZoneCushion(BossModIpc.DefaultPresetName, cushion))
        {
            this.lastForbiddenZoneCushion = cushion;
        }
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
        if (this.lastPartyRole == role)
        {
            return;
        }

        if (this.bossMod.SetPartyRole(BossModIpc.DefaultPresetName, role))
        {
            this.lastPartyRole = role;
        }
    }

    private void SetLeylines(bool useBetweenTheLines, bool useRetrace, bool returnToLeylines)
    {
        if (this.lastLeylinesBetweenTheLines != useBetweenTheLines &&
            this.bossMod.SetLeylinesBetweenTheLines(BossModIpc.DefaultPresetName, useBetweenTheLines))
        {
            this.lastLeylinesBetweenTheLines = useBetweenTheLines;
        }

        if (this.lastLeylinesRetrace != useRetrace &&
            this.bossMod.SetLeylinesRetrace(BossModIpc.DefaultPresetName, useRetrace))
        {
            this.lastLeylinesRetrace = useRetrace;
        }

        if (this.lastLeylinesGoal != returnToLeylines &&
            this.bossMod.SetLeylinesGoal(BossModIpc.DefaultPresetName, returnToLeylines))
        {
            this.lastLeylinesGoal = returnToLeylines;
        }
    }

    private float CalculateDesiredRange()
    {
        var rangeRole = GetCurrentRangeRole();
        if (this.config.AoERangeInMultiTarget && TargetManager.Target != null)
        {
            var enemyCount = ObjectFunctions.GetAttackableEnemyCountAroundPoint(TargetManager.Target.Position, this.config.EnemyCountRadius);
            if (enemyCount > this.config.AoEEnemyThreshold)
            {
                var classJobId = ObjectTable.LocalPlayer?.ClassJob.RowId ?? 0;
                if (rangeRole == RangeRole.Melee || (this.config.AoEHealerMeleeRange && classJobId is 24 or 28 or 40))
                    return this.config.AoEMeleeRange;
                if (!this.config.RoleBasedRange)
                    return this.config.AoERangedRange;
                return rangeRole switch
                {
                    RangeRole.PhysicalRanged => this.config.AoEPhysicalRangedRange,
                    RangeRole.Healer => this.config.AoEHealerRange,
                    RangeRole.MagicRanged => this.config.AoEMagicRangedRange,
                    _ => this.config.AoERangedRange
                };
            }
        }

        if (!this.config.RoleBasedRange)
        {
            return this.config.MeleeRange;
        }

        return rangeRole switch
        {
            RangeRole.PhysicalRanged => this.config.PhysicalRangedRange,
            RangeRole.Healer => this.config.HealerRange,
            RangeRole.MagicRanged => this.config.MagicRangedRange,
            _ => this.config.MeleeRange
        };
    }

    private bool CurrentTargetHasBossModule()
    {
        var dataId = TargetManager.Target?.BaseId ?? 0;
        if (dataId == 0)
        {
            return false;
        }

        try
        {
            return this.bossMod.HasModuleByDataId(dataId);
        }
        catch (Exception ex)
        {
            Log.Verbose(ex, "Could not query BossMod module state yet.");
            return false;
        }
    }

    private static RangeRole GetCurrentRangeRole()
    {
        var classJobId = ObjectTable.LocalPlayer?.ClassJob.RowId ?? 0;
        return classJobId is
            5 or 23 or // ARC/BRD
            31 or    // MCH
            38       // DNC
                ? RangeRole.PhysicalRanged
                : classJobId is
                    6 or 24 or // CNJ/WHM
                    28 or    // SCH
                    33 or    // AST
                    40       // SGE
                        ? RangeRole.Healer
                        : classJobId is
                            7 or 25 or // THM/BLM
                            26 or 27 or // ACN/SMN
                            35 or    // RDM
                            36 or    // BLU
                            42       // PCT
                                ? RangeRole.MagicRanged
                                : RangeRole.Melee;
    }

    private static Positional ReadAvaricePositional()
    {
        if (!EzSharedData.TryGet<uint[]>(AvaricePositionalStatusKey, out var status) || status.Length < 2)
        {
            return Positional.Any;
        }

        return status[1] switch
        {
            1 => Positional.Rear,
            2 => Positional.Flank,
            _ => Positional.Any
        };
    }

    private static bool HasTrueNorthCoverage()
    {
        return HasActiveTrueNorth() || GetTrueNorthCharges() > 0;
    }

    private static bool HasActiveTrueNorth()
    {
        return ObjectTable.LocalPlayer?.StatusList.Any(status => status.StatusId == TrueNorthStatusId && status.RemainingTime > 0) == true;
    }

    private static unsafe uint GetTrueNorthCharges()
    {
        try
        {
            return ActionManager.Instance()->GetCurrentCharges(TrueNorthActionId);
        }
        catch (Exception ex)
        {
            Log.Verbose(ex, "Could not read True North charges.");
            return 0;
        }
    }

    private void OnCommand(string command, string arguments)
    {
        var args = arguments.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (args.Length == 0)
        {
            this.TrySetEnabled(!this.config.Enabled);
            return;
        }

        switch (args[0].ToLowerInvariant())
        {
            case "on":
                this.TrySetEnabled(true);
                break;
            case "off":
                this.TrySetEnabled(false, false);
                this.Print("Disabled.");
                break;
            case "toggle":
                this.TrySetEnabled(!this.config.Enabled);
                break;
            case "status":
                this.Print($"Enabled={this.config.Enabled}, Dependencies={(this.GetDependencyWarning() ?? "OK")}, Preset={BossModIpc.DefaultPresetName}, LastPositional={this.lastPositional}, TrueNorthCharges={GetTrueNorthCharges()}, TrueNorthActive={HasActiveTrueNorth()}, Range={this.lastRange:0.0}, Movement={this.lastMovement}, Cushion={this.lastForbiddenZoneCushion}, Role={this.lastPartyRole}, LeylinesBTL={this.lastLeylinesBetweenTheLines}, LeylinesRetrace={this.lastLeylinesRetrace}, LeylinesGoal={this.lastLeylinesGoal}, Initialized={this.initializedPreset}");
                break;
            case "config":
                this.OpenConfig();
                break;
            default:
                this.Print("Usage: /xcai [on|off|toggle|status|config]");
                break;
        }
    }

    private void OpenConfig()
    {
        this.configWindow.IsOpen = true;
    }

    private void SaveConfig()
    {
        this.config.Clamp();
        this.config.Save(PluginInterface);
        this.UpdateDtr();
    }

    private void ToggleEnabled()
    {
        this.TrySetEnabled(!this.config.Enabled);
    }

    private void UpdateDtr()
    {
        this.dtrEntry.Text = $"XCAI: {(this.config.Enabled ? "On" : "Off")}";
        var dependencyWarning = this.GetDependencyWarning();
        this.dtrEntry.Tooltip = dependencyWarning == null
            ? "Left click: toggle Xel's Combat AI\nRight click: open config"
            : $"Cannot enable: {dependencyWarning}\nRight click: open config";
        this.dtrEntry.Shown = true;
    }

    private void OnDtrClick(DtrInteractionEvent interactionEvent)
    {
        if (interactionEvent.ClickType == MouseClickType.Left)
        {
            this.ToggleEnabled();
        }
        else if (interactionEvent.ClickType == MouseClickType.Right)
        {
            this.OpenConfig();
        }
    }

    private void ResetRuntimeCache()
    {
        this.initializedPreset = false;
        this.lastPositional = Positional.Any;
        this.lastRange = -1f;
        this.lastMovement = null;
        this.lastForbiddenZoneCushion = null;
        this.lastPartyRole = null;
        this.lastLeylinesBetweenTheLines = null;
        this.lastLeylinesRetrace = null;
        this.lastLeylinesGoal = null;
    }

    private bool TrySetEnabled(bool enabled, bool warn = true)
    {
        if (enabled && !this.DependenciesAvailable(out var missing))
        {
            this.config.Enabled = false;
            this.ResetRuntimeCache();
            this.SaveConfig();
            if (warn)
            {
                this.WarnMissingDependencies(missing);
            }

            return false;
        }

        this.config.Enabled = enabled;
        if (!enabled)
        {
            this.ResetRuntimeCache();
        }

        this.SaveConfig();
        this.Print(this.config.Enabled ? "Enabled." : "Disabled.");
        return true;
    }

    private void DisableDueToMissingDependencies(string missing)
    {
        this.config.Enabled = false;
        this.ResetRuntimeCache();
        this.SaveConfig();
        this.WarnMissingDependencies(missing);
    }

    private void WarnMissingDependencies(string missing)
    {
        ChatGui.PrintError($"[Xel's Combat AI] Cannot enable: {missing}.");
    }

    private string? GetDependencyWarning()
    {
        return this.DependenciesAvailable(out var missing) ? null : missing;
    }

    private bool DependenciesAvailable(out string missing)
    {
        var missingParts = new List<string>();

        if (!this.IsBossModAvailable())
        {
            missingParts.Add("BossMod Reborn is not loaded or its IPC is unavailable");
        }

        if (!this.IsAvariceAvailable())
        {
            missingParts.Add("Avarice is not loaded");
        }

        missing = string.Join("; ", missingParts);
        return missingParts.Count == 0;
    }

    private bool IsBossModAvailable()
    {
        return this.HasLoadedPlugin("BossModReborn", "BossMod Reborn", "BossMod") && this.bossMod.IsAvailable();
    }

    private bool IsAvariceAvailable()
    {
        return this.HasLoadedPlugin("Avarice");
    }

    private bool HasLoadedPlugin(params string[] names)
    {
        return PluginInterface.InstalledPlugins.Any(plugin =>
            plugin.IsLoaded &&
            names.Any(name =>
                string.Equals(plugin.InternalName, name, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(plugin.Name, name, StringComparison.OrdinalIgnoreCase)));
    }

    private void Print(string message)
    {
        if (this.config.EchoStatusToChat)
        {
            ChatGui.Print($"[Xel's Combat AI] {message}");
        }
    }
}

using System;
using System.IO;
using Dalamud.Game.Command;
using Dalamud.Game.Gui.Dtr;
using Dalamud.Interface.Windowing;
using Dalamud.IoC;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using ECommons;
using XelsCombatAI.Game;

namespace XelsCombatAI;

public sealed class Plugin : IDalamudPlugin
{
    private const string CommandName = "/xcai";

    [PluginService] private static IDalamudPluginInterface PluginInterface { get; set; } = null!;
    [PluginService] private static ICommandManager CommandManager { get; set; } = null!;
    [PluginService] private static IFramework Framework { get; set; } = null!;
    [PluginService] private static IChatGui ChatGui { get; set; } = null!;
    [PluginService] private static IPluginLog Log { get; set; } = null!;
    [PluginService] private static IKeyState KeyState { get; set; } = null!;
    [PluginService] private static IDtrBar DtrBar { get; set; } = null!;
    [PluginService] private static ICondition Condition { get; set; } = null!;
    [PluginService] private static IClientState ClientState { get; set; } = null!;
    [PluginService] private static IGameGui GameGui { get; set; } = null!;
    [PluginService] private static IObjectTable ObjectTable { get; set; } = null!;
    [PluginService] private static ITargetManager TargetManager { get; set; } = null!;
    [PluginService] private static IPartyList PartyList { get; set; } = null!;
    [PluginService] private static ITextureProvider TextureProvider { get; set; } = null!;

    private readonly Configuration config;
    private readonly WindowSystem windowSystem = new("XelsCombatAI");
    private readonly ConfigWindow configWindow;
    private readonly IDtrBarEntry? dtrEntry;
    private readonly DalamudServices services;
    private readonly CombatRuntime runtime;
    private readonly DecisionOverlayController decisionOverlay;
    private readonly JobRangeProvider jobRangeProvider;

    public Plugin()
    {
        ECommonsMain.Init(PluginInterface, this, Module.ObjectFunctions);

        this.services = new DalamudServices(
            PluginInterface,
            CommandManager,
            Framework,
            ChatGui,
            Log,
            KeyState,
            DtrBar,
            Condition,
            ClientState,
            GameGui,
            ObjectTable,
            TargetManager,
            PartyList);

        this.config = PluginInterface.GetPluginConfig() as Configuration ?? new Configuration();
        this.config.Migrate();
        this.config.Clamp();

        var bossMod = new BossModIpc(PluginInterface);
        var bossModSafety = new BossModReflectionSafety(PluginInterface, Log);
        var manualMovement = new ManualMovementInputDetector();
        var rotationSolver = new RotationSolverIpc(PluginInterface);
        var rotationSolverActions = new RotationSolverActionReflection(PluginInterface, Log);
        var dependencyChecker = new DependencyChecker(this.config, this.services, bossMod, rotationSolver);
        this.jobRangeProvider = new JobRangeProvider(this.services);
        this.jobRangeProvider.Initialize();
        var targetUptimePlanner = new TargetUptimePlanner(this.services, bossMod, this.jobRangeProvider);
        BossModPresetController? presetController = null;
        CombatRuntime? runtime = null;
        var positionalsController = new PositionalsController(this.config, this.services, rotationSolver, positional => presetController!.SetPositional(positional), this.UpdateDtr);
        var gapCloserController = new GapCloserController(this.config, this.services, bossMod, bossModSafety);
        var escapeGapCloserController = new EscapeGapCloserController(this.config, this.services, bossModSafety, gapCloserController);
        var aoePackPositioningController = new AoePackPositioningController(this.config, this.services, rotationSolverActions, () => runtime?.AutomatedMovementSuppressed == true, rotationSolver, () => targetUptimePlanner.CurrentTargetHasBossModule(), this.jobRangeProvider);
        var passageOfArmsPositioningController = new PassageOfArmsPositioningController(this.config, this.services, () => runtime?.AutomatedMovementSuppressed == true);
        var partyGravityPositioningController = new PartyGravityPositioningController(this.config, this.services, bossModSafety, () => runtime?.AutomatedMovementSuppressed == true, targetUptimePlanner.ShouldDeferPartyGravityForMeleeUptime);
        var healerAoePositioningController = new HealerAoePositioningController(this.config, this.services, new RotationSolverActionReflection(PluginInterface, Log), () => runtime?.AutomatedMovementSuppressed == true);
        var survivabilityZonePositioningController = new SurvivabilityZonePositioningController(this.config, this.services, () => runtime?.AutomatedMovementSuppressed == true);
        var aggroSafetyController = new AggroSafetyController(this.config, this.services);
        var bossFrontalConeController = new BossFrontalConeController(this.config, this.services, bossMod);
        var arenaEdgePositioningController = new ArenaEdgePositioningController(this.config, this.services);
        var aoeGoalHook = new BossModGoalZoneHook(PluginInterface, this.services, Log, [aggroSafetyController, bossFrontalConeController, aoePackPositioningController, passageOfArmsPositioningController, partyGravityPositioningController, healerAoePositioningController, survivabilityZonePositioningController, arenaEdgePositioningController]);
        presetController = new BossModPresetController(
            this.config,
            this.services,
            bossMod,
            bossModSafety,
            targetUptimePlanner,
            positionalsController,
            gapCloserController,
            escapeGapCloserController);

        runtime = new CombatRuntime(
            this.config,
            this.services,
            dependencyChecker,
            presetController,
            positionalsController,
            bossModSafety,
            aoeGoalHook,
            aoePackPositioningController,
            passageOfArmsPositioningController,
            partyGravityPositioningController,
            healerAoePositioningController,
            survivabilityZonePositioningController,
            aggroSafetyController,
            bossFrontalConeController,
            manualMovement,
            gapCloserController,
            escapeGapCloserController,
            this.jobRangeProvider,
            this.SaveConfig,
            this.UpdateDtr,
            this.Print);
        this.runtime = runtime;
        this.decisionOverlay = new DecisionOverlayController(
            this.config,
            this.services,
            aoePackPositioningController,
            passageOfArmsPositioningController,
            partyGravityPositioningController,
            healerAoePositioningController,
            survivabilityZonePositioningController,
            bossModSafety,
            gapCloserController,
            escapeGapCloserController,
            rotationSolverActions,
            bossFrontalConeController);

        this.configWindow = new ConfigWindow(
            this.config,
            this.SaveConfig,
            this.runtime.ResetRuntimeCache,
            enabled => this.runtime.SetEnabled(enabled),
            () => StatusReporter.BuildDebug(this.config, this.runtime.GetStatus()),
            this.runtime.GetCombatHistory,
            this.runtime.GetDependencyWarning,
            this.runtime.GetTrueNorthWarning,
            this.runtime.EnsureRsrTrueNorthDisabled,
            KeyState,
            TextureProvider,
            Path.Combine(PluginInterface.AssemblyLocation.DirectoryName ?? string.Empty, "icon.png"));

        this.dtrEntry = DtrBar.Get("XelsCombatAI");
        this.dtrEntry.OnClick = this.OnDtrClick;
        if (this.config.ManagePositionals && this.config.ManageTrueNorth)
        {
            this.runtime.EnsureRsrTrueNorthDisabled();
        }
        this.windowSystem.AddWindow(this.configWindow);
        this.UpdateDtr();

        CommandManager.AddHandler(CommandName, new CommandInfo(this.OnCommand)
        {
            HelpMessage = "Toggle Xel's Combat AI. Usage: /xcai [on|off|toggle|config]"
        });
        Framework.Update += this.runtime.OnFrameworkUpdate;
        PluginInterface.UiBuilder.Draw += this.decisionOverlay.Draw;
        PluginInterface.UiBuilder.Draw += this.windowSystem.Draw;
        PluginInterface.UiBuilder.OpenConfigUi += this.OpenConfig;
        PluginInterface.UiBuilder.OpenMainUi += this.OpenConfig;
    }

    public void Dispose()
    {
        this.runtime.DisposeRuntime();
        jobRangeProvider.Dispose();
        Framework.Update -= this.runtime.OnFrameworkUpdate;
        PluginInterface.UiBuilder.Draw -= this.decisionOverlay.Draw;
        PluginInterface.UiBuilder.OpenMainUi -= this.OpenConfig;
        PluginInterface.UiBuilder.OpenConfigUi -= this.OpenConfig;
        PluginInterface.UiBuilder.Draw -= this.windowSystem.Draw;
        CommandManager.RemoveHandler(CommandName);
        this.dtrEntry?.Remove();
        this.windowSystem.RemoveAllWindows();
        this.configWindow.Dispose();
        ECommonsMain.Dispose();
    }

    private void OnCommand(string command, string arguments)
    {
        var args = arguments.Split(' ', System.StringSplitOptions.RemoveEmptyEntries | System.StringSplitOptions.TrimEntries);
        if (args.Length == 0)
        {
            this.runtime.SetEnabled(!this.config.Enabled);
            return;
        }

        switch (args[0].ToLowerInvariant())
        {
            case "on":
                this.runtime.SetEnabled(true);
                break;
            case "off":
                this.runtime.SetEnabled(false, false);
                this.Print("Disabled.");
                break;
            case "toggle":
                this.runtime.SetEnabled(!this.config.Enabled);
                break;
            case "config":
                this.OpenConfig();
                break;
            default:
                this.Print("Usage: /xcai [on|off|toggle|config]");
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
        this.runtime.SetEnabled(!this.config.Enabled);
    }

    private void UpdateDtr()
    {
        if (this.dtrEntry == null)
        {
            return;
        }

        this.dtrEntry.Text = $"XCAI: {(this.config.Enabled ? "On" : "Off")}";
        var dependencyWarning = this.runtime.GetDependencyWarning();
        var trueNorthWarning = this.runtime.GetTrueNorthWarning();
        this.dtrEntry.Tooltip = dependencyWarning == null
            ? "Left click: toggle Xel's Combat AI\nRight click: open config"
            : $"Waiting for: {dependencyWarning}\nRight click: open config";
        if (trueNorthWarning != null)
        {
            this.dtrEntry.Tooltip += $"\nWarning: {trueNorthWarning}";
        }
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

    private void Print(string message)
    {
        if (this.config.EchoStatusToChat)
        {
            ChatGui.Print($"[Xel's Combat AI] {message}");
        }
    }
}

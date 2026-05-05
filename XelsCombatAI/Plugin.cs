using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Dalamud.Game.Command;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Game.ClientState.Objects.Enums;
using Dalamud.Game.ClientState.Objects.Types;
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
    private const float MeleeActionRange = 3f;
    private const float HealerCoverageAttackRange = 25f;
    private const float GapCloserMaxRange = 20f;
    private const float GapCloserDestinationMeleeRange = 2.6f;
    private const float FixedForwardGapCloserRange = 15f;
    private const uint TrueNorthActionId = 7546;
    private const uint TrueNorthStatusId = 1250;
    private const uint CircleOfPowerStatusId = 738;
    private const uint PaladinInterveneActionId = 16461;
    private const uint WarriorOnslaughtActionId = 7386;
    private const uint DarkKnightShadowstrideActionId = 36926;
    private const uint GunbreakerTrajectoryActionId = 36934;
    private const uint MonkThunderclapActionId = 25762;
    private const uint DragoonWingedGlideActionId = 36951;
    private const uint NinjaShukuchiActionId = 2262;
    private const uint SamuraiGyotenActionId = 7492;
    private const uint ReaperHellsIngressActionId = 24401;
    private const uint ReaperRegressActionId = 24403;
    private const uint ReaperHellsgatePortalDataId = 0x4C3u;
    private const uint ViperSlitherActionId = 34646;
    private const uint BlackMageAetherialManipulationActionId = 155;
    private const uint SageIcarusActionId = 24295;
    private const uint PictomancerSmudgeActionId = 34684;
    private const uint BlueMageLoomActionId = 11401;
    private const uint DancerEnAvantActionId = 16010;
    private static readonly float[] EscapeLocationRadii = [8f, 12f, 16f, 20f];

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
    [PluginService] internal static IKeyState KeyState { get; private set; } = null!;
    [PluginService] internal static IDtrBar DtrBar { get; private set; } = null!;
    [PluginService] internal static ICondition Condition { get; private set; } = null!;
    [PluginService] internal static IClientState ClientState { get; private set; } = null!;
    [PluginService] internal static IObjectTable ObjectTable { get; private set; } = null!;
    [PluginService] internal static ITargetManager TargetManager { get; private set; } = null!;
    [PluginService] internal static IPartyList PartyList { get; private set; } = null!;

    private readonly Configuration config;
    private readonly BossModIpc bossMod;
    private readonly BossModReflectionSafety bossModSafety;
    private readonly RotationSolverIpc rotationSolver;
    private readonly WindowSystem windowSystem = new("XelsCombatAI");
    private readonly ConfigWindow configWindow;
    private readonly IDtrBarEntry? dtrEntry;
    private Positional lastPositional = Positional.Any;
    private float lastRange = -1f;
    private bool? lastMovement;
    private string? lastMovementRangeStrategy;
    private string? lastForbiddenZoneCushion;
    private string? lastPartyRole;
    private bool? lastLeylinesBetweenTheLines;
    private bool? lastLeylinesRetrace;
    private bool? lastLeylinesGoal;
    private bool? lastMonkThunderclap;
    private bool? lastDragoonWingedGlide;
    private bool? lastNinjaShukuchi;
    private bool? lastViperSlither;
    private bool? lastHealerStayNearParty;
    private bool? lastHealerHeal;
    private bool? lastHealerEsuna;
    private bool? lastHealerOutOfCombat;
    private string? lastHealerRaise;
    private bool? rsrTrueNorthDisabled;
    private bool? trueNorthStrategy;
    private bool initializedPreset;
    private bool wasDead;
    private DateTime nextRuntimeUpdate = DateTime.MinValue;
    private DateTime nextGapCloserAttempt = DateTime.MinValue;
    private DateTime nextEscapeGapCloserAttempt = DateTime.MinValue;
    private string lastGapCloserSafety = "not checked";
    private string lastEscapeGapCloserSafety = "not checked";
    private string? lastMissingDependencies;

    public Plugin()
    {
        ECommonsMain.Init(PluginInterface, this, Module.ObjectFunctions);

        this.config = PluginInterface.GetPluginConfig() as Configuration ?? new Configuration();
        this.config.Migrate();
        this.config.Clamp();
        this.bossMod = new BossModIpc(PluginInterface);
        this.bossModSafety = new BossModReflectionSafety(PluginInterface);
        this.rotationSolver = new RotationSolverIpc();
        this.configWindow = new ConfigWindow(this.config, this.SaveConfig, this.ResetRuntimeCache, enabled => this.TrySetEnabled(enabled), this.GetDependencyWarning, this.GetTrueNorthWarning, this.EnsureRsrTrueNorthDisabled);
        this.dtrEntry = DtrBar.Get("XelsCombatAI");
        this.dtrEntry.OnClick = this.OnDtrClick;
        if (this.config.ManagePositionals && this.config.ManageTrueNorth)
        {
            this.EnsureRsrTrueNorthDisabled();
        }
        this.windowSystem.AddWindow(this.configWindow);
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
        if (this.config.Enabled)
        {
            this.DeactivateBossModPreset();
        }

        Framework.Update -= this.OnFrameworkUpdate;
        PluginInterface.UiBuilder.OpenMainUi -= this.OpenConfig;
        PluginInterface.UiBuilder.OpenConfigUi -= this.OpenConfig;
        PluginInterface.UiBuilder.Draw -= this.windowSystem.Draw;
        CommandManager.RemoveHandler(CommandName);
        this.dtrEntry?.Remove();
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
            this.WaitForDependencies(missing);
            return;
        }

        this.ClearDependencyWaitState();

        if (!Condition[ConditionFlag.InCombat])
        {
            this.HandleOutOfCombat();
            return;
        }

        // BMR sets the preset to ForceDisable (name="") on death (ClearPresetOnDeath=true default).
        // Detect the dead→alive transition and re-initialize so strategies are re-applied.
        var isDead = Condition[ConditionFlag.Unconscious];
        if (this.wasDead && !isDead)
            this.ResetRuntimeCache();
        this.wasDead = isDead;

        if (DateTime.UtcNow < this.nextRuntimeUpdate)
        {
            return;
        }
        this.nextRuntimeUpdate = DateTime.UtcNow.AddMilliseconds(250);

        if (!this.initializedPreset && !this.InitializePreset())
        {
            return;
        }

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
        if (this.initializedPreset)
        {
            this.DeactivateBossModPreset();
            this.ResetRuntimeCache();
        }
    }

    private void DeactivateBossModPreset()
    {
        try
        {
            var presetName = BossModIpc.DefaultPresetName;

            this.bossMod.SetMovement(presetName, false);
            this.bossMod.SetMovementRangeStrategy(presetName, "Any");
            this.bossMod.SetPositional(presetName, Positional.Any);
            this.bossMod.SetPartyRole(presetName, "None");
            this.bossMod.SetLeylinesBetweenTheLines(presetName, false);
            this.bossMod.SetLeylinesRetrace(presetName, false);
            this.bossMod.SetLeylinesGoal(presetName, false);
            this.bossMod.SetMonkThunderclap(presetName, false);
            this.bossMod.SetDragoonWingedGlide(presetName, false);
            this.bossMod.SetNinjaShukuchi(presetName, false);
            this.bossMod.SetViperSlither(presetName, false);
            this.bossMod.SetHealerStayNearParty(presetName, false);
            this.bossMod.SetHealerHeal(presetName, false);
            this.bossMod.SetHealerEsuna(presetName, false);
            this.bossMod.SetHealerOutOfCombat(presetName, false);
            this.bossMod.SetHealerRaise(presetName, "None");

            if (this.bossMod.GetActive() == presetName)
            {
                this.bossMod.ClearActive();
            }
        }
        catch (Exception ex)
        {
            Log.Verbose(ex, "Could not deactivate BossMod preset.");
        }
    }

    private void UpdateRuntimeBossModStrategies()
    {
        try
        {
            if (this.config.ManageRange)
            {
                if (this.config.HealerPartyCoverage &&
                    GetCurrentRangeRole() == RangeRole.Healer &&
                    this.CurrentTargetHasBossModule() &&
                    !this.IsAoEMultiTargetActive())
                    this.SetRange(HealerCoverageAttackRange);
                else
                    this.SetRange(this.CalculateDesiredRange());
            }

            if (this.config.ManageForbiddenZoneDistance)
            {
                this.SetForbiddenZoneCushion(MapForbiddenZoneCushion(this.config.PreferredForbiddenZoneDistance));
            }

            this.SetMovementRangeStrategy(MapCombatStyle(this.config.CombatStyle));

            if (this.config.ManagePartyRoleFollow)
            {
                this.SetPartyRole(this.CurrentTargetHasBossModule() ? "None" : "Tank");
            }

            if (this.config.ManagePositionals)
            {
                if (this.config.ManageTrueNorth)
                {
                    this.EnsureRsrTrueNorthDisabled();
                    var positional = ReadAvaricePositional();
                    if (positional == Positional.Any)
                    {
                        this.trueNorthStrategy = null;
                        this.SetPositional(Positional.Any);
                    }
                    else
                    {
                        if (this.trueNorthStrategy == null)
                            this.trueNorthStrategy = HasActiveTrueNorth() || GetTrueNorthCharges() > 0;
                        if (this.trueNorthStrategy == true)
                        {
                            TryUseTrueNorth(positional);
                            var pending = !HasActiveTrueNorth() && !IsOutsideMeleeRange();
                            this.SetPositional(Positional.Any);
                            if (pending && IsOutsideMeleeRange()) return;
                        }
                        else
                        {
                            this.SetPositional(positional);
                        }
                    }
                }
                else
                {
                    this.SetPositional(HasTrueNorthCoverage() ? Positional.Any : ReadAvaricePositional());
                }
            }

            if (this.config.ManageMovement)
            {
                this.SetMovement(true);
            }

            this.SetLeylines(
                this.config.ManageLeylines && this.config.UseBetweenTheLines,
                this.config.ManageLeylines && this.config.UseRetrace,
                this.config.ManageLeylines && this.config.ReturnToLeylines);

            this.SetHealerAi();
            this.SetGapClosers();
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

    private void EnsureRsrTrueNorthDisabled()
    {
        if (this.rsrTrueNorthDisabled != null)
        {
            return;
        }

        try
        {
            this.rotationSolver.DisableAutoTrueNorth();
            this.rsrTrueNorthDisabled = true;
            Log.Verbose("Disabled Rotation Solver Reborn Auto True North.");
        }
        catch (Exception ex)
        {
            this.rsrTrueNorthDisabled = false;
            Log.Verbose(ex, "Could not disable Rotation Solver Reborn Auto True North.");
            this.Print("Warning: Manage True North is enabled, but Rotation Solver Reborn Auto True North could not be disabled.");
            this.UpdateDtr();
        }
    }

    private static unsafe bool TryUseTrueNorth(Positional positional)
    {
        if (positional == Positional.Any) return false;
        if (GetCurrentRangeRole() != RangeRole.Melee) return false;
        if (HasActiveTrueNorth()) return false;
        if (GetTrueNorthCharges() == 0) return false;
        if (IsOutsideMeleeRange()) return false;

        if (ActionManager.Instance()->AnimationLock > 0) return false;
        if (ObjectTable.LocalPlayer?.IsCasting == true) return false;
        if (ActionManager.Instance()->GetActionStatus(ActionType.Action, TrueNorthActionId) != 0) return false;

        ActionManager.Instance()->UseAction(ActionType.Action, TrueNorthActionId);
        return true;
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

    private void SetMovementRangeStrategy(string strategy)
    {
        if (this.lastMovementRangeStrategy == strategy)
        {
            return;
        }

        if (this.bossMod.SetMovementRangeStrategy(BossModIpc.DefaultPresetName, strategy))
        {
            this.lastMovementRangeStrategy = strategy;
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

    private void SetHealerAi()
    {
        var presetName = BossModIpc.DefaultPresetName;
        var stayNearParty = this.config.HealerPartyCoverage &&
                            GetCurrentRangeRole() == RangeRole.Healer &&
                            this.CurrentTargetHasBossModule();

        if (this.lastHealerRaise != "None" && this.bossMod.SetHealerRaise(presetName, "None"))
        {
            this.lastHealerRaise = "None";
        }

        if (this.lastHealerHeal != false && this.bossMod.SetHealerHeal(presetName, false))
        {
            this.lastHealerHeal = false;
        }

        if (this.lastHealerEsuna != false && this.bossMod.SetHealerEsuna(presetName, false))
        {
            this.lastHealerEsuna = false;
        }

        if (this.lastHealerOutOfCombat != false && this.bossMod.SetHealerOutOfCombat(presetName, false))
        {
            this.lastHealerOutOfCombat = false;
        }

        if (this.lastHealerStayNearParty != stayNearParty &&
            this.bossMod.SetHealerStayNearParty(presetName, stayNearParty))
        {
            this.lastHealerStayNearParty = stayNearParty;
        }
    }

    private void SetGapClosers()
    {
        var presetName = BossModIpc.DefaultPresetName;

        this.SetGapCloser(ref this.lastMonkThunderclap, false, value => this.bossMod.SetMonkThunderclap(presetName, value));
        this.SetGapCloser(ref this.lastDragoonWingedGlide, false, value => this.bossMod.SetDragoonWingedGlide(presetName, value));
        this.SetGapCloser(ref this.lastNinjaShukuchi, false, value => this.bossMod.SetNinjaShukuchi(presetName, value));
        this.SetGapCloser(ref this.lastViperSlither, false, value => this.bossMod.SetViperSlither(presetName, value));

        if (this.config.UseEscapeGapCloser && this.TryUseEscapeGapCloser())
        {
            return;
        }

        if (this.config.UseGapCloser)
        {
            this.TryUseGapCloser();
        }
    }

    private void SetGapCloser(ref bool? last, bool enabled, Func<bool, bool> setter)
    {
        if (last == enabled)
        {
            return;
        }

        if (setter(enabled))
        {
            last = enabled;
        }
    }

    private unsafe bool TryUseGapCloser()
    {
        if (DateTime.UtcNow < this.nextGapCloserAttempt)
        {
            return false;
        }

        this.nextGapCloserAttempt = DateTime.UtcNow.AddMilliseconds(250);

        var player = ObjectTable.LocalPlayer;
        var target = TargetManager.Target;
        if (player == null || target == null)
        {
            this.lastGapCloserSafety = "missing player or target";
            return false;
        }

        if (target is not IBattleNpc battleNpc || battleNpc.BattleNpcKind != BattleNpcSubKind.Combatant)
        {
            this.lastGapCloserSafety = "target is not attackable";
            return false;
        }

        if (player.IsCasting || ActionManager.Instance()->AnimationLock > 0)
        {
            this.lastGapCloserSafety = "player busy";
            return false;
        }

        var distanceToHitbox = DistanceToHitbox(player.Position, player.HitboxRadius, target.Position, target.HitboxRadius);

        // In a light party or larger (4+ members), redirect to the enemy closest to the tank on
        // trash pulls, but only when a gap close is actually needed (current target out of melee
        // range). On bosses (target has a BossMod module), keep the current target unchanged.
        if (distanceToHitbox > MeleeActionRange && PartyList.Count >= 4)
        {
            var targetIsBoss = target is IBattleNpc bossCheck && this.bossMod.HasModuleByDataId(bossCheck.BaseId);
            if (!targetIsBoss)
            {
                var bestTrash = FindTrashGapCloserTarget(player);
                if (bestTrash != null)
                {
                    target = bestTrash;
                    distanceToHitbox = DistanceToHitbox(player.Position, player.HitboxRadius, target.Position, target.HitboxRadius);
                }
            }
        }
        if (distanceToHitbox <= MeleeActionRange || distanceToHitbox > GapCloserMaxRange)
        {
            this.lastGapCloserSafety = "target not in gap closer range";
            return false;
        }

        var classJobId = player.ClassJob.RowId;
        return classJobId switch
        {
            1 or 19 when this.config.GapCloserPLD => this.TryUseTargetGapCloser(PaladinInterveneActionId, distanceToHitbox, target),
            3 or 21 when this.config.GapCloserWAR => this.TryUseTargetGapCloser(WarriorOnslaughtActionId, distanceToHitbox, target),
            32 when this.config.GapCloserDRK => this.TryUseTargetGapCloser(DarkKnightShadowstrideActionId, distanceToHitbox, target),
            37 when this.config.GapCloserGNB => this.TryUseTargetGapCloser(GunbreakerTrajectoryActionId, distanceToHitbox, target),
            2 or 20 when this.config.GapCloserMNK => this.TryUseTargetGapCloser(MonkThunderclapActionId, distanceToHitbox, target),
            4 or 22 when this.config.GapCloserDRG => this.TryUseTargetGapCloser(DragoonWingedGlideActionId, distanceToHitbox, target),
            29 or 30 when this.config.GapCloserNIN => this.TryUseNinjaShukuchi(target),
            34 when this.config.GapCloserSAM => this.TryUseTargetGapCloser(SamuraiGyotenActionId, distanceToHitbox, target),
            38 when this.config.GapCloserDNC => this.TryUseForwardGapCloser(DancerEnAvantActionId, distanceToHitbox),
            39 when this.config.GapCloserRPR => this.TryUseReaperRegress(ref this.lastGapCloserSafety, distanceToHitbox) || this.TryUseForwardGapCloser(ReaperHellsIngressActionId, distanceToHitbox),
            41 when this.config.GapCloserVPR => this.TryUseTargetGapCloser(ViperSlitherActionId, distanceToHitbox, target),
            _ => false
        };
    }

    private unsafe bool TryUseEscapeGapCloser()
    {
        if (DateTime.UtcNow < this.nextEscapeGapCloserAttempt)
        {
            return false;
        }

        this.nextEscapeGapCloserAttempt = DateTime.UtcNow.AddMilliseconds(250);

        var player = ObjectTable.LocalPlayer;
        if (player == null)
        {
            this.lastEscapeGapCloserSafety = "missing player";
            return false;
        }

        if (player.IsCasting || ActionManager.Instance()->AnimationLock > 0)
        {
            this.lastEscapeGapCloserSafety = "player busy";
            return false;
        }

        var classJobId = player.ClassJob.RowId;
        if (this.config.CombatStyle == CombatStyle.Greed && !CanUseEscapeGapCloserInGreed(classJobId))
        {
            this.lastEscapeGapCloserSafety = "disabled in Greed mode";
            return false;
        }

        if (this.config.CombatStyle == CombatStyle.Greed && classJobId == 25 && HasActiveCircleOfPower())
        {
            this.lastEscapeGapCloserSafety = "disabled in Greed mode while in Ley Lines";
            return false;
        }

        if (!this.bossModSafety.TryGetSafeMovementIntent(player.Position, out _, out var intentReason))
        {
            this.lastEscapeGapCloserSafety = intentReason;
            return false;
        }

        if (!this.bossModSafety.TryIsPositionSafe(player.Position, out var currentSafe, out var currentReason))
        {
            this.lastEscapeGapCloserSafety = currentReason;
            return false;
        }

        if (currentSafe)
        {
            this.lastEscapeGapCloserSafety = "current position safe";
            return false;
        }

        return classJobId switch
        {
            2 or 20 when this.config.EscapeGapCloserMNK => this.TryUseFriendlyEscapeGapCloser(MonkThunderclapActionId, GapCloserMaxRange),
            25 when this.config.EscapeGapCloserBLM => this.TryUseFriendlyEscapeGapCloser(BlackMageAetherialManipulationActionId, 25f),
            29 or 30 when this.config.EscapeGapCloserNIN => this.TryUseLocationEscapeGapCloser(NinjaShukuchiActionId, GapCloserMaxRange, "Shukuchi"),
            36 when this.config.EscapeGapCloserBLU => this.TryUseLocationEscapeGapCloser(BlueMageLoomActionId, 15f, "Loom"),
            39 when this.config.EscapeGapCloserRPR => this.TryUseReaperRegress(ref this.lastEscapeGapCloserSafety) || this.TryUseForwardEscapeGapCloser(ReaperHellsIngressActionId),
            40 when this.config.EscapeGapCloserSGE => this.TryUseFriendlyEscapeGapCloser(SageIcarusActionId, 25f),
            41 when this.config.EscapeGapCloserVPR => this.TryUseFriendlyEscapeGapCloser(ViperSlitherActionId, GapCloserMaxRange),
            38 when this.config.EscapeGapCloserDNC => this.TryUseForwardEscapeGapCloser(DancerEnAvantActionId),
            42 when this.config.EscapeGapCloserPCT => this.TryUseForwardEscapeGapCloser(PictomancerSmudgeActionId),
            _ => false
        };
    }

    private unsafe bool TryUseFriendlyEscapeGapCloser(uint actionId, float maxRange)
    {
        var player = ObjectTable.LocalPlayer;
        if (player == null)
        {
            return false;
        }

        if (!CanUseAction(actionId))
        {
            this.lastEscapeGapCloserSafety = "action unavailable";
            return false;
        }

        foreach (var ally in this.EnumerateFriendlyEscapeTargets(player, maxRange))
        {
            if (!this.bossModSafety.TryIsDashSafe(player.Position, ally.Position, out var reason))
            {
                this.lastEscapeGapCloserSafety = reason;
                continue;
            }

            var used = ActionManager.Instance()->UseAction(ActionType.Action, actionId, ally.GameObjectId);
            if (used)
            {
                this.lastEscapeGapCloserSafety = $"used {actionId} on ally";
                return true;
            }

            this.lastEscapeGapCloserSafety = $"failed to use {actionId} on ally";
        }

        if (string.IsNullOrEmpty(this.lastEscapeGapCloserSafety) || this.lastEscapeGapCloserSafety == "current position safe")
        {
            this.lastEscapeGapCloserSafety = "no safe ally found";
        }

        return false;
    }

    private unsafe bool TryUseLocationEscapeGapCloser(uint actionId, float maxRange, string actionName)
    {
        var player = ObjectTable.LocalPlayer;
        if (player == null)
        {
            return false;
        }

        if (!CanUseAction(actionId))
        {
            this.lastEscapeGapCloserSafety = "action unavailable";
            return false;
        }

        foreach (var candidate in this.EnumerateEscapeLocationCandidates(player.Position, maxRange))
        {
            if (!this.bossModSafety.TryIsDashSafe(player.Position, candidate, out var reason))
            {
                this.lastEscapeGapCloserSafety = reason;
                continue;
            }

            var location = candidate;
            var used = ActionManager.Instance()->UseActionLocation(ActionType.Action, actionId, player.GameObjectId, &location);
            this.lastEscapeGapCloserSafety = used ? $"used escape {actionName}" : $"failed to use escape {actionName}";
            return used;
        }

        if (string.IsNullOrEmpty(this.lastEscapeGapCloserSafety) || this.lastEscapeGapCloserSafety == "current position safe")
        {
            this.lastEscapeGapCloserSafety = $"no safe {actionName} escape destination";
        }

        return false;
    }

    private unsafe bool TryUseForwardEscapeGapCloser(uint actionId)
    {
        var player = ObjectTable.LocalPlayer;
        if (player == null)
        {
            return false;
        }

        if (!CanUseAction(actionId))
        {
            this.lastEscapeGapCloserSafety = "action unavailable";
            return false;
        }

        var destination = player.Position + RotationToDirection(player.Rotation) * FixedForwardGapCloserRange;
        if (!this.bossModSafety.TryIsDashSafe(player.Position, destination, out var reason))
        {
            this.lastEscapeGapCloserSafety = reason;
            return false;
        }

        var used = ActionManager.Instance()->UseAction(ActionType.Action, actionId, player.GameObjectId);
        this.lastEscapeGapCloserSafety = used ? $"used {actionId}" : $"failed to use {actionId}";
        return used;
    }

    private static bool CanUseEscapeGapCloserInGreed(uint classJobId)
    {
        return classJobId is 25 or 36 or 38 or 40 or 42;
    }

    private unsafe bool TryUseTargetGapCloser(uint actionId, float distanceToHitbox, IGameObject target)
    {
        var player = ObjectTable.LocalPlayer;
        if (player == null)
        {
            return false;
        }

        if (!CanUseAction(actionId))
        {
            this.lastGapCloserSafety = "action unavailable";
            return false;
        }

        if (!TryCalculateTargetDashDestination(player.Position, target.Position, distanceToHitbox, out var destination))
        {
            this.lastGapCloserSafety = "could not calculate dash destination";
            return false;
        }

        if (!this.bossModSafety.TryIsDashSafe(player.Position, destination, out var reason))
        {
            this.lastGapCloserSafety = reason;
            return false;
        }

        var used = ActionManager.Instance()->UseAction(ActionType.Action, actionId, target.GameObjectId);
        this.lastGapCloserSafety = used ? $"used {actionId}" : $"failed to use {actionId}";
        return used;
    }

    private static IGameObject? TryFindReaperPortal(IGameObject player)
    {
        foreach (var obj in ObjectTable)
        {
            if (obj.BaseId == ReaperHellsgatePortalDataId && obj.OwnerId == player.GameObjectId)
            {
                return obj;
            }
        }

        return null;
    }

    private unsafe bool TryUseReaperRegress(ref string lastSafety, float distanceToHitboxRequired = 0f)
    {
        var player = ObjectTable.LocalPlayer;
        if (player == null)
        {
            return false;
        }

        if (!CanUseAction(ReaperRegressActionId))
        {
            lastSafety = "Regress unavailable";
            return false;
        }

        var portal = TryFindReaperPortal(player);
        if (portal == null)
        {
            lastSafety = "Regress: no portal found";
            return false;
        }

        var portalPosition = portal.Position;

        if (distanceToHitboxRequired > 0f)
        {
            var target = TargetManager.Target;
            if (target == null)
            {
                lastSafety = "Regress: no target";
                return false;
            }

            var regressDistanceToHitbox = DistanceToHitbox(portalPosition, player.HitboxRadius, target.Position, target.HitboxRadius);
            if (regressDistanceToHitbox >= distanceToHitboxRequired || regressDistanceToHitbox > MeleeActionRange + 1f)
            {
                lastSafety = "Regress: portal would not re-engage";
                return false;
            }
        }

        if (!this.bossModSafety.TryIsDashSafe(player.Position, portalPosition, out var reason))
        {
            lastSafety = reason;
            return false;
        }

        var location = portalPosition;
        var used = ActionManager.Instance()->UseActionLocation(ActionType.Action, ReaperRegressActionId, player.GameObjectId, &location);
        lastSafety = used ? "used Regress" : "failed to use Regress";
        return used;
    }

    private unsafe bool TryUseForwardGapCloser(uint actionId, float distanceToHitbox)
    {
        var player = ObjectTable.LocalPlayer;
        var target = TargetManager.Target;
        if (player == null || target == null)
        {
            return false;
        }

        if (!CanUseAction(actionId))
        {
            this.lastGapCloserSafety = "action unavailable";
            return false;
        }

        var forward = RotationToDirection(player.Rotation);
        var toTarget = target.Position - player.Position;
        toTarget.Y = 0;
        if (toTarget.LengthSquared() <= 0.0001f)
        {
            this.lastGapCloserSafety = "could not calculate target direction";
            return false;
        }

        var targetDirection = Vector3.Normalize(toTarget);
        if (Vector3.Dot(forward, targetDirection) < 0.85f)
        {
            this.lastGapCloserSafety = "target not in front for fixed dash";
            return false;
        }

        var destination = player.Position + forward * FixedForwardGapCloserRange;
        var destinationDistanceToHitbox = DistanceToHitbox(destination, player.HitboxRadius, target.Position, target.HitboxRadius);
        if (destinationDistanceToHitbox >= distanceToHitbox || destinationDistanceToHitbox > MeleeActionRange + 1f)
        {
            this.lastGapCloserSafety = "fixed dash would not re-engage";
            return false;
        }

        if (!this.bossModSafety.TryIsDashSafe(player.Position, destination, out var reason))
        {
            this.lastGapCloserSafety = reason;
            return false;
        }

        var used = ActionManager.Instance()->UseAction(ActionType.Action, actionId, player.GameObjectId);
        this.lastGapCloserSafety = used ? $"used {actionId}" : $"failed to use {actionId}";
        return used;
    }

    private unsafe bool TryUseNinjaShukuchi(IGameObject target)
    {
        var player = ObjectTable.LocalPlayer;
        if (player == null)
        {
            return false;
        }

        if (!CanUseAction(NinjaShukuchiActionId))
        {
            this.lastGapCloserSafety = "action unavailable";
            return false;
        }

        if (!this.TryFindSafeShukuchiDestination(player.Position, target.Position, target.HitboxRadius, out var destination))
        {
            return false;
        }

        var actionManager = ActionManager.Instance();
        var location = destination;
        var used = actionManager->UseActionLocation(ActionType.Action, NinjaShukuchiActionId, player.GameObjectId, &location);
        this.lastGapCloserSafety = used ? "used Shukuchi" : "failed to use Shukuchi";
        return used;
    }

    private bool TryFindSafeShukuchiDestination(Vector3 playerPosition, Vector3 targetPosition, float targetHitboxRadius, out Vector3 destination)
    {
        foreach (var candidate in this.EnumerateShukuchiCandidates(playerPosition, targetPosition, targetHitboxRadius))
        {
            if (Vector3.Distance(playerPosition, candidate) > GapCloserMaxRange)
            {
                continue;
            }

            if (this.bossModSafety.TryIsDashSafe(playerPosition, candidate, out var reason))
            {
                destination = candidate;
                this.lastGapCloserSafety = "safe Shukuchi destination found";
                return true;
            }

            this.lastGapCloserSafety = reason;
        }

        destination = default;
        if (string.IsNullOrEmpty(this.lastGapCloserSafety) || this.lastGapCloserSafety == "safe Shukuchi destination found")
        {
            this.lastGapCloserSafety = "no safe Shukuchi destination";
        }

        return false;
    }

    private IEnumerable<Vector3> EnumerateShukuchiCandidates(Vector3 playerPosition, Vector3 targetPosition, float targetHitboxRadius)
    {
        var radius = targetHitboxRadius + GapCloserDestinationMeleeRange;
        var toTarget = targetPosition - playerPosition;
        toTarget.Y = 0;
        if (toTarget.LengthSquared() > 0.0001f)
        {
            var direction = Vector3.Normalize(toTarget);
            yield return new Vector3(targetPosition.X - (direction.X * radius), playerPosition.Y, targetPosition.Z - (direction.Z * radius));
        }

        for (var i = 0; i < 16; i++)
        {
            var angle = i * (MathF.Tau / 16f);
            yield return new Vector3(
                targetPosition.X + MathF.Cos(angle) * radius,
                playerPosition.Y,
                targetPosition.Z + MathF.Sin(angle) * radius);
        }
    }

    private static bool IsTankJob(uint classJobId)
        => classJobId is 1 or 19 or 3 or 21 or 32 or 37;

    private static bool TryFindTankPosition(out Vector3 tankPosition)
    {
        foreach (var member in PartyList)
        {
            if (IsTankJob(member.ClassJob.RowId) && member.GameObject is { } go && !go.IsDead)
            {
                tankPosition = go.Position;
                return true;
            }
        }

        tankPosition = default;
        return false;
    }

    private static IBattleNpc? FindTrashGapCloserTarget(IBattleChara player)
    {
        var hasTank = TryFindTankPosition(out var tankPos);

        IBattleNpc? best = null;
        var bestScore = float.MaxValue;

        foreach (var obj in ObjectTable.OfType<IBattleNpc>())
        {
            if (obj.BattleNpcKind != BattleNpcSubKind.Combatant) continue;
            if (!obj.StatusFlags.HasFlag(StatusFlags.InCombat)) continue;
            var d = DistanceToHitbox(player.Position, player.HitboxRadius, obj.Position, obj.HitboxRadius);
            if (d <= MeleeActionRange || d > GapCloserMaxRange) continue;

            var score = hasTank ? Vector3.Distance(obj.Position, tankPos) : d;
            if (score < bestScore) { best = obj; bestScore = score; }
        }

        return best;
    }

    private IEnumerable<IBattleChara> EnumerateFriendlyEscapeTargets(IBattleChara player, float maxRange)
    {
        return ObjectTable
            .OfType<IBattleChara>()
            .Where(ally =>
                ally.ObjectKind == ObjectKind.Pc &&
                ally.GameObjectId != player.GameObjectId &&
                ally.GameObjectId != 0 &&
                !ally.IsDead &&
                ally.CurrentHp > 0 &&
                Vector3.Distance(player.Position, ally.Position) <= maxRange)
            .OrderByDescending(ally => Vector3.Distance(player.Position, ally.Position));
    }

    private IEnumerable<Vector3> EnumerateEscapeLocationCandidates(Vector3 playerPosition, float maxRange)
    {
        foreach (var radius in EscapeLocationRadii)
        {
            if (radius > maxRange)
            {
                continue;
            }

            for (var i = 0; i < 16; i++)
            {
                var angle = i * (MathF.Tau / 16f);
                yield return new Vector3(
                    playerPosition.X + MathF.Cos(angle) * radius,
                    playerPosition.Y,
                    playerPosition.Z + MathF.Sin(angle) * radius);
            }
        }
    }

    private static unsafe bool CanUseAction(uint actionId)
    {
        var actionManager = ActionManager.Instance();
        return actionManager->GetActionStatus(ActionType.Action, actionId) == 0 &&
               actionManager->GetCurrentCharges(actionId) > 0;
    }

    private static bool TryCalculateTargetDashDestination(Vector3 playerPosition, Vector3 targetPosition, float distanceToHitbox, out Vector3 destination)
    {
        var direction = targetPosition - playerPosition;
        direction.Y = 0;
        if (direction.LengthSquared() <= 0.0001f)
        {
            destination = default;
            return false;
        }

        direction = Vector3.Normalize(direction);
        destination = playerPosition + direction * Math.Max(0f, distanceToHitbox);
        return true;
    }

    private static float DistanceToHitbox(Vector3 from, float fromHitboxRadius, Vector3 to, float toHitboxRadius)
    {
        var delta = to - from;
        delta.Y = 0;
        return delta.Length() - fromHitboxRadius - toHitboxRadius;
    }

    private static Vector3 RotationToDirection(float rotation)
    {
        var (sin, cos) = MathF.SinCos(rotation);
        return new Vector3(sin, 0f, cos);
    }

    private float CalculateDesiredRange()
    {
        var rangeRole = GetCurrentRangeRole();
        if (this.config.AoERangeInMultiTarget && TargetManager.Target != null)
        {
            var enemyCount = ObjectFunctions.GetAttackableEnemyCountAroundPoint(TargetManager.Target.Position, Configuration.EnemyCountRadius);
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

    private bool IsAoEMultiTargetActive()
    {
        if (!this.config.AoERangeInMultiTarget || TargetManager.Target == null) return false;
        return ObjectFunctions.GetAttackableEnemyCountAroundPoint(
            TargetManager.Target.Position, Configuration.EnemyCountRadius) > this.config.AoEEnemyThreshold;
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

    private static bool IsOutsideMeleeRange()
    {
        var player = ObjectTable.LocalPlayer;
        var target = TargetManager.Target;
        if (player == null || target == null) return false;
        return Vector3.Distance(player.Position, target.Position) - player.HitboxRadius - target.HitboxRadius > MeleeActionRange;
    }

    private static bool HasTrueNorthCoverage()
    {
        return HasActiveTrueNorth() || GetTrueNorthCharges() > 0;
    }

    private static bool HasActiveTrueNorth()
    {
        return ObjectTable.LocalPlayer?.StatusList.Any(status => status.StatusId == TrueNorthStatusId && status.RemainingTime > 0) == true;
    }

    private static bool HasActiveCircleOfPower()
    {
        return ObjectTable.LocalPlayer?.StatusList.Any(status => status.StatusId == CircleOfPowerStatusId && status.RemainingTime > 0) == true;
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
                this.Print($"Enabled={this.config.Enabled}, Dependencies={(this.GetDependencyWarning() ?? "OK")}, TrueNorthManagement={(this.GetTrueNorthWarning() ?? this.rsrTrueNorthDisabled?.ToString() ?? "NotManaged")}, Preset={BossModIpc.DefaultPresetName}, LastPositional={this.lastPositional}, TrueNorthCharges={GetTrueNorthCharges()}, TrueNorthActive={HasActiveTrueNorth()}, Range={this.lastRange:0.0}, Movement={this.lastMovement}, MovementRange={this.lastMovementRangeStrategy}, Cushion={this.lastForbiddenZoneCushion}, Role={this.lastPartyRole}, LeylinesBTL={this.lastLeylinesBetweenTheLines}, LeylinesRetrace={this.lastLeylinesRetrace}, LeylinesGoal={this.lastLeylinesGoal}, BmrGapMNK={this.lastMonkThunderclap}, BmrGapDRG={this.lastDragoonWingedGlide}, BmrGapNIN={this.lastNinjaShukuchi}, BmrGapVPR={this.lastViperSlither}, GapPLD={this.config.GapCloserPLD}, GapWAR={this.config.GapCloserWAR}, GapDRK={this.config.GapCloserDRK}, GapGNB={this.config.GapCloserGNB}, GapSAM={this.config.GapCloserSAM}, GapRPR={this.config.GapCloserRPR}, EscapeGapMNK={this.config.EscapeGapCloserMNK}, EscapeGapNIN={this.config.EscapeGapCloserNIN}, EscapeGapRPR={this.config.EscapeGapCloserRPR}, EscapeGapVPR={this.config.EscapeGapCloserVPR}, EscapeGapBLM={this.config.EscapeGapCloserBLM}, EscapeGapSGE={this.config.EscapeGapCloserSGE}, EscapeGapPCT={this.config.EscapeGapCloserPCT}, EscapeGapBLU={this.config.EscapeGapCloserBLU}, ReflectedGapSafety={this.bossModSafety.Status}, LastGapCloser={this.lastGapCloserSafety}, LastEscapeGapCloser={this.lastEscapeGapCloserSafety}, Initialized={this.initializedPreset}");
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
        if (this.dtrEntry == null)
        {
            return;
        }

        this.dtrEntry.Text = $"XCAI: {(this.config.Enabled ? "On" : "Off")}";
        var dependencyWarning = this.GetDependencyWarning();
        var trueNorthWarning = this.GetTrueNorthWarning();
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

    private void ResetRuntimeCache()
    {
        this.initializedPreset = false;
        this.lastPositional = Positional.Any;
        this.lastRange = -1f;
        this.lastMovement = null;
        this.lastMovementRangeStrategy = null;
        this.lastForbiddenZoneCushion = null;
        this.lastPartyRole = null;
        this.lastLeylinesBetweenTheLines = null;
        this.lastLeylinesRetrace = null;
        this.lastLeylinesGoal = null;
        this.lastMonkThunderclap = null;
        this.lastDragoonWingedGlide = null;
        this.lastNinjaShukuchi = null;
        this.lastViperSlither = null;
        this.lastHealerStayNearParty = null;
        this.lastHealerHeal = null;
        this.lastHealerEsuna = null;
        this.lastHealerOutOfCombat = null;
        this.lastHealerRaise = null;
        this.rsrTrueNorthDisabled = null;
        this.trueNorthStrategy = null;
        this.nextGapCloserAttempt = DateTime.MinValue;
        this.nextEscapeGapCloserAttempt = DateTime.MinValue;
        this.lastGapCloserSafety = "not checked";
        this.lastEscapeGapCloserSafety = "not checked";
        this.bossModSafety.Reset();
    }

    private bool TrySetEnabled(bool enabled, bool warn = true)
    {
        if (enabled && !this.DependenciesAvailable(out var missing))
        {
            this.config.Enabled = true;
            this.ResetRuntimeCache();
            this.SaveConfig();
            if (warn)
            {
                Log.Verbose($"XCAI enabled while waiting for dependencies: {missing}.");
            }

            return true;
        }

        this.config.Enabled = enabled;
        if (!enabled)
        {
            this.DeactivateBossModPreset();
            this.ResetRuntimeCache();
        }

        this.SaveConfig();
        this.Print(this.config.Enabled ? "Enabled." : "Disabled.");
        return true;
    }

    private void WaitForDependencies(string missing)
    {
        if (this.initializedPreset)
        {
            this.DeactivateBossModPreset();
        }

        this.initializedPreset = false;
        if (!string.Equals(this.lastMissingDependencies, missing, StringComparison.Ordinal))
        {
            this.lastMissingDependencies = missing;
            Log.Verbose($"XCAI waiting for dependencies: {missing}.");
            this.UpdateDtr();
        }
    }

    private void ClearDependencyWaitState()
    {
        if (this.lastMissingDependencies == null)
        {
            return;
        }

        this.lastMissingDependencies = null;
        this.UpdateDtr();
    }

    private string? GetDependencyWarning()
    {
        return this.DependenciesAvailable(out var missing) ? null : missing;
    }

    private string? GetTrueNorthWarning()
    {
        if (!this.config.ManagePositionals || !this.config.ManageTrueNorth)
        {
            return null;
        }

        if (!this.IsRotationSolverAvailable())
        {
            return "Manage True North is enabled, but Rotation Solver Reborn is not loaded or its IPC is unavailable. XCAI will continue without disabling RSR Auto True North.";
        }

        if (this.rsrTrueNorthDisabled == false)
        {
            return "Manage True North is enabled, but XCAI could not disable Rotation Solver Reborn Auto True North.";
        }

        return null;
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

    private bool IsRotationSolverAvailable()
    {
        return this.rotationSolver.IsAvailable(PluginInterface);
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

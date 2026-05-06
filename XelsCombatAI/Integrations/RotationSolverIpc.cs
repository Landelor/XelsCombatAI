using System;
using System.Linq;
using System.Reflection;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using ECommons.EzIpcManager;

namespace XelsCombatAI.Integrations;

internal enum StateCommandType : byte
{
    Off        = 0,
    Auto       = 1,
    TargetOnly = 2,
    Manual     = 3,
    AutoDuty   = 4,
    Henched    = 5,
    PvP        = 6,
}

internal sealed class RotationSolverIpc
{
    private const string InternalName = "RotationSolver";
    private const string IpcPrefix = "RotationSolverReborn";
    private const string DataCenterTypeName = "RotationSolver.Basic.DataCenter";
    private const string DisableTrueNorthCommand = "AutoUseTrueNorth False";

    [EzIPC("OtherCommand")]
    private readonly Action<OtherCommandType, string> otherCommand = null!;

    [EzIPC("ChangeOperatingMode")]
    private readonly Action<StateCommandType> changeOperatingMode = null!;

    private Type? dataCenterType;
    private PropertyInfo? stateProp;
    private PropertyInfo? isManualProp;
    private PropertyInfo? isHenchedProp;

    public RotationSolverIpc()
    {
        EzIPC.Init(this, IpcPrefix);
    }

    public bool IsAvailable(IDalamudPluginInterface pluginInterface)
    {
        return pluginInterface.InstalledPlugins.Any(plugin =>
            plugin.IsLoaded &&
            (string.Equals(plugin.InternalName, InternalName, StringComparison.OrdinalIgnoreCase) ||
             string.Equals(plugin.Name, "Rotation Solver Reborn", StringComparison.OrdinalIgnoreCase)));
    }

    public void DisableAutoTrueNorth()
    {
        this.otherCommand(OtherCommandType.Settings, DisableTrueNorthCommand);
    }

    public void SetHenched()
    {
        this.changeOperatingMode(StateCommandType.Henched);
    }

    public void RestoreMode(StateCommandType mode)
    {
        this.changeOperatingMode(mode);
    }

    public StateCommandType? TryGetCurrentState(IPluginLog log)
    {
        try
        {
            if (!this.EnsureDataCenterResolved())
            {
                return null;
            }

            var isHenched = this.isHenchedProp!.GetValue(null) as bool? ?? false;
            if (isHenched) return StateCommandType.Henched;

            var isManual = this.isManualProp!.GetValue(null) as bool? ?? false;
            if (isManual) return StateCommandType.Manual;

            var state = this.stateProp!.GetValue(null) as bool? ?? false;
            if (state) return StateCommandType.Auto;

            return StateCommandType.Off;
        }
        catch (Exception ex)
        {
            log.Verbose($"Could not read RSR DataCenter state: {ex.Message}");
            return null;
        }
    }

    private bool EnsureDataCenterResolved()
    {
        if (this.dataCenterType != null)
        {
            return true;
        }

        foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
        {
            Type? type;
            try { type = assembly.GetType(DataCenterTypeName, throwOnError: false); }
            catch { continue; }

            if (type == null) continue;

            const BindingFlags StaticFlags = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
            var state    = type.GetProperty("State",     StaticFlags);
            var isManual = type.GetProperty("IsManual",  StaticFlags);
            var isHenched = type.GetProperty("IsHenched", StaticFlags);

            if (state == null || isManual == null || isHenched == null) continue;

            this.dataCenterType  = type;
            this.stateProp       = state;
            this.isManualProp    = isManual;
            this.isHenchedProp   = isHenched;
            return true;
        }

        return false;
    }

    private enum OtherCommandType : byte
    {
        Settings,
        Rotations,
        DutyRotations,
        DoActions,
        ToggleActions,
        NextAction,
        Cycle
    }
}

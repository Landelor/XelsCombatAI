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

    private readonly IDalamudPluginInterface pluginInterface;

    private Type? dataCenterType;
    private PropertyInfo? stateProp;
    private PropertyInfo? isManualProp;
    private PropertyInfo? isAutoDutyProp;
    private PropertyInfo? isHenchedProp;
    private PropertyInfo? isTargetOnlyProp;
    private PropertyInfo? isPvpStateEnabledProp;
    private PropertyInfo? specialTypeProp;

    public RotationSolverIpc(IDalamudPluginInterface pluginInterface)
    {
        this.pluginInterface = pluginInterface;
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

    public bool IsNoCasting(IPluginLog log)
    {
        try
        {
            if (!this.EnsureDataCenterResolved() || this.specialTypeProp == null)
                return false;
            var value = this.specialTypeProp.GetValue(null);
            // SpecialCommandType.NoCasting == 13
            return value != null && Convert.ToByte(value) == 13;
        }
        catch (Exception ex)
        {
            log.Verbose($"Could not read RSR SpecialType: {ex.Message}");
            return false;
        }
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

            var isAutoDuty = this.isAutoDutyProp?.GetValue(null) as bool? ?? false;
            if (isAutoDuty) return StateCommandType.AutoDuty;

            var isPvp = this.isPvpStateEnabledProp?.GetValue(null) as bool? ?? false;
            if (isPvp) return StateCommandType.PvP;

            var isTargetOnly = this.isTargetOnlyProp?.GetValue(null) as bool? ?? false;
            if (isTargetOnly) return StateCommandType.TargetOnly;

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

        // Resolve DataCenter through the live RSR plugin instance to avoid picking up
        // stale types from a previous plugin load still lingering in the AppDomain.
        // DataCenter lives in RotationSolver.Basic.dll (a separate assembly from the
        // main RotationSolver.dll). We find the main plugin, then locate the Basic
        // assembly it references by name to guarantee we get the live version.
        var plugin = ReflectionObjectSearch.FindLoadedPlugin(
            this.pluginInterface,
            "RotationSolver.RotationSolverPlugin",
            maxDepth: 2,
            "RotationSolver",
            "Rotation Solver Reborn");
        if (plugin == null)
            return false;

        var pluginAssembly = plugin.GetType().Assembly;
        System.Reflection.Assembly? basicAssembly = null;
        foreach (var refName in pluginAssembly.GetReferencedAssemblies())
        {
            if (!string.Equals(refName.Name, "RotationSolver.Basic", StringComparison.Ordinal))
                continue;
            foreach (var loaded in AppDomain.CurrentDomain.GetAssemblies())
            {
                if (loaded.GetName().Name == refName.Name && loaded.GetName().Version == refName.Version)
                {
                    basicAssembly = loaded;
                    break;
                }
            }
            break;
        }
        if (basicAssembly == null)
            return false;

        Type? type;
        try { type = basicAssembly.GetType(DataCenterTypeName, throwOnError: false); }
        catch { return false; }
        if (type == null) return false;

        const BindingFlags StaticFlags = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
        var state    = type.GetProperty("State",     StaticFlags);
        var isManual = type.GetProperty("IsManual",  StaticFlags);
        var isAutoDuty = type.GetProperty("IsAutoDuty", StaticFlags);
        var isHenched = type.GetProperty("IsHenched", StaticFlags);
        var isTargetOnly = type.GetProperty("IsTargetOnly", StaticFlags);
        var isPvpStateEnabled = type.GetProperty("IsPvPStateEnabled", StaticFlags);

        if (state == null || isManual == null || isHenched == null) return false;

        this.dataCenterType        = type;
        this.stateProp             = state;
        this.isManualProp          = isManual;
        this.isAutoDutyProp        = isAutoDuty;
        this.isHenchedProp         = isHenched;
        this.isTargetOnlyProp      = isTargetOnly;
        this.isPvpStateEnabledProp = isPvpStateEnabled;
        this.specialTypeProp       = type.GetProperty("SpecialType", StaticFlags);
        return true;
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

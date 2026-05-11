using System;
using System.Linq;
using System.Reflection;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using ECommons.EzIpcManager;

namespace XelsCombatAI.Integrations;

internal enum StateCommandType : byte
{
    Off = 0,
    Auto = 1,
    TargetOnly = 2,
    Manual = 3,
    AutoDuty = 4,
    Henched = 5,
    PvP = 6,
}

internal sealed class RotationSolverIpc
{
    private const string InternalName = "RotationSolver";
    private const string IpcPrefix = "RotationSolverReborn";
    private const string DataCenterTypeName = "RotationSolver.Basic.DataCenter";
    private const string DisableTrueNorthCommand = "AutoUseTrueNorth False";

    [EzIPC("OtherCommand")]
    private Action<OtherCommandType, string> otherCommand = static (_, _) => throw new InvalidOperationException("Rotation Solver Reborn IPC is not initialized.");

    [EzIPC("ChangeOperatingMode")]
    private Action<StateCommandType> changeOperatingMode = static _ => throw new InvalidOperationException("Rotation Solver Reborn IPC is not initialized.");

    private readonly IDalamudPluginInterface pluginInterface;
    private readonly IPluginLog log;

    private Type? dataCenterType;
    private PropertyInfo? stateProp;
    private PropertyInfo? isManualProp;
    private PropertyInfo? isAutoDutyProp;
    private PropertyInfo? isHenchedProp;
    private PropertyInfo? isTargetOnlyProp;
    private PropertyInfo? isPvpStateEnabledProp;
    private PropertyInfo? specialTypeProp;
    private DateTime nextFailureLog = DateTime.MinValue;

    public RotationSolverIpc(IDalamudPluginInterface pluginInterface, IPluginLog log)
    {
        this.pluginInterface = pluginInterface;
        this.log = log;
        try
        {
            EzIPC.Init(this, IpcPrefix);
        }
        catch (Exception ex)
        {
            // RSR is optional. If its IPC is unavailable during plugin-manager churn,
            // callers treat the integration as temporarily unavailable.
            this.LogRecoverableFailure(ex, "Could not initialize Rotation Solver Reborn IPC.");
        }
    }

    public bool IsAvailable(IDalamudPluginInterface pluginInterface)
    {
        return ReflectionObjectSearch.HasLoadedPlugin(
            pluginInterface,
            InternalName,
            "RotationSolverReborn",
            "Rotation Solver Reborn");
    }

    public bool DisableAutoTrueNorth()
    {
        return this.TryInvoke(() => this.otherCommand(OtherCommandType.Settings, DisableTrueNorthCommand));
    }

    public bool SetHenched()
    {
        return this.TryInvoke(() => this.changeOperatingMode(StateCommandType.Henched));
    }

    public bool RestoreMode(StateCommandType mode)
    {
        return this.TryInvoke(() => this.changeOperatingMode(mode));
    }

    public bool IsNoCasting(IPluginLog log)
    {
        try
        {
            if (!this.EnsureDataCenterResolved() || this.specialTypeProp == null)
            {
                return false;
            }

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
            if (isHenched)
            {
                return StateCommandType.Henched;
            }

            var isAutoDuty = this.isAutoDutyProp?.GetValue(null) as bool? ?? false;
            if (isAutoDuty)
            {
                return StateCommandType.AutoDuty;
            }

            var isPvp = this.isPvpStateEnabledProp?.GetValue(null) as bool? ?? false;
            if (isPvp)
            {
                return StateCommandType.PvP;
            }

            var isTargetOnly = this.isTargetOnlyProp?.GetValue(null) as bool? ?? false;
            if (isTargetOnly)
            {
                return StateCommandType.TargetOnly;
            }

            var isManual = this.isManualProp!.GetValue(null) as bool? ?? false;
            if (isManual)
            {
                return StateCommandType.Manual;
            }

            var state = this.stateProp!.GetValue(null) as bool? ?? false;
            if (state)
            {
                return StateCommandType.Auto;
            }

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
        if (!this.IsAvailable(this.pluginInterface))
        {
            this.ResetReflectionCache();
            return false;
        }

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
        {
            return false;
        }

        var pluginAssembly = plugin.GetType().Assembly;
        Assembly? basicAssembly = null;
        foreach (var refName in pluginAssembly.GetReferencedAssemblies())
        {
            if (!string.Equals(refName.Name, "RotationSolver.Basic", StringComparison.Ordinal))
            {
                continue;
            }

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
        {
            return false;
        }

        Type? type;
        try
        {
            type = basicAssembly.GetType(DataCenterTypeName, throwOnError: false);
        }
        catch (Exception ex)
        {
            this.LogRecoverableFailure(ex, "Could not resolve Rotation Solver Reborn DataCenter type.");
            return false;
        }

        if (type == null)
        {
            return false;
        }

        const BindingFlags StaticFlags = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
        var state = type.GetProperty("State", StaticFlags);
        var isManual = type.GetProperty("IsManual", StaticFlags);
        var isAutoDuty = type.GetProperty("IsAutoDuty", StaticFlags);
        var isHenched = type.GetProperty("IsHenched", StaticFlags);
        var isTargetOnly = type.GetProperty("IsTargetOnly", StaticFlags);
        var isPvpStateEnabled = type.GetProperty("IsPvPStateEnabled", StaticFlags);

        if (state == null || isManual == null || isHenched == null)
        {
            return false;
        }

        this.dataCenterType = type;
        this.stateProp = state;
        this.isManualProp = isManual;
        this.isAutoDutyProp = isAutoDuty;
        this.isHenchedProp = isHenched;
        this.isTargetOnlyProp = isTargetOnly;
        this.isPvpStateEnabledProp = isPvpStateEnabled;
        this.specialTypeProp = type.GetProperty("SpecialType", StaticFlags);
        return true;
    }

    private bool TryInvoke(Action action)
    {
        try
        {
            if (!this.IsAvailable(this.pluginInterface))
            {
                this.ResetReflectionCache();
                return false;
            }

            action();
            return true;
        }
        catch (Exception ex)
        {
            this.LogRecoverableFailure(ex, "Rotation Solver Reborn IPC invocation failed.");
            this.ResetReflectionCache();
            return false;
        }
    }

    private void LogRecoverableFailure(Exception ex, string message)
    {
        var now = DateTime.UtcNow;
        if (now < this.nextFailureLog)
        {
            return;
        }

        this.log.Verbose(ex, message);
        this.nextFailureLog = now.AddSeconds(10);
    }

    private void ResetReflectionCache()
    {
        this.dataCenterType = null;
        this.stateProp = null;
        this.isManualProp = null;
        this.isAutoDutyProp = null;
        this.isHenchedProp = null;
        this.isTargetOnlyProp = null;
        this.isPvpStateEnabledProp = null;
        this.specialTypeProp = null;
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

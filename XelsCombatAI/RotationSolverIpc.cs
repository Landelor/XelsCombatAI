using System;
using System.Linq;
using Dalamud.Plugin;
using ECommons.EzIpcManager;

namespace XelsCombatAI;

internal sealed class RotationSolverIpc
{
    private const string InternalName = "RotationSolver";
    private const string IpcPrefix = "RotationSolverReborn";
    private const string DisableTrueNorthCommand = "AutoUseTrueNorth False";

    [EzIPC("OtherCommand")]
    private readonly Action<OtherCommandType, string> otherCommand = null!;

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

using System.Collections.Generic;

namespace XelsCombatAI.Integrations;

internal sealed class DependencyChecker(
    Configuration config,
    DalamudServices services,
    BossModIpc bossMod,
    RotationSolverIpc rotationSolver)
{
    public bool DependenciesAvailable(out string missing)
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

    public string? GetDependencyWarning()
    {
        return this.DependenciesAvailable(out var missing) ? null : missing;
    }

    public string? GetTrueNorthWarning(bool? rsrTrueNorthDisabled)
    {
        if (!config.ManagePositionals || !config.ManageTrueNorth)
        {
            return null;
        }

        if (!this.IsRotationSolverAvailable())
        {
            return "Manage True North is enabled, but Rotation Solver Reborn is not loaded or its IPC is unavailable. XCAI will continue without disabling RSR Auto True North.";
        }

        if (rsrTrueNorthDisabled == false)
        {
            return "Manage True North is enabled, but XCAI could not disable Rotation Solver Reborn Auto True North.";
        }

        return null;
    }

    public bool IsBossModAvailable()
    {
        return services.HasLoadedPlugin("BossModReborn", "BossMod Reborn", "BossMod") && bossMod.IsAvailable();
    }

    public bool IsAvariceAvailable()
    {
        return services.HasLoadedPlugin("Avarice");
    }

    public bool IsRotationSolverAvailable()
    {
        return rotationSolver.IsAvailable(services.PluginInterface);
    }
}

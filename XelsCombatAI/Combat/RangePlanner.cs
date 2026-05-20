using System;
namespace XelsCombatAI.Combat;

internal sealed class TargetUptimePlanner(DalamudServices services, BossModIpc bossMod)
{
    public Func<float?> TargetUptimeRangeOverride { get; set; } = () => null;

    public float CalculateTargetUptimeRange()
    {
        var overrideRange = this.TargetUptimeRangeOverride();
        if (overrideRange.HasValue)
        {
            return overrideRange.Value;
        }

        return Configuration.InternalDisabledUptimeRange;
    }

    public bool CurrentTargetHasBossModule()
    {
        var dataId = services.TargetManager.Target?.BaseId ?? 0;
        if (dataId == 0)
        {
            return false;
        }

        try
        {
            return bossMod.HasModuleByDataId(dataId);
        }
        catch (Exception ex)
        {
            services.Log.Verbose(ex, "Could not query BossMod module state yet.");
            return false;
        }
    }
}

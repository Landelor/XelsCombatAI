using System;
using ECommons.GameFunctions;

namespace XelsCombatAI.Combat;

internal sealed class RangePlanner(Configuration config, DalamudServices services, BossModIpc bossMod)
{
    public float CalculateDesiredRange()
    {
        var rangeRole = this.GetCurrentRangeRole();
        if (config.AoERangeInMultiTarget && services.TargetManager.Target != null)
        {
            var enemyCount = ObjectFunctions.GetAttackableEnemyCountAroundPoint(services.TargetManager.Target.Position, Configuration.EnemyCountRadius);
            if (enemyCount > config.AoEEnemyThreshold)
            {
                var classJobId = services.ObjectTable.LocalPlayer?.ClassJob.RowId ?? 0;
                if (rangeRole == RangeRole.Melee || (config.AoEHealerMeleeRange && JobRoles.IsMeleeAoEHealer(classJobId)))
                    return config.AoEMeleeRange;
                if (!config.RoleBasedRange)
                    return config.AoERangedRange;
                return rangeRole switch
                {
                    RangeRole.PhysicalRanged => config.AoEPhysicalRangedRange,
                    RangeRole.Healer => config.AoEHealerRange,
                    RangeRole.MagicRanged => config.AoEMagicRangedRange,
                    _ => config.AoERangedRange
                };
            }
        }

        if (!config.RoleBasedRange)
        {
            return config.MeleeRange;
        }

        return rangeRole switch
        {
            RangeRole.PhysicalRanged => config.PhysicalRangedRange,
            RangeRole.Healer => config.HealerRange,
            RangeRole.MagicRanged => config.MagicRangedRange,
            _ => config.MeleeRange
        };
    }

    public bool IsAoEMultiTargetActive()
    {
        if (!config.AoERangeInMultiTarget || services.TargetManager.Target == null) return false;
        return ObjectFunctions.GetAttackableEnemyCountAroundPoint(
            services.TargetManager.Target.Position, Configuration.EnemyCountRadius) > config.AoEEnemyThreshold;
    }

    public float CalculateHealerCoverageRange()
    {
        var classJobId = services.ObjectTable.LocalPlayer?.ClassJob.RowId ?? 0;
        if (this.IsAoEMultiTargetActive() &&
            config.AoEHealerMeleeRange &&
            JobRoles.IsMeleeAoEHealer(classJobId))
        {
            return config.AoEMeleeRange;
        }

        return CombatConstants.HealerCoverageAttackRange;
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

    public RangeRole GetCurrentRangeRole()
    {
        return JobRoles.GetRangeRole(services.ObjectTable.LocalPlayer);
    }
}

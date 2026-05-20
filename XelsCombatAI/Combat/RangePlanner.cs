using System;
using Dalamud.Game.ClientState.Objects.Enums;
using Dalamud.Game.ClientState.Objects.Types;
using XelsCombatAI.Game;

namespace XelsCombatAI.Combat;

internal sealed class TargetUptimePlanner(
    DalamudServices services,
    BossModIpc bossMod,
    JobRangeProvider jobRangeProvider,
    RotationSolverActionReflection rotationSolverActions)
{
    public Func<float?> TargetUptimeRangeOverride { get; set; } = () => null;

    public float CalculateTargetUptimeRange()
    {
        var overrideRange = this.TargetUptimeRangeOverride();
        if (overrideRange.HasValue)
        {
            return overrideRange.Value;
        }

        if (!this.HasUsableHostileTarget())
        {
            return Configuration.InternalDisabledUptimeRange;
        }

        var rangeRole = JobRoles.GetRangeRole(services.ObjectTable.LocalPlayer);
        if (rotationSolverActions.TryGetUpcomingGcd(requirePreview: false, out var action, out _) &&
            !action.IsFriendly)
        {
            return ResolveTargetUptimeRange(rangeRole, jobRangeProvider.EngagementRange, action.Range);
        }

        return jobRangeProvider.EngagementRange;
    }

    internal static float ResolveTargetUptimeRange(RangeRole rangeRole, float jobEngagementRange, float actionRange)
    {
        var usableActionRange = actionRange > 0f
            ? Math.Clamp(actionRange, CombatConstants.MeleeActionRange, Configuration.InternalDisabledUptimeRange)
            : jobEngagementRange;

        return rangeRole == RangeRole.Melee
            ? MathF.Min(jobEngagementRange, usableActionRange)
            : usableActionRange;
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

    private bool HasUsableHostileTarget()
    {
        return services.TargetManager.Target is IBattleNpc target &&
               target.BattleNpcKind == BattleNpcSubKind.Combatant &&
               target.GameObjectId != 0 &&
               target.StatusFlags.HasFlag(StatusFlags.Hostile) &&
               !target.IsDead &&
               target.CurrentHp > 0;
    }
}
